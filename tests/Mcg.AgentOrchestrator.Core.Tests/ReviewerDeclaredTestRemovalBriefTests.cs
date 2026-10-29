using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: every test owns its in-memory kernel.
public sealed class ReviewerDeclaredTestRemovalBriefTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildTaskBrief_Removals_AppearOnceAsUnindexedContext(bool registered)
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var reviewer = new TaskSpec(TaskId.New(), "Review the candidate diff", AgentRole.Reviewer);
        var identities = Enumerable.Range(0, 50).Select(i => $"Example.Tests.FooTests.Removed{i:D2}").ToArray();
        var objective = "Implement the change.\n\n## Acceptance criteria\n1. The behavior works.\n" +
            string.Join("\n", identities.Select((identity, i) => $"{i + 2}. test-removal: {identity}"));
        var goal = kernel.CreateGoal(objective, [reviewer]);
        if (registered)
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec($"Implement: {objective}",
                ["The behavior works."], VerificationClass.TestVerifiable, [], []));

        var brief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;

        Assert.Equal(1, Occurrences(brief, "these 50 removals are declared; confirm the diff removes them"));
        foreach (var identity in identities)
        {
            Assert.Equal(1, Occurrences(brief, identity));
            Assert.Contains($"- {identity}", brief, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("test-removal:", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("2. test-removal", brief, StringComparison.Ordinal);
        Assert.Contains("- The behavior works.", brief, StringComparison.Ordinal);
        if (registered)
            Assert.Single(goal.RefinedSpec!.AcceptanceCriteria);
    }

    [Fact]
    public void ReviseGoalBrief_Removals_DoNotReenterRegisteredCriteria()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Initial objective");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Initial behavior", ["Initial criterion"],
            VerificationClass.TestVerifiable, [], []));

        kernel.ReviseGoalBrief(goal.Id,
            "Updated objective\n## Acceptance criteria\n1. test-removal: FooTests.Removed\n2. Updated criterion.",
            "Update the brief");

        Assert.Equal(["Updated criterion."], goal.RefinedSpec!.AcceptanceCriteria);
    }

    private static int Occurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;
}
