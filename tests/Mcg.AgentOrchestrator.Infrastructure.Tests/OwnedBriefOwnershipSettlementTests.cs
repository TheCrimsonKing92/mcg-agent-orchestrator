using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;
using static OwnedBriefOwnershipTestScenario;

// Parallel-safe: immutable inputs and in-memory collaborators, with a private temporary root per scenario.
public sealed class OwnedBriefOwnershipSettlementTests
{
    [Fact]
    public async Task RefineAsync_FullyOwnedBrief_RecordsDecisionWithoutClarification()
    {
        using var scenario = new RefinerScenario(Brief);

        var result = await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        Assert.Empty(scenario.Collaboration.Items);
        Assert.Empty(result.Spec.OpenQuestions);
        var decision = Assert.Single(result.Spec.Decisions);
        Assert.Equal(Question, decision.Question);
        Assert.True(OwnedBriefOwnershipSettlement.TrySettle("ownership-lifecycle", Question, Brief, out var settlement));
        Assert.Equal(settlement.AnswerText, decision.Choice);
        Assert.Equal($"Settled by the brief's declared criterion owners (topic: {Topic}).", decision.Rationale);
        Assert.Single(scenario.Goal.Timeline, entry => entry.Kind == ProgressKind.GoalPolicyDecision &&
            entry.Message == $"{PolicyPrefix}: topic={Topic}");
    }

    [Theory]
    [InlineData(Brief)]
    [InlineData("Return widgets.\r\n\r\n## Acceptance criteria\r\n1. The widget count is returned correctly.\r\n   Developer owns; Acceptance executes. TEST-VERIFIABLE.\r\n2. " + SecondCriterion)]
    public void TrySettle_FullyOwnedBrief_ReturnsGroundableOwnerSpans(string brief)
    {
        Assert.True(OwnedBriefOwnershipSettlement.TrySettle(" OWNERSHIP-LIFECYCLE ", Question, brief, out var settlement));

        Assert.Equal(new[] { FirstCriterion, SecondCriterion }, settlement.Citations);
        Assert.Contains("The conductor and the acceptance gate execute verification", settlement.AnswerText);
        Assert.Contains("never gate completion", settlement.AnswerText);
        Assert.Equal("missing-evidence", ConductorAuthorOwnerClassCheck.Evaluate(Question, settlement.AnswerText, null));
        Assert.True(ConductorAuthorBriefGrounding.TryGround(brief, settlement.AnswerText,
            settlement.Citations, "missing-evidence", out var grounded));
        Assert.Equal(settlement.Citations, grounded);
    }

    [Theory]
    [InlineData("scope", Brief)]
    [InlineData("ownership-lifecycle", "Return widgets.")]
    [InlineData("ownership-lifecycle", "## Acceptance criteria\n\n## Scope\nDeveloper owns; Acceptance executes.")]
    [InlineData("ownership-lifecycle", MissingOwnerBrief)]
    [InlineData("ownership-lifecycle", OperatorOwnedBrief)]
    [InlineData("ownership-lifecycle", "## Acceptance criteria\n1. Widget count is correct. Developer owns; Acceptance executes. Operator owns; Operator executes.")]
    public void TrySettle_IneligibleBriefOrFork_RejectsSettlement(string kind, string brief)
    {
        Assert.False(OwnedBriefOwnershipSettlement.TrySettle(kind, Question, brief, out _));
    }
}
