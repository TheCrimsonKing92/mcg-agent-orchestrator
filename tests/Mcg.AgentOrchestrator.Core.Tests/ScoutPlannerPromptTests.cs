using Mcg.AgentOrchestrator.Core;

public sealed class ScoutPlannerPromptTests
{
    [Xunit.Fact]
    public void PlannerWithoutEarlierResearcherGetsResearchAndPlanningDuties()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Inspect and plan the goal.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Update docs/operator-runbook.md.", [planner]);

        var brief = kernel.BuildTaskBrief(goal.Id, planner.Id).Content;

        Xunit.Assert.Contains("## Scout Requirements", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("## Researcher Requirements", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("## Planner Requirements", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("Do NOT build the solution or run tests", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("Separate confirmed facts from inferences", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("before the Planner plan sections", brief, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Use the complete Durable Research Notes", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerAfterResearcherKeepsExistingRoleBlock()
    {
        var kernel = new AgentOrchestratorKernel();
        var researcher = new TaskSpec(TaskId.New(), "Inspect source.", AgentRole.Researcher);
        var planner = new TaskSpec(TaskId.New(), "Plan the goal.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Update docs/operator-runbook.md.", [researcher, planner]);

        var brief = kernel.BuildTaskBrief(goal.Id, planner.Id).Content;

        Xunit.Assert.DoesNotContain("## Scout Requirements", brief, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("## Researcher Requirements", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("## Planner Requirements", brief, StringComparison.Ordinal);
        Xunit.Assert.False(ScoutRoundPolicy.IsScoutPlanner(goal, planner));
    }
}
