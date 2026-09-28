using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsOperatorCancelDispatchIntent : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsOperatorCancelDispatchIntent(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public async Task BatchTickAppliesOperatorCancelAndDoesNotRedispatch()
    {
        var (kernel, goal, task) = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.RunningTask();
        var store = new CancelDispatchIntentTestStore();
        var intent = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.Intent(goal, task);
        await store.EnqueueAsync(intent);
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false, tryKillOwnedProcess: _ => false)
        {
            OperatorIntents = store,
            RequeueRefusalLog = _ => { }
        };
        var coordinator = new OperatorIntentCoordinator(store)
        {
            CancelLatestProcess = (wk, goalId, taskId) => runner.CancelLatestProcess(wk, goalId, taskId)
        };
        var dispatchStarts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: candidate =>
            {
                if (!candidate.Tasks.Any(item => item.Status == WorkTaskStatus.Assigned))
                    return DispatchStartOutcome.EmptyBatch("No assigned tasks remain.");
                dispatchStarts++;
                return DispatchStartOutcome.Started();
            });

        void Tick() => new ConductorBatchLoop(
            recoverInterruptedDispatches: wk => runner.RequeueInterruptedDispatches(wk),
            operatorIntents: coordinator).Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 1, persistGoalTick: (_, _) => { });

        Tick();
        Xunit.Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
        Xunit.Assert.False(task.WasCancelledByConductor);
        Xunit.Assert.True(task.LastProcess!.WasCancelled);
        Xunit.Assert.False(task.LastProcess.WasCancelledByConductor);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
        Xunit.Assert.Equal(0, dispatchStarts);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);

        var lastDispatch = task.LastDispatch;
        Tick();
        Xunit.Assert.Equal(0, dispatchStarts);
        Xunit.Assert.Same(lastDispatch, task.LastDispatch);
        Xunit.Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
    }

    [Xunit.Fact]
    public async Task BatchSweepRefreshDoesNotPreemptOperatorCancelIntent()
    {
        var (kernel, goal, task) = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.RunningTask(
            DateTimeOffset.UtcNow.AddHours(-2), AgentRole.Researcher);
        var store = new CancelDispatchIntentTestStore();
        var intent = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.Intent(goal, task);
        await store.EnqueueAsync(intent);
        kernel.RecordDispatchProviderSessionId(goal.Id, task.Id, "session-after-cancel");
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false, tryKillOwnedProcess: _ => false)
        {
            OperatorIntents = store,
            RequeueRefusalLog = _ => { }
        };
        var coordinator = new OperatorIntentCoordinator(store)
        {
            CancelLatestProcess = (wk, goalId, taskId) => runner.CancelLatestProcess(wk, goalId, taskId)
        };
        var dispatchStarts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { dispatchStarts++; return DispatchStartOutcome.Started(); });

        new ConductorBatchLoop(
            measuredSweep: wk =>
            {
                new GoalDispatchOperations().RefreshDispatches(wk, goal, runner);
                return new TerminalGoalSweepResult([]);
            },
            recoverInterruptedDispatches: wk => runner.RequeueInterruptedDispatches(wk),
            operatorIntents: coordinator).Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 1, persistGoalTick: (_, _) => { });

        Xunit.Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
        Xunit.Assert.False(task.WasCancelledByConductor);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
        Xunit.Assert.Equal(0, dispatchStarts);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
    }
}
