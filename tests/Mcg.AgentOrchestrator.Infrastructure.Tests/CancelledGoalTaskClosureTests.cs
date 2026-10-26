using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its temporary root and only observes tracked PIDs.
public sealed class CancelledGoalTaskClosureTests : CliCommandTestBase
{
    [Fact]
    public void CancelledGoalWithRetiredDispositionClosesStaleTasksOnce()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var assigned = new TaskSpec(TaskId.New(), "Assigned work", AgentRole.Developer);
            var failed = new TaskSpec(TaskId.New(), "Failed work", AgentRole.Tester);
            var waiting = new TaskSpec(TaskId.New(), "Await operator", AgentRole.Reviewer);
            var exited = new TaskSpec(TaskId.New(), "Interrupted work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Retired cancelled work", [assigned, failed, waiting, exited]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var request = kernel.RequestHumanInput(goal.Id, waiting.Id, "Continue?", HumanWaitKind.SpecClarification);
            RecordRunningProcess(kernel, goal, exited, root, processId: 999999);
            kernel.ReportTaskProgress(goal.Id, failed.Id, WorkTaskStatus.Failed, "Earlier failure");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Cancelled);
            GoalOperationJournal.RecordTerminalDisposition(root, kernel.GetGoal(goal.Id),
                new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "Abandoned by operator"));

            Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, assigned.Id).Status);
            Assert.Equal(WorkTaskStatus.Failed, kernel.GetTask(goal.Id, failed.Id).Status);
            Assert.Equal(WorkTaskStatus.WaitingForHuman, kernel.GetTask(goal.Id, waiting.Id).Status);
            Assert.True(kernel.GetTask(goal.Id, exited.Id).LastProcess!.IsRunning);
            Assert.False(kernel.HumanInputRequests.Single(item => item.Id == request.Id).IsCompleted);

            var first = TerminalGoalSweep.Run(kernel, root, TrunkBranchName.Default);
            var repaired = kernel.GetGoal(goal.Id);

            Assert.Equal(GoalStatus.Cancelled, repaired.Status);
            Assert.All(repaired.Tasks, task => Assert.Equal(WorkTaskStatus.Cancelled, task.Status));
            var result = Assert.Single(first.Goals);
            Assert.Equal(goal.Id, result.GoalId);
            var repair = Assert.Single(result.Repairs.Where(item => item.Kind == "terminal-task-desync"));
            Assert.Contains("goalState=Cancelled", repair.Evidence, StringComparison.Ordinal);
            Assert.Contains("action=cancel-stale-tasks", repair.Evidence, StringComparison.Ordinal);
            Assert.True(kernel.HumanInputRequests.Single(item => item.Id == request.Id).IsCompleted);
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
            var statuses = repaired.Tasks.Select(task => (task.Id, task.Status)).ToArray();
            var timelineCount = repaired.Timeline.Count;

            var second = TerminalGoalSweep.Run(kernel, root, TrunkBranchName.Default);

            Assert.DoesNotContain(second.Goals, item => item.GoalId == goal.Id);
            Assert.Equal(GoalStatus.Cancelled, kernel.GetGoal(goal.Id).Status);
            Assert.Equal(statuses, kernel.GetGoal(goal.Id).Tasks.Select(task => (task.Id, task.Status)).ToArray());
            Assert.Equal(timelineCount, kernel.GetGoal(goal.Id).Timeline.Count);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void CancelledGoalWithLiveTrackedProcessKeepsEveryTask()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var assigned = new TaskSpec(TaskId.New(), "Assigned work", AgentRole.Developer);
            var running = new TaskSpec(TaskId.New(), "Live work", AgentRole.Tester);
            var goal = kernel.CreateGoal("Retired goal with live process", [assigned, running]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            RecordRunningProcess(kernel, goal, running, root, Environment.ProcessId);
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Cancelled);
            GoalOperationJournal.RecordTerminalDisposition(root, kernel.GetGoal(goal.Id),
                new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "Abandoned by operator"));
            var statuses = kernel.GetGoal(goal.Id).Tasks.Select(task => (task.Id, task.Status)).ToArray();
            Assert.Equal(new[] { WorkTaskStatus.Assigned, WorkTaskStatus.Running }, statuses.Select(task => task.Status).ToArray());

            var sweep = TerminalGoalSweep.Run(kernel, root, TrunkBranchName.Default);

            var unchanged = kernel.GetGoal(goal.Id);
            Assert.Equal(GoalStatus.Cancelled, unchanged.Status);
            Assert.Equal(statuses, unchanged.Tasks.Select(task => (task.Id, task.Status)).ToArray());
            Assert.DoesNotContain(sweep.Goals.Where(item => item.GoalId == goal.Id)
                .SelectMany(item => item.Repairs), repair => repair.Kind == "terminal-task-desync");
            Assert.Contains(unchanged.Tasks, task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void FailedAndCompletedGoalsWithRetiredDispositionAreUnchanged()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var failed = kernel.CreateGoal("Retired failed goal",
                [new TaskSpec(TaskId.New(), "Assigned failed-goal work", AgentRole.Developer)]);
            var completed = kernel.CreateGoal("Retired completed goal",
                [new TaskSpec(TaskId.New(), "Assigned completed-goal work", AgentRole.Developer)]);
            kernel.ActivateGoal(failed.Id, AgentCatalog.Default().Agents);
            kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
            kernel = WithGoalStatus(WithGoalStatus(kernel, failed.Id, GoalStatus.Failed), completed.Id, GoalStatus.Completed);
            var goals = new[] { kernel.GetGoal(failed.Id), kernel.GetGoal(completed.Id) };
            foreach (var goal in goals)
            {
                GoalOperationJournal.RecordTerminalDisposition(root, goal,
                    new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "Retired without branch artifacts"));
            }
            var statuses = goals.ToDictionary(goal => goal.Id,
                goal => (goal.Status, Tasks: goal.Tasks.Select(task => (task.Id, task.Status)).ToArray()));

            var sweep = TerminalGoalSweep.Run(kernel, root, TrunkBranchName.Default);

            foreach (var goal in goals)
            {
                var unchanged = kernel.GetGoal(goal.Id);
                Assert.Equal(statuses[goal.Id].Status, unchanged.Status);
                Assert.Equal(statuses[goal.Id].Tasks, unchanged.Tasks.Select(task => (task.Id, task.Status)).ToArray());
                Assert.DoesNotContain(sweep.Goals, item => item.GoalId == goal.Id);
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
