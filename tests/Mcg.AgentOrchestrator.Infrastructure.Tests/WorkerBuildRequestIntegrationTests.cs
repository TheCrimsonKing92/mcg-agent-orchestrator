using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fixture owns its stores, slot locks and processes.
public sealed class WorkerBuildRequestIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OrdinaryHelperLowIntegrityPeerAndCompilerRetainSandbox()
    {
        using var fixture = new WorkerBuildRequestFixture(lowIntegrity: true);
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
        using var fixture = new WorkerBuildRequestFixture(invalidSource);
        var invocation = await fixture.RunHelper();
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
    }

    [Fact]
    public async Task CapacityAcquisitionEndingBeforeStart_HasNoOwnedCompiler()
    {
        using var fixture = new WorkerBuildRequestFixture();
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
        Assert.DoesNotContain(invocation.Observations, o => o.Phase == WorkerBuildPhase.PreparingLaunch);
        Assert.Null(invocation.CompilerExitCode);
        Assert.True(fixture.Consume().MissingEvidence);
    }

    [Fact]
    public async Task HostDeadlineAfterObservedCompilerStart_IsDistinctFromCapacityAndCompilerExit()
    {
        using var fixture = new WorkerBuildRequestFixture();
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
}
