using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCohortWorkflowTestsMainSuspectReleaseField
{
    [Fact]
    public async Task AttributionReceipt_PersistsSameStructuredTestsAsDetail()
    {
        using var scenario = AcceptanceCohortWorkflowTestsMainSuspect.CreateScenario();
        scenario.Run();
        var failure = Assert.Single((await scenario.Events.ReadAllAsync()).Where(item => item.Kind == PostLandingCanaryEventKind.Failed));
        Assert.Equal(new[] { AcceptanceCohortWorkflowTestsMainSuspect.TestA }, failure.Payload.SharedFailingTests);
        Assert.Contains($"tests={ConductorAcceptanceCohortMainSuspect.FormatTests(failure.Payload.SharedFailingTests!)}", failure.Payload.Detail);
    }

    [Fact]
    public async Task LegacyPersistedPayload_DeserializesWithNullSharedTests()
    {
        using var fixture = new AcceptanceEngineMainSuspectReleaseTests.Fixture();
        var now = DateTimeOffset.UtcNow;
        var payload = new PostLandingCanaryEventPayload(PostLandingCanaryEventPayload.CanaryTag,
            AcceptanceEngineMainSuspectReleaseTests.S, [], "main-suspect", 1, "legacy", null, now);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("sharedFailingTests", json);
        await new SqliteRunEventStore(fixture.Events.Identity).AppendAsync(new RunEventAppend(
            RunEventTypes.PostLandingCanary, GoalId: null, Operation: "receipt", Status: "Failed",
            Detail: "legacy", PayloadJson: json, OccurredAt: now, EventId: "legacy-receipt"));
        Assert.Null(Assert.Single(await fixture.Events.ReadAllAsync()).Payload.SharedFailingTests);
    }
}
