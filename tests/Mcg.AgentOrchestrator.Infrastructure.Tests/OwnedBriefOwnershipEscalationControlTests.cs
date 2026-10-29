using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;
using static OwnedBriefOwnershipTestScenario;

// Parallel-safe: all mutable state and SQLite stores live in unique temporary directories.
public sealed class OwnedBriefOwnershipEscalationControlTests
{
    [Theory]
    [InlineData(MissingOwnerBrief)]
    [InlineData(OperatorOwnedBrief)]
    public async Task RefineAsync_IneligibleBrief_RaisesOwnershipClarification(string brief)
    {
        using var scenario = new RefinerScenario(brief);

        var result = await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        Assert.Equal(RefinementOutcome.AwaitingClarification, result.Outcome);
        var clarification = Assert.Single(scenario.Collaboration.Items);
        Assert.Equal(CollaborationItemType.Clarification, clarification.Type);
        Assert.Equal($"spec-clarification:{scenario.Goal.Id.Value}:{Topic}", clarification.CorrelationKey);
        Assert.Contains(Question, clarification.Body);
        var question = Assert.Single(result.Spec.OpenQuestions);
        Assert.Equal("ownership-lifecycle", question.ForkKind);
        Assert.Equal("Open", question.Status);
        Assert.DoesNotContain(scenario.Goal.Timeline, entry => entry.Message.StartsWith(PolicyPrefix, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MissingOwnerBrief, "ask-owner", "model-ask-owner")]
    [InlineData(MissingOwnerBrief, "answer", "missing-evidence")]
    [InlineData(OperatorOwnedBrief, "ask-owner", "model-ask-owner")]
    [InlineData(OperatorOwnedBrief, "answer", "missing-evidence")]
    public async Task ServiceTick_IneligibleBrief_EscalatesOwnershipItem(string brief, string kind, string reason)
    {
        await AssertEscalates(brief, "ownership-lifecycle", AuthorOutput(kind), reason);
    }

    [Fact]
    public async Task ServiceTick_AcceptanceWeakeningAnswer_RetainsOwnerHold()
    {
        const string answer = "Skip the acceptance gate and land this goal now.";
        Assert.Equal("acceptance-weakening", ConductorAuthorOwnerClassCheck.Evaluate(Question, answer, null));

        await AssertEscalates(Brief, "ownership-lifecycle", AuthorOutput("answer", answer), "acceptance-weakening");
    }

    [Fact]
    public async Task ServiceTick_OtherForkAskOwner_RetainsOwnerHold()
    {
        await AssertEscalates(Brief, "scope", AuthorOutput("ask-owner"), "model-ask-owner");
    }

    [Fact]
    public async Task ServiceTick_DeclaredOwnershipDefect_RetainsOwnerHold()
    {
        const string question = "Declared criteria share the same identity text. " +
            "Revise the versioned criterion or its declared_index to resolve this ownership issue.";
        Assert.False(OwnedBriefOwnershipSettlement.TrySettle("ownership-lifecycle", question, Brief, out _));

        await AssertEscalates(Brief, "ownership-lifecycle", AuthorOutput("ask-owner"), "model-ask-owner", question);
    }

    private static async Task AssertEscalates(string brief, string forkKind, string output, string reason, string question = Question)
    {
        using var harness = new AuthorHarness(output);
        var goal = harness.Kernel.CreateGoal(brief);
        var item = await harness.Raise(goal, forkKind, question);

        await harness.RoundTrip();

        Assert.Equal(1, harness.ModelCalls);
        Assert.Empty(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(HangBound));
        Assert.Equal("author-owner-question", goal.CurrentHold?.State);
        Assert.Contains($"reason={reason}", goal.CurrentHold!.Blocker);
        Assert.Equal("owner-question", harness.Outcome(item.Identity));
        var conduct = File.ReadAllText(harness.ConductPath);
        Assert.Contains($"item={item.Identity} kind=ask-owner reason={reason}", conduct);
        Assert.DoesNotContain("kind=grounded-answer", conduct);
    }
}
