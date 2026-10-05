using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsCancelDispatchRecoveryHint(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public async Task Cancel_second_task_appends_recovery_command_after_existing_fields()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"cancel-recovery-missing-{Guid.NewGuid():N}");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Cancel second task with recovery guidance.",
            [new TaskSpec(TaskId.New(), "First task", AgentRole.Planner),
             new TaskSpec(TaskId.New(), "Second task", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, goal.Tasks[0].Id, WorkTaskStatus.Completed, "First task done.");
        var task = goal.Tasks[1];
        var startedAt = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("worker", "worker command", missingPath, startedAt));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(4242, "worker command", missingPath,
                Path.Combine(missingPath, "out.log"), Path.Combine(missingPath, "err.log"),
                Path.Combine(missingPath, "exit.txt"), startedAt, null, null));
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.False(Directory.Exists(missingPath));
        var store = new CancelDispatchIntentTestStore();
        var intent = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.Intent(goal, task);
        await store.EnqueueAsync(intent);
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false, tryKillOwnedProcess: _ => false)
        {
            OperatorIntents = store,
            RequeueRefusalLog = _ => { }
        };
        var cancelCalls = 0;
        var coordinator = new OperatorIntentCoordinator(store)
        {
            CancelLatestProcess = (wk, goalId, taskId) =>
            {
                cancelCalls++;
                Assert.Equal(goal.Id, goalId);
                Assert.Equal(task.Id, taskId);
                return runner.CancelLatestProcess(wk, goalId, taskId);
            }
        };

        new ConductorBatchLoop(
            recoverInterruptedDispatches: wk => runner.RequeueInterruptedDispatches(wk),
            operatorIntents: coordinator).Run(kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1,
                persistGoalTick: (_, _) => { });

        Assert.Equal(1, cancelCalls);
        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
        Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
        Assert.True(task.LastProcess!.WasCancelled);
        var note = Assert.Single(goal.Timeline, evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote && evt.Message.StartsWith("CANCEL_DISPOSITION", StringComparison.Ordinal)).Message;
        Assert.StartsWith("CANCEL_DISPOSITION task_status=Cancelled redispatch=awaits-operator-retry preservation=failed=status:", note);
        Assert.EndsWith($" next=adjudicate --goal {goal.Id.Value[..8]} 2 route --cause <cause> --text-file <note> --evidence <reference>", note);
        Assert.DoesNotContain('\r', note);
        Assert.DoesNotContain('\n', note);
        Assert.False(Directory.Exists(missingPath));
    }
}
