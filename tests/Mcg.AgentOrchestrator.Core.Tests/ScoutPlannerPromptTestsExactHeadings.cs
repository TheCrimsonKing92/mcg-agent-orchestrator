using Mcg.AgentOrchestrator.Core;

public sealed class ScoutPlannerPromptTestsExactHeadings
{
    [Xunit.Fact]
    public void ScoutBriefStatesExactHeadingsAndTheirLineContract()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Inspect and plan the goal.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Update docs/operator-runbook.md.", [planner]);

        var brief = kernel.BuildTaskBrief(goal.Id, planner.Id).Content;

        Xunit.Assert.Contains("## Scout Requirements", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("## Current source findings", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("## Prior goal evidence", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("## Upstream capabilities", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("## Likely seams and risks", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("each on its own line with nothing else on it", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("before the Planner plan sections", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerBriefAfterResearcherHasNoScoutRequirements()
    {
        var kernel = new AgentOrchestratorKernel();
        var researcher = new TaskSpec(TaskId.New(), "Inspect source.", AgentRole.Researcher);
        var planner = new TaskSpec(TaskId.New(), "Plan the goal.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Update docs/operator-runbook.md.", [researcher, planner]);

        var brief = kernel.BuildTaskBrief(goal.Id, planner.Id).Content;

        Xunit.Assert.DoesNotContain("## Scout Requirements", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("## Planner Requirements", brief, StringComparison.Ordinal);
    }
}
