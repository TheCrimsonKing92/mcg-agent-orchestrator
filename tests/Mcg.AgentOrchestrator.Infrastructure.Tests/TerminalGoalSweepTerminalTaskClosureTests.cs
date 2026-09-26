using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TerminalGoalSweepTerminalTaskClosureTests : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Superseded)]
    [Xunit.InlineData(GoalStatus.Cancelled)]
    [Xunit.InlineData(GoalStatus.Completed)]
    public void ProtectedGoalClosesAssignedAndFailedTasksWithoutReopening(GoalStatus status)
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var assigned = new TaskSpec(TaskId.New(), "Assigned work", AgentRole.Developer);
        var failed = new TaskSpec(TaskId.New(), "Failed work", AgentRole.Tester);
        var goal = kernel.CreateGoal("Terminal task repair", [assigned, failed]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, failed.Id, WorkTaskStatus.Failed, "Earlier failure");
        kernel = WithGoalStatus(kernel, goal.Id, status);

        var first = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var repaired = kernel.GetGoal(goal.Id);

        Xunit.Assert.Equal(status, repaired.Status);
        Xunit.Assert.All(repaired.Tasks, task => Xunit.Assert.Equal(WorkTaskStatus.Cancelled, task.Status));
        Xunit.Assert.Equal(2, repaired.Timeline.Count(evt =>
            evt.Kind == ProgressKind.TaskCancelled && evt.Message.Contains(status.ToString(), StringComparison.Ordinal)));
        Xunit.Assert.Contains(repaired.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision && evt.Message.Contains(status.ToString(), StringComparison.Ordinal));
        var repair = Xunit.Assert.Single(Xunit.Assert.Single(first.Goals).Repairs.Where(item => item.Kind == "terminal-task-desync"));
        Xunit.Assert.Contains($"goalState={status}", repair.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains("action=cancel-stale-tasks", repair.Evidence, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(repaired.Timeline, evt => evt.Kind == ProgressKind.HumanInputRequested);
        Xunit.Assert.Empty(TerminalGoalSweep.Run(kernel, root, goal.Id).Goals);
    }

    [Xunit.Fact]
    public void FailedGoalStillReopensWithAssignedTask()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Assigned work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Failed task repair", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Failed);

        var sweep = TerminalGoalSweep.Run(kernel, CreateTempDirectory(), goal.Id);

        Xunit.Assert.Equal(GoalStatus.Active, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, task.Id).Status);
        var repair = Xunit.Assert.Single(Xunit.Assert.Single(sweep.Goals).Repairs.Where(item => item.Kind == "terminal-task-desync"));
        Xunit.Assert.Contains("goalState=Failed", repair.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains("action=reopen", repair.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains(kernel.GetGoal(goal.Id).Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message == "terminal stale-goal sweep: reopened terminal goal with non-terminal task(s).");
    }

    [Xunit.Fact]
    public void SupersededGoalClosesFailedTaskEvenWithoutAssignedTask()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Failed work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Failed only", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Earlier failure");
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Superseded);

        var sweep = TerminalGoalSweep.Run(kernel, CreateTempDirectory(), goal.Id);

        Xunit.Assert.Contains(Xunit.Assert.Single(sweep.Goals).Repairs, repair => repair.Kind == "terminal-task-desync");
        Xunit.Assert.Equal(GoalStatus.Superseded, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(WorkTaskStatus.Cancelled, kernel.GetTask(goal.Id, task.Id).Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Superseded)]
    [Xunit.InlineData(GoalStatus.Cancelled)]
    [Xunit.InlineData(GoalStatus.Completed)]
    public void GlobalSweepClosesProtectedGoalTasks(GoalStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Stale assigned work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Global terminal repair", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel = WithGoalStatus(kernel, goal.Id, status);

        var sweep = TerminalGoalSweep.Run(kernel, CreateTempDirectory());

        Xunit.Assert.Contains(Xunit.Assert.Single(sweep.Goals).Repairs, repair => repair.Kind == "terminal-task-desync");
        Xunit.Assert.Equal(status, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(WorkTaskStatus.Cancelled, kernel.GetTask(goal.Id, task.Id).Status);
    }
}
