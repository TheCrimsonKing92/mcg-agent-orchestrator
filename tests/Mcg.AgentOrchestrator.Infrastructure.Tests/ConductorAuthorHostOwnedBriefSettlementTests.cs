using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;
using static OwnedBriefOwnershipTestScenario;

// Parallel-safe: each harness owns its temporary workspace and SQLite stores; no shared state.
public sealed class ConductorAuthorHostOwnedBriefSettlementTests
{
    [Theory]
    [InlineData("ask-owner", "model-ask-owner")]
    [InlineData("answer", "missing-evidence")]
    public async Task ServiceTick_OwnedOwnershipFork_SubmitsGroundedAgentAnswer(string kind, string reason)
    {
        using var harness = new AuthorHarness(AuthorOutput(kind));
        var goal = harness.Kernel.CreateGoal(Brief);
        var item = await harness.Raise(goal);
        if (kind == "answer")
            Assert.Equal("missing-evidence", ConductorAuthorAnswerRouting.Decide(item,
                "The conductor executes verification.", null).EscalationReason);

        await harness.RoundTrip();

        Assert.Equal(1, harness.ModelCalls);
        var intent = Assert.Single(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(HangBound));
        var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(OperatorIntentVerbs.Answer, intent.Verb);
        Assert.Equal("author", intent.Actor);
        Assert.Equal("conductor-author", intent.Channel);
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Assert.Equal(OperatorActorKind.Agent, payload.ActorKind);
        Assert.Equal(item.TargetKind, payload.TargetKind);
        Assert.Equal(item.TargetId, payload.TargetId);
        Assert.True(OwnedBriefOwnershipSettlement.TrySettle(item.ForkKind, item.Question, Brief, out var settlement));
        Assert.Equal(settlement.AnswerText, payload.Text);
        Assert.Equal(new[] { "brief-span=" + FirstCriterion, "brief-span=" + SecondCriterion,
            "grounded-reason=" + reason, "author-item=" + item.Identity }, payload.EvidenceReferences);
        Assert.Null(goal.CurrentHold);
        Assert.Equal("answer-submitted", harness.Outcome(item.Identity));
        var conduct = File.ReadAllText(harness.ConductPath);
        Assert.Contains($"item={item.Identity} kind=grounded-answer", conduct);
        Assert.DoesNotContain("kind=ask-owner", conduct);
    }
}
