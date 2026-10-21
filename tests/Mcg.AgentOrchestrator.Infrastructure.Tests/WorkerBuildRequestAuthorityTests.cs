using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns canonical state and any native child.
public sealed class WorkerBuildRequestAuthorityTests
{
    [Fact]
    public async Task ForeignPipePeerCannotReachHostService()
    {
        using var fixture = new WorkerBuildRequestFixture();
        var observed = new TaskCompletionSource<(int Pid, bool Admitted)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CountingService();
        using var channel = new WorkerBuildRequestChannel(fixture.RepositoryRoot, service,
            (pid, admitted) => observed.TrySetResult((pid, admitted)));
        var ownerInfo = new ProcessStartInfo(WorkerShell.Executable) { UseShellExecute = false, CreateNoWindow = true };
        ownerInfo.ArgumentList.Add("-NoProfile");
        ownerInfo.ArgumentList.Add("-Command");
        ownerInfo.ArgumentList.Add("exit 0");
        // The legitimate owner is held suspended. No timing race can put the foreign peer in its job.
        using var owner = OwnedProcessGroup.StartSuspendedContained(ownerInfo);
        channel.Register(owner.Process, owner.Group);
        var start = new ProcessStartInfo(WorkerShell.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture.RepositoryRoot,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(WorkerBuildRequestFixture.FindRepository(), "scripts", "Request-HostWorkerBuild.ps1"),
            "-Projects", WorkerBuildRequestFixture.Project }) start.ArgumentList.Add(arg);
        var foreignPid = 0;
        var run = CliChildProcessRunner.RunAsync(start, started: p => { foreignPid = p.Id; p.StandardInput.Close(); });
        var peer = await observed.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(foreignPid, peer.Pid);
        Assert.False(peer.Admitted);
        Assert.Equal(0, service.Calls);
        Assert.NotEqual(0, result.ExitCode);
    }

    private sealed class CountingService : IWorkerBuildRequestService
    {
        internal int Calls;
        public Task<WorkerBuildResponse> ExecuteAsync(WorkerBuildRequest request, WorkerBuildProcess worker, CancellationToken workerLifetime)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new WorkerBuildResponse("forged", WorkerBuildOutcome.Success, "unexpected host admission"));
        }
    }

    [Theory]
    [InlineData("dispatch")]
    [InlineData("task")]
    [InlineData("goal")]
    [InlineData("admission")]
    public void WrongOriginCannotAuthorizeHost(string control)
    {
        using var fixture = new WorkerBuildRequestFixture();
        var reference = control switch
        {
            "dispatch" => fixture.Reference with { DispatchId = "forged-dispatch" },
            "task" => fixture.Reference with { TaskId = TaskId.New().Value },
            "goal" => fixture.Reference with { GoalId = GoalId.New().Value },
            _ => fixture.Reference with { Admission = null }
        };
        Assert.ThrowsAny<Exception>(() => new WorkerBuildRequestHost(reference));
        Assert.Empty(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
    }

    [Theory]
    [InlineData("pid", true)]
    [InlineData("start", true)]
    [InlineData("image", true)]
    [InlineData("pid", false)]
    [InlineData("start", false)]
    [InlineData("image", false)]
    public void WrongOrReusedHostProcessCannotAuthorizeLaunchOrConsumption(string control, bool requireRunning)
    {
        using var fixture = new WorkerBuildRequestFixture();
        using var current = Process.GetCurrentProcess();
        var identity = WorkerBuildRequestChannel.ReadIdentity(current);
        identity = control switch
        {
            "pid" => identity with { Pid = identity.Pid + 1 },
            "start" => identity with { StartedAt = identity.StartedAt.AddSeconds(-1) },
            _ => identity with { ImagePath = Path.Combine(fixture.Root, "unrelated.exe") }
        };
        Assert.Throws<UnauthorizedAccessException>(() => WorkerBuildInvocationOperations.RequireOrigin(
            fixture.Operations.Load(fixture.GoalId.Value), fixture.Reference, identity, requireRunning));
    }

    [Fact]
    public void MissingPersistedHostImageCannotAuthorizeAnEnrolledDispatch()
    {
        using var fixture = new WorkerBuildRequestFixture();
        var process = fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId).LastProcess!;
        fixture.Kernel.RecordTaskProcessStarted(fixture.GoalId, fixture.TaskId,
            process with { ProcessIdentityImagePath = null });
        fixture.Save();
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Host());
    }

    [Fact]
    public void UnstartedSuppressionCannotReplaceCanonicalHostImage()
    {
        using var fixture = new WorkerBuildRequestFixture();
        var task = fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId);
        var process = task.LastProcess!;
        var unrelated = process with { CompletedAt = DateTimeOffset.UtcNow,
            ProcessIdentityImagePath = Path.Combine(fixture.Root, "unrelated.exe") };
        Assert.Throws<InvalidOperationException>(() => fixture.Kernel.DiscardUnstartedDispatch(
            fixture.GoalId, fixture.TaskId, unrelated, "fixture suppression"));
        Assert.Same(process, task.LastProcess);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("image")]
    public async Task ChangedCanonicalHostCannotConsumeGenuineCompilation(string component)
    {
        using var fixture = new WorkerBuildRequestFixture();
        var invocation = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        var process = fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId).LastProcess!;
        var replacement = component switch
        {
            "pid" => process with { ProcessId = process.ProcessId + 1 },
            "start" => process with { ProcessIdentityStartedAt = process.ProcessIdentityStartedAt!.Value.AddSeconds(-1) },
            _ => process with { ProcessIdentityImagePath = Path.Combine(fixture.Root, "unrelated.exe") }
        };
        // The host saved this invocation independently of the fixture's pre-build kernel.
        // Mutate the canonical snapshot, rather than overwriting it with that stale kernel.
        await new SqliteOrchestratorStateRepository(fixture.StatePath).TransactGoalStateAsync(fixture.GoalId, (state, _) =>
        {
            Assert.NotNull(state);
            var canonical = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([state.Goal], []));
            canonical.RecordTaskProcessStarted(fixture.GoalId, fixture.TaskId, replacement);
            return Task.FromResult((true, (GoalStateSnapshot?)(state with
            {
                Goal = canonical.ExportGoalSnapshot(fixture.GoalId)
            }), true));
        });
        var beforeConsumption = fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId);
        Assert.NotNull(beforeConsumption.LastProcess);
        Assert.Equal(replacement.ProcessId, beforeConsumption.LastProcess.ProcessId);
        Assert.Equal(replacement.ProcessIdentityStartedAt, beforeConsumption.LastProcess.ProcessIdentityStartedAt);
        Assert.Equal(replacement.ProcessIdentityImagePath, beforeConsumption.LastProcess.ProcessIdentityImagePath);
        var preserved = Assert.Single(beforeConsumption.WorkerBuildInvocations);
        Assert.Equal(invocation.Id, preserved.Id);
        Assert.Equal(WorkerBuildOutcome.Success, preserved.Outcome);
        Assert.False(preserved.Consumed);
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Consume());
        var retained = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value)
            .GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        Assert.Equal(invocation.Id, retained.Id);
        Assert.Equal(WorkerBuildOutcome.Success, retained.Outcome);
        Assert.False(retained.Consumed);
    }

    [Fact]
    public async Task EditedWorkerReceiptCannotSupplyHostEvidence()
    {
        using var fixture = new WorkerBuildRequestFixture();
        File.WriteAllText(Path.Combine(fixture.RepositoryRoot, ".orchestrator-context", "worker-build-receipt.json"),
            "{\"outcome\":\"Success\",\"compilerStarted\":true,\"exitCode\":0}");
        var consumed = fixture.Consume();
        Assert.True(consumed.MissingEvidence);
        Assert.False(consumed.CompilerFailed);
        Assert.Empty(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChangedCandidateCannotBorrowAnEarlierHostPass()
    {
        using var fixture = new WorkerBuildRequestFixture();
        Assert.Equal(WorkerBuildOutcome.Success, (await fixture.RunHelper()).Outcome);
        File.AppendAllText(Path.Combine(fixture.RepositoryRoot, WorkerBuildRequestFixture.Source), "\ninvalid later edit;");
        Assert.True(fixture.Consume().MissingEvidence);
        Assert.False(Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations).Consumed);
    }

    [Fact]
    public async Task CandidateChangeAtRegistrationPreventsCompilerResume()
    {
        using var fixture = new WorkerBuildRequestFixture();
        var changed = false;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Phase == WorkerBuildPhase.ChildRegistered && !changed)
            {
                changed = true;
                File.AppendAllText(Path.Combine(fixture.RepositoryRoot, WorkerBuildRequestFixture.Source), "\ninvalid later edit;");
            }
        });
        Assert.True(changed);
        Assert.Equal(WorkerBuildOutcome.StaleCandidate, invocation.Outcome);
        Assert.Single(invocation.Children!);
        Assert.DoesNotContain(invocation.Observations, o => o.Phase == WorkerBuildPhase.CompilerStarted);
    }
}
