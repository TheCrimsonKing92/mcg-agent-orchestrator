using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class WorkerBuildScopeDispatchOwnershipTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SuccessorPreparationPreservesPredecessorCompletion(bool abortStart, bool reload)
    {
        using var fixture = new WorkerBuildScopeDispatchFixture();
        Assert.True(fixture.Receipt().Matches);
        var predecessor = fixture.Predecessor.LastDispatch!;
        var candidate = WorkerBuildScopeDispatchFixture.Git(fixture.Root, "rev-parse", "HEAD");
        var durableBeforeSuccessor = fixture.Kernel.ExportSnapshot();
        fixture.Prepare(fixture.Successor);
        Assert.NotEqual(predecessor.BaseCommit, fixture.Successor.LastDispatch!.BaseCommit);
        if (abortStart)
        {
            var checkpointReached = false;
            var startInvoked = false;
            var runner = new BackgroundDispatchRunner(fixture.Clock, disableProcessStart: false,
                startProcess: _ => { startInvoked = true; throw new InvalidOperationException("Unexpected process start."); });
            var rejection = Assert.Throws<DispatchRecordWriteException>(() => runner.TryStartLatestDispatch(
                fixture.Kernel, fixture.GoalId, fixture.SuccessorId, Path.Combine(fixture.Main, "start-logs"),
                checkpointBeforeWorkerStart: (_, goalId, taskId, phase) =>
                {
                    Assert.Equal(DispatchRecordCheckpointPhase.BeforeProcessStart, phase);
                    checkpointReached = true;
                    throw DispatchRecordWriteException.From(new DispatchCheckpointConflictException("Controlled stale-state rejection."),
                        phase, "fixture", goalId, taskId);
                }, sandboxOptions: new(false, "fixture", "fixture")));
            Assert.True(rejection.PreservesAuthoritativeState);
            Assert.True(checkpointReached);
            Assert.False(startInvoked);
            // The rejected start never became authoritative. Its prepared file remains in the worktree.
            fixture.Kernel = AgentOrchestratorKernel.FromSnapshot(durableBeforeSuccessor, fixture.Clock);
            Assert.Null(fixture.Successor.LastDispatch);
        }
        var successorBytes = File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(fixture.Root));
        Assert.NotEqual(predecessor.WorkerBuildScope!.MetadataDigest,
            WorkerBuildProjectScope.ReadDispatchIdentity(fixture.Root).MetadataDigest);
        if (reload)
            fixture.Kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot()))!, fixture.Clock);

        fixture.Reconcile();

        Assert.Equal(WorkTaskStatus.Completed, fixture.Predecessor.Status);
        Assert.Equal(0, fixture.Predecessor.LastVerification!.ExitCode);
        Assert.DoesNotContain(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing,
            fixture.Predecessor.LastVerification.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Builds);
        Assert.Single(fixture.Predecessor.DispatchHistory);
        Assert.Equal(2, fixture.Kernel.GetGoal(fixture.GoalId).Tasks.Count);
        Assert.Equal(candidate, WorkerBuildScopeDispatchFixture.Git(fixture.Root, "rev-parse", "HEAD"));
        Assert.Equal(successorBytes, File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(fixture.Root)));
    }

    [Fact]
    public void SuccessorLaterCompletionUsesItsOwnScopeAndDispatchBase()
    {
        using var fixture = new WorkerBuildScopeDispatchFixture();
        fixture.Prepare(fixture.Successor);
        var successor = fixture.Successor.LastDispatch!;
        Assert.NotEqual(fixture.Predecessor.LastDispatch!.BaseCommit, successor.BaseCommit);
        fixture.Reconcile();
        Assert.Equal(WorkTaskStatus.Completed, fixture.Predecessor.Status);
        fixture.CompleteProcess(fixture.Successor);
        fixture.WriteReceipt(fixture.Successor);
        Assert.True(fixture.Receipt(fixture.Successor).Matches);

        fixture.Reconcile(fixture.Successor);

        Assert.Equal(WorkTaskStatus.Completed, fixture.Successor.Status);
        Assert.Equal(successor.WorkerBuildScope, fixture.Successor.LastDispatch!.WorkerBuildScope);
        Assert.Equal(successor.BaseCommit, fixture.Successor.LastDispatch.BaseCommit);
        Assert.Equal(0, fixture.Builds);
    }

    [Fact]
    public void ProviderSessionDiscoveryPreservesScopeAuthority()
    {
        using var fixture = new WorkerBuildScopeDispatchFixture();
        var captured = fixture.Predecessor.LastDispatch!.WorkerBuildScope;
        Assert.Null(fixture.Predecessor.LastDispatch.ProviderSessionId);
        fixture.Kernel.RecordDispatchProviderSessionId(fixture.GoalId, fixture.PredecessorId, "discovered-session");
        Assert.Equal("discovered-session", fixture.Predecessor.LastDispatch!.ProviderSessionId);
        Assert.Equal(captured, fixture.Predecessor.LastDispatch.WorkerBuildScope);
        fixture.Prepare(fixture.Successor);

        fixture.Reconcile();

        Assert.Equal(WorkTaskStatus.Completed, fixture.Predecessor.Status);
        Assert.Equal(0, fixture.Builds);
    }
}
