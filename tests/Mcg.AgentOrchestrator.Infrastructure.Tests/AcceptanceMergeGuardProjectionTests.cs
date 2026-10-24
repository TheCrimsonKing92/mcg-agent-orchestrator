using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class AcceptanceMergeGuardProjectionTests
{
    [Xunit.Fact(DisplayName = "Acceptance_merge_guard_names_every_invalidating_task_projection_field")]
    public void AcceptanceMergeGuardNamesEveryInvalidatingTaskProjectionField()
    {
        var baseline = new AcceptanceMergeGuardSnapshot(
            GoalStatus.Verified,
            [new AcceptanceMergeGuardTask("task-one", AgentRole.Developer, WorkTaskStatus.Completed)]);

        Xunit.Assert.Equal("Goal.Status", AcceptanceMergeGuard.Compare(
            baseline,
            baseline with { GoalStatus = GoalStatus.Active })!.Field);
        Xunit.Assert.Equal("Tasks added/removed", AcceptanceMergeGuard.Compare(
            baseline,
            baseline with { Tasks = [] })!.Field);
        Xunit.Assert.Equal("Tasks added/removed", AcceptanceMergeGuard.Compare(
            baseline,
            baseline with { Tasks = [.. baseline.Tasks, new("task-two", AgentRole.Tester, WorkTaskStatus.Pending)] })!.Field);
        Xunit.Assert.Equal("Task task-one.RequiredRole", AcceptanceMergeGuard.Compare(
            baseline,
            baseline with { Tasks = [new("task-one", AgentRole.Tester, WorkTaskStatus.Completed)] })!.Field);
        Xunit.Assert.Equal("Task task-one.Status", AcceptanceMergeGuard.Compare(
            baseline,
            baseline with { Tasks = [new("task-one", AgentRole.Developer, WorkTaskStatus.Running)] })!.Field);
    }
}
