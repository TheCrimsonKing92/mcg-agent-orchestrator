using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsFailedRoundCheckpointCancelExclusion : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsFailedRoundCheckpointCancelExclusion(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task OperatorCancel_DirtyDeveloper_PreservesStashWithoutFailedReceipt()
    {
        using var f = new FailedRoundCheckpointFixture();
        f.WriteEdits();
        var before = FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD");
        var store = new CancelDispatchIntentTestStore();
        var intent = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.Intent(f.Goal, f.Worker)
            with { CreatedAt = f.Clock.UtcNow };
        await store.EnqueueAsync(intent);
        var runner = new BackgroundDispatchRunner(f.Clock,
            isStillRunning: _ => false, tryKillOwnedProcess: _ => false,
            findBuildDaemons: _ => [], tryKillBuildDaemon: _ => false)
        {
            OperatorIntents = store,
            RequeueRefusalLog = _ => { }
        };
        var cancelCalls = 0;
        var coordinator = new OperatorIntentCoordinator(store, utcNow: () => f.Clock.UtcNow)
        {
            CancelLatestProcess = (kernel, goalId, taskId) =>
            {
                cancelCalls++;
                return runner.CancelLatestProcess(kernel, goalId, taskId);
            }
        };
        var starts = 0;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); });
        new ConductorBatchLoop(recoverInterruptedDispatches: kernel => runner.RequeueInterruptedDispatches(kernel),
            operatorIntents: coordinator).Run(f.Kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 1, persistGoalTick: (_, _) => { });

        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
        Assert.Equal(1, cancelCalls);
        Assert.Equal(0, starts);
        Assert.Equal(WorkTaskStatus.Cancelled, f.Worker.Status);
        Assert.True(f.Worker.LastProcess!.WasCancelled);
        runner.RefreshLatestProcess(f.Kernel, f.Goal.Id, f.Worker.Id);
        Assert.All(f.Worker.DispatchHistory, dispatch => Assert.Null(dispatch.FailedRoundCheckpointReceipt));
        Assert.All(f.Worker.DispatchHistory, dispatch => Assert.Null(dispatch.FailedRoundCheckpointDecision));
        Assert.Equal(before, FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD"));
        Assert.DoesNotContain("checkpoint: preserved uncommitted edits from failed dispatch",
            FailedRoundCheckpointFixture.Git(f.Worktree, "log", "--all", "--format=%s"));
        var disposition = Assert.Single(f.Goal.Timeline, evt => evt.TaskId == f.Worker.Id &&
            evt.Kind == ProgressKind.TaskNote && evt.Message.StartsWith("CANCEL_DISPOSITION", StringComparison.Ordinal));
        Assert.StartsWith("CANCEL_DISPOSITION task_status=Cancelled redispatch=awaits-operator-retry preservation=preserved=",
            disposition.Message);
        Assert.Empty(FailedRoundCheckpointFixture.Git(f.Worktree, "status", "--short"));
        var stash = FailedRoundCheckpointFixture.Git(f.Worktree, "stash", "list", "--format=%H");
        Assert.Equal(40, stash.Length);
        Assert.Equal("modified tracked content", FailedRoundCheckpointFixture.Git(f.Worktree, "show", stash + ":seed.txt"));
        Assert.Equal("new untracked content", FailedRoundCheckpointFixture.Git(f.Worktree, "show", stash + "^3:new-untracked.txt"));
        f.Retry();
        Assert.True(new FailedRoundCheckpointPreDispatch(f.Kernel, f.Repository)
            .IntegrateMainBeforeDeveloperDispatch(f.Goal).CanDispatch);
        Assert.Equal(before, FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD"));
        Assert.All(f.Worker.DispatchHistory, dispatch => Assert.Null(dispatch.FailedRoundCheckpointReceipt));
    }
}
