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
    [InlineData("pid")]
    [InlineData("start")]
    public void WrongOrReusedHostProcessCannotAuthorizeConsumption(string control)
    {
        using var fixture = new WorkerBuildRequestFixture();
        using var current = Process.GetCurrentProcess();
        var identity = WorkerBuildRequestChannel.ReadIdentity(current);
        identity = control == "pid" ? identity with { Pid = identity.Pid + 1 } : identity with { StartedAt = identity.StartedAt.AddSeconds(-1) };
        Assert.Throws<UnauthorizedAccessException>(() => WorkerBuildInvocationOperations.RequireOrigin(
            fixture.Kernel, fixture.Reference, identity, requireRunning: true));
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
