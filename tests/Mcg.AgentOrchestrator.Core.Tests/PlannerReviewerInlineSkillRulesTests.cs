using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all state belongs to an in-memory kernel; no shared resources.
public sealed class PlannerReviewerInlineSkillRulesTests
{
    private static readonly string[] PlannerRules =
    [
        "- For each criterion name the evidence owner (checked against `docs/role-capability-matrix.md`), the owning seam, and the class `TEST-VERIFIABLE` or `REAL-WORLD-DEPENDENT`.",
        "- Route full-suite, test-host, and acceptance evidence to Acceptance and live or post-landing evidence to the operator; never assign evidence a worker cannot reach to Tester or Reviewer.",
        "- For evidence you cannot obtain, name what would settle it, its source, and why it is unavailable, then plan every other criterion in the same round."
    ];

    private static readonly string[] ReviewerRules =
    [
        "- Treat `Completed` status and worker prose as claims: check each claimed file, commit, command, and test against `git diff main...HEAD` and the repository.",
        "- A claim naming a file, command, endpoint, or test that does not exist is a blocking `correctness` finding.",
        "- Exit 0 with no relevant source change, or only generated or scratch noise, is not a pass.",
        "- Pass only when a relevant source change exists, the claims match the diff, and nothing unrelated changed."
    ];

    [Xunit.Fact]
    public void Build_AllComplexities_ContainsPlannerRulesOnlyForPlanner()
    {
        AssertRoleRules(AgentRole.Planner, PlannerRules);
    }

    [Xunit.Fact]
    public void Build_AllComplexities_ContainsReviewerRulesOnlyForReviewerWithinCaps()
    {
        AssertRoleRules(AgentRole.Reviewer, ReviewerRules);
        foreach (var complexity in Enum.GetValues<TaskComplexity>())
        {
            var text = SdlcRolePromptRequirements.BuildPlainText(AgentRole.Reviewer, complexity);
            var cap = complexity == TaskComplexity.Complex
                ? SdlcRolePromptRequirements.ReviewerComplexRequirementsMaxChars
                : SdlcRolePromptRequirements.ReviewerCompactRequirementsMaxChars;
            Assert.True(text.Length <= cap, $"{complexity}: {text.Length} exceeds {cap}.");
        }
    }

    [Xunit.Fact]
    public void BuildTaskBrief_PlannerAndReviewer_ContainsInlineRules()
    {
        var kernel = new AgentOrchestratorKernel();
        var researcher = new TaskSpec(TaskId.New(), "Inspect source.", AgentRole.Researcher);
        var planner = new TaskSpec(TaskId.New(), "Plan the change.", AgentRole.Planner);
        var reviewer = new TaskSpec(TaskId.New(), "Review the change.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Correct role guidance.", [researcher, planner, reviewer]);

        var plannerBrief = kernel.BuildTaskBrief(goal.Id, planner.Id).Content;
        var reviewerBrief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;

        foreach (var rule in PlannerRules)
        {
            Assert.Contains(rule, plannerBrief, StringComparison.Ordinal);
        }
        foreach (var rule in ReviewerRules)
        {
            Assert.Contains(rule, reviewerBrief, StringComparison.Ordinal);
        }
    }

    private static void AssertRoleRules(AgentRole owner, IReadOnlyList<string> rules)
    {
        foreach (var complexity in Enum.GetValues<TaskComplexity>())
        {
            var requirements = SdlcRolePromptRequirements.Build(owner, complexity);
            var anchorIndex = requirements.ToList().IndexOf(
                "- Do not modify repository files; implementation belongs to the Developer task.");
            Assert.True(anchorIndex >= rules.Count);
            for (var index = 0; index < rules.Count; index++)
            {
                var rule = rules[index];
                Assert.Single(requirements, requirement => requirement == rule);
                Assert.Equal(rule, requirements[anchorIndex - rules.Count + index]);
                foreach (var role in Enum.GetValues<AgentRole>().Where(role => role != owner))
                {
                    Assert.DoesNotContain(rule, SdlcRolePromptRequirements.Build(role, complexity));
                }
            }
        }
    }
}
