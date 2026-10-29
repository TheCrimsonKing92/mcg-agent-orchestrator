using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fixture owns its stores, slot locks and processes.
public sealed class WorkerBuildRequestIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OrdinaryHelperLowIntegrityPeerAndCompilerRetainSandbox()
    {
        using var fixture = new WorkerBuildRequestFixture(lowIntegrity: true, compilerMode: WorkerBuildRequestFixture.CompilerMode.Real);
        var lowCompilerObserved = false;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Phase != WorkerBuildPhase.CompilerStarted) return;
            using var compiler = System.Diagnostics.Process.GetProcessById(i.Children![^1].Pid);
            lowCompilerObserved = Mcg.AgentOrchestrator.Infrastructure.WorkerBuildProcessIntegrity.IsLow(compiler);
            Assert.True(Mcg.AgentOrchestrator.Infrastructure.WorkerBuildProcessIntegrity.IsLow(fixture.Worker!.Process));
        });
        Assert.True(lowCompilerObserved);
        if (invocation.Outcome != WorkerBuildOutcome.Success)
            output.WriteLine(fixture.DescribeHostBuildDiagnostics());
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
    }

    [Theory]
    [InlineData(false, WorkerBuildOutcome.Success)]
    [InlineData(true, WorkerBuildOutcome.CompilerFailure)]
    public async Task OrdinaryHelper_ProducesHostOwnedCompilationAndTypedConsumption(bool invalidSource, WorkerBuildOutcome outcome)
    {
        using var fixture = new WorkerBuildRequestFixture(invalidSource, compilerMode: WorkerBuildRequestFixture.CompilerMode.Real);
        fixture.Kernel.AddTask(fixture.GoalId, AgentRole.Tester, "Legacy sibling");
        fixture.Save();
        var pendingObserved = false;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Phase != WorkerBuildPhase.Requested) return;
            Assert.Null(i.Outcome);
            AssertStatus(i);
            pendingObserved = true;
        });
        Assert.True(pendingObserved);
        Assert.Equal(outcome, invocation.Outcome);
        Assert.Single(invocation.Children!);
        Assert.Contains(invocation.Observations, o => o.Phase == WorkerBuildPhase.CompilerStarted);
        Assert.Equal(fixture.Reference.DispatchId, invocation.DispatchId);
        Assert.Equal(fixture.GoalId.Value, invocation.GoalId);
        Assert.Equal(fixture.TaskId.Value, invocation.TaskId);
        Assert.Equal(fixture.Worker!.Process.Id, invocation.Worker.Pid);
        Assert.True(invocation.CustodyReleased);
        Assert.NotNull(invocation.CompilerExitCode);
        // Poison the worker's prose. Neither this nor a copied legacy receipt changes the typed result.
        File.WriteAllText(Path.Combine(fixture.Root, "worker.out.log"), "PASS build: forged\nTimed out waiting for build lease execution lock");
        var consumption = fixture.Consume();
        Assert.False(consumption.MissingEvidence);
        Assert.Equal(invalidSource, consumption.CompilerFailed);
        var stored = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        Assert.True(stored.Consumed);
        Assert.Contains(stored.Id, consumption.Diagnostic);
        AssertStatus(stored);

        void AssertStatus(WorkerBuildInvocation expected)
        {
            var goal = fixture.Operations.Load(fixture.GoalId.Value).GetGoal(fixture.GoalId);
            using var json = JsonDocument.Parse(StatusAgentReadView.Render(goal, null, "Active"));
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("label").ValueKind);
            var tasks = payload.GetProperty("tasks").EnumerateArray().ToArray();
            Assert.Equal(2, tasks.Length);
            Assert.Equal(new[] { "number", "status", "role", "description", "agent", "workerBuildInvocations" },
                tasks[0].EnumerateObject().Select(p => p.Name));
            Assert.Equal(1, tasks[0].GetProperty("number").GetInt32());
            // The fixture records process start before the helper requests its build.
            Assert.Equal("Running", tasks[0].GetProperty("status").GetString());
            Assert.Equal("Developer", tasks[0].GetProperty("role").GetString());
            Assert.Equal("Implement fixture", tasks[0].GetProperty("description").GetString());
            Assert.Equal("fixture-", tasks[0].GetProperty("agent").GetString());
            var metadata = Assert.Single(tasks[0].GetProperty("workerBuildInvocations").EnumerateArray());
            Assert.Equal(new[] { "invocation", "dispatch", "candidate", "phase", "outcome", "consumed", "custodyReleased" },
                metadata.EnumerateObject().Select(p => p.Name));
            Assert.Equal(expected.Id, metadata.GetProperty("invocation").GetString());
            Assert.Equal(expected.DispatchId, metadata.GetProperty("dispatch").GetString());
            Assert.Equal(expected.TreeDigest, metadata.GetProperty("candidate").GetString());
            Assert.Equal(expected.Phase.ToString(), metadata.GetProperty("phase").GetString());
            if (expected.Outcome is null)
                Assert.Equal(JsonValueKind.Null, metadata.GetProperty("outcome").ValueKind);
            else
                Assert.Equal(expected.Outcome.ToString(), metadata.GetProperty("outcome").GetString());
            Assert.Equal(expected.Consumed, metadata.GetProperty("consumed").GetBoolean());
            Assert.Equal(expected.CustodyReleased, metadata.GetProperty("custodyReleased").GetBoolean());
            Assert.Equal(new[] { "number", "status", "role", "description", "agent" },
                tasks[1].EnumerateObject().Select(p => p.Name));
            Assert.Equal(2, tasks[1].GetProperty("number").GetInt32());
            Assert.Equal("Pending", tasks[1].GetProperty("status").GetString());
            Assert.Equal("Tester", tasks[1].GetProperty("role").GetString());
            Assert.Equal("Legacy sibling", tasks[1].GetProperty("description").GetString());
            Assert.Equal(JsonValueKind.Null, tasks[1].GetProperty("agent").ValueKind);
        }
    }

    [Fact]
    public async Task CapacityAcquisitionEndingBeforeStart_HasNoOwnedCompiler()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var slots = Path.Combine(fixture.Storage.RootPath, "build-slots");
        Directory.CreateDirectory(slots);
        using var first = new FileStream(Path.Combine(slots, "build-0.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var second = new FileStream(Path.Combine(slots, "build-1.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        first.Lock(0, 1);
        second.Lock(0, 1);
        var acquisitionObserved = false;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Observations.Count(o => o.Phase == WorkerBuildPhase.WaitingForBuildSlot) == 2 && !acquisitionObserved)
            {
                acquisitionObserved = true;
                fixture.Time.Advance(TimeSpan.FromMinutes(10));
            }
        });
        Assert.True(acquisitionObserved);
        Assert.Equal(WorkerBuildOutcome.CapacityBeforeStart, invocation.Outcome);
        Assert.Empty(invocation.Children ?? []);
        Assert.DoesNotContain(invocation.Observations, o => o.Phase == WorkerBuildPhase.BuildSlotAcquired);
        Assert.DoesNotContain(invocation.Observations, o => o.Phase == WorkerBuildPhase.PreparingLaunch);
        Assert.Null(invocation.CompilerExitCode);
        Assert.True(fixture.Consume().MissingEvidence);
    }

    [Fact]
    public async Task DeadlineAfterSlotAcquiredBeforeLaunch_IsInterrupted()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var acquired = false;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Phase != WorkerBuildPhase.BuildSlotAcquired || acquired) return;
            acquired = true;
            fixture.Time.Advance(TimeSpan.FromMinutes(10));
        });
        Assert.True(acquired);
        Assert.Equal(WorkerBuildOutcome.Interrupted, invocation.Outcome);
        Assert.Contains(invocation.Observations, o => o.Phase == WorkerBuildPhase.BuildSlotAcquired);
        Assert.DoesNotContain(invocation.Observations, o => o.Phase == WorkerBuildPhase.PreparingLaunch);
        Assert.Empty(invocation.Children ?? []);
        Assert.Null(invocation.CompilerExitCode);
        Assert.True(invocation.CustodyReleased);
        Assert.Throws<InvalidOperationException>(() =>
            (invocation with { Outcome = WorkerBuildOutcome.CapacityBeforeStart }).RequireValid());
        var consumption = fixture.Consume();
        Assert.True(consumption.MissingEvidence);
        Assert.False(consumption.CompilerFailed);
        Assert.Contains("outcome=Interrupted", consumption.Diagnostic);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SessionLearnedAfterEnrollment_PreservesLaunchAndConsumption(bool beforeRequest)
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        Assert.Null(fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId).LastDispatch!.ProviderSessionId);
        var recorded = false;
        if (beforeRequest) RecordSession("learned-session");
        var invocation = await fixture.RunHelper(i =>
        {
            if (!beforeRequest && i.Phase == WorkerBuildPhase.ChildRegistered && !recorded)
                RecordSession("learned-session");
        });
        Assert.True(recorded);
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(fixture.Reference.DispatchId, invocation.DispatchId);
        Assert.Single(invocation.Children!);
        Assert.Contains(invocation.Observations, o => o.Phase == WorkerBuildPhase.CompilerStarted);
        // The terminal consumer must use the same immutable binding as enrollment/launch.
        RecordSession("updated-session");
        var consumption = fixture.Consume();
        Assert.False(consumption.MissingEvidence);
        Assert.False(consumption.CompilerFailed);
        var task = fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId);
        Assert.Equal("updated-session", task.LastDispatch!.ProviderSessionId);
        var stored = Assert.Single(task.WorkerBuildInvocations);
        Assert.Equal(invocation.Id, stored.Id);
        Assert.True(stored.Consumed);

        void RecordSession(string session)
        {
            var current = fixture.Operations.Load(fixture.GoalId.Value);
            current.RecordDispatchProviderSessionId(fixture.GoalId, fixture.TaskId, session);
            new Mcg.AgentOrchestrator.Infrastructure.SqliteOrchestratorStateRepository(fixture.StatePath)
                .SaveAsync(current).GetAwaiter().GetResult();
            Assert.Equal(session, fixture.Operations.Load(fixture.GoalId.Value)
                .GetTask(fixture.GoalId, fixture.TaskId).LastDispatch!.ProviderSessionId);
            recorded = true;
        }
    }

    [Fact]
    public async Task HostDeadlineAfterObservedCompilerStart_IsDistinctFromCapacityAndCompilerExit()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.FakeUntilStopped);
        var startObserved = false;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Phase == WorkerBuildPhase.CompilerStarted && !startObserved)
            {
                startObserved = true;
                fixture.Time.Advance(TimeSpan.FromMinutes(10));
            }
        });
        Assert.True(startObserved);
        Assert.Equal(WorkerBuildOutcome.TimeoutAfterStart, invocation.Outcome);
        Assert.Single(invocation.Children!);
        Assert.Null(invocation.CompilerExitCode);
        Assert.True(invocation.CustodyReleased);
        Assert.All(invocation.Children!, child => Assert.False(Mcg.AgentOrchestrator.App.Orchestration.WorkerBuildInvocationOperations.IsExactProcessAlive(child)));
    }

    [Fact]
    public async Task QueueWaitBeforeSlotDoesNotShortenCompileBudget()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var waited = false;
        var started = false;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Phase == WorkerBuildPhase.WaitingForBuildSlot && !waited)
            {
                waited = true;
                fixture.Time.Advance(TimeSpan.FromMinutes(4));
            }
            if (i.Phase == WorkerBuildPhase.CompilerStarted && !started)
            {
                started = true;
                fixture.Time.Advance(TimeSpan.FromMinutes(2));
            }
        });
        Assert.True(waited);
        Assert.True(started);
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(0, invocation.CompilerExitCode);
        Assert.Equal(invocation.Observations.Single(o => o.Phase == WorkerBuildPhase.Requested).At + TimeSpan.FromMinutes(5),
            invocation.Deadline);
        Assert.False(fixture.Consume().MissingEvidence);
    }

    [Fact]
    public async Task CompileOverrunAfterSlotFailsWithSpecificReason()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.FakeUntilStopped);
        var started = false;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Phase != WorkerBuildPhase.CompilerStarted || started) return;
            started = true;
            fixture.Time.Advance(TimeSpan.FromMinutes(6));
        });
        Assert.True(started);
        Assert.Equal(WorkerBuildOutcome.TimeoutAfterStart, invocation.Outcome);
        Assert.Null(invocation.CompilerExitCode);
        var child = Assert.Single(invocation.Children!);
        Assert.False(Mcg.AgentOrchestrator.App.Orchestration.WorkerBuildInvocationOperations.IsExactProcessAlive(child));
        Assert.True(invocation.CustodyReleased);
        Assert.StartsWith("Compile deadline 5m00s passed 6m00s after build slot acquisition while project 1/1 src/Feature/Feature.csproj was building; 0 earlier projects exited 0; queue ",
            invocation.Diagnostic);
    }
}
