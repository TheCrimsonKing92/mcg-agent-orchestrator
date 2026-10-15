using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: injected clock and model; each fixture owns its temporary SQLite stores and logs.
public sealed class ConductorStewardCaseGRemedyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const ConductorStewardTriggerKind Kind = ConductorStewardTriggerKind.VerifyingWithoutLiveGate;

    [Xunit.Fact]
    public async Task Stranded_goal_queues_one_evidenced_intent_without_a_model_round()
    {
        using var h = new StewardCaseGHarness();
        var trigger = Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow));
        h.Host.ServiceTick(h.Kernel);
        var intent = Assert.Single(await h.Intents.ListForGoalAsync(h.Goal.Id.Value));
        Assert.Equal(OperatorIntentVerbs.Adjudicate, intent.Verb);
        Assert.Equal("steward", intent.Actor);
        Assert.Equal("conductor-steward", intent.Channel);
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Assert.Equal(OperatorIntentAdjudication.StewardAssurance, intent.AuthenticationAssurance);
        Assert.Equal(h.Target.Id.Value, intent.TaskId);
        var payload = Payload(intent);
        Assert.Equal("reopen-regate", payload.Shape);
        Assert.Contains($"quiet-since={h.QuietSince:O}", payload.Text);
        Assert.Contains("no live acceptance attempt", payload.Text);
        Assert.EndsWith(StewardCaseGHarness.WorkerResult, payload.Text);
        Assert.StartsWith("steward-case=G trigger=", payload.Precedent);
        Assert.Equal("reversible-with-cost", payload.Reversibility);
        Assert.NotNull(payload.Precondition);
        Assert.Equal(7, payload.ExpectedGoalStateVersion);
        Assert.Contains($"steward-trigger={ConductorStewardTriggerStore.KeyFor(trigger)}", payload.EvidenceReferences);
        Assert.Single(payload.EvidenceReferences.Where(reference => reference.StartsWith("steward-case=")));
        Assert.Equal(1, h.Triggers.CountRemedies(Kind));
        Assert.Equal(0, h.Triggers.CountRemedies(ConductorStewardTriggerKind.DeveloperGateReopenNoCommit));

        h.Host.ServiceTick(h.Kernel);

        Assert.Single(await h.Intents.ListForGoalAsync(h.Goal.Id.Value));
        Assert.Equal(1, h.Triggers.CountRemedies(Kind));
        var conduct = File.ReadAllLines(h.ConductPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, Json)!)
            .Where(record => record.EventKind == "steward").ToArray();
        var entry = Assert.Single(conduct);
        Assert.Contains("kind=reopen-regate case=G reason=reopen-regate-submitted", entry.Detail);
        Assert.Contains($"intent={intent.Id}", entry.Detail);
        Assert.Equal(h.Goal.Id.Value, entry.GoalId);
        Assert.Equal(0, h.Model.Calls);
        Assert.Null(h.Host.CurrentRound);
    }

    [Xunit.Fact]
    public async Task Twenty_nine_minutes_queues_no_remedy()
    {
        using var h = new StewardCaseGHarness(quietMinutes: 29);
        h.Host.ServiceTick(h.Kernel);
        Assert.Empty(await h.Intents.ListForGoalAsync(h.Goal.Id.Value));
        Assert.Equal(0, h.Triggers.CountRemedies(Kind));
        Assert.Equal(0, h.Model.Calls);
    }

    [Xunit.Fact]
    public async Task Applied_remedy_regates_then_repeat_on_same_head_asks_owner_once()
    {
        using var h = new StewardCaseGHarness();
        h.Host.ServiceTick(h.Kernel);
        var intent = Assert.Single(await h.Intents.ListForGoalAsync(h.Goal.Id.Value));
        var prior = h.Target.LastVerification;
        var decisions = CollaborationItemStore.ForDirectory(h.Root);
        var coordinator = Coordinator(h, h.Intents, decisions, _ => false);

        var result = coordinator.ExecutePending(h.Kernel, h.Goal);

        Assert.True(result.MutatedGoalState);
        Assert.Equal(GoalStatus.Verified, h.Goal.Status);
        Assert.Equal(WorkTaskStatus.Completed, h.Target.Status);
        Assert.True(h.Target.LastVerification?.Succeeded);
        Assert.NotSame(prior, h.Target.LastVerification);
        Assert.Equal(Payload(intent).Text, h.Target.AcceptedRetryFeedback?.Message);
        var state = await decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal(EffectReceiptStatus.Applied, state!.Effect!.Status);
        coordinator.CompletePersisted([h.Goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await h.Intents.GetAsync(intent.Id))!.Status);

        Assert.True(h.Kernel.BeginGoalAcceptanceVerification(h.Goal.Id, "gate started again"));
        h.Clock.Advance(TimeSpan.FromMinutes(31));
        var recurrence = Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow));
        Assert.True(recurrence.OccurredAt > h.QuietSince);
        Assert.Equal(StewardCaseGHarness.Sha, recurrence.CandidateSha);
        h.Host.ServiceTick(h.Kernel);
        h.Host.ServiceTick(h.Kernel);

        Assert.Equal("steward-owner-question", h.Goal.CurrentHold?.State);
        Assert.Contains("case=G", h.Goal.CurrentHold!.Blocker);
        Assert.Single(await h.Intents.ListForGoalAsync(h.Goal.Id.Value));
        Assert.Equal(1, h.Triggers.CountRemedies(Kind));
        Assert.True(h.Triggers.Observe(recurrence).RepeatQuestionRaised);
        var escalations = File.ReadAllLines(h.ConductPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, Json)!)
            .Where(record => record.EventKind == "goal-escalation");
        Assert.Single(escalations);
        Assert.Equal(0, h.Model.Calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData("live", "acceptance-attempt-live")]
    [Xunit.InlineData("missing-case", "steward-capability-boundary")]
    [Xunit.InlineData("recent-event", "steward-capability-boundary")]
    [Xunit.InlineData("duplicate-case", "steward-capability-boundary")]
    [Xunit.InlineData("wrong-case", "steward-capability-boundary")]
    [Xunit.InlineData("wrong-precedent", "steward-capability-boundary")]
    public async Task Unsafe_apply_rejects_without_changing_goal_state(string scenario, string reason)
    {
        using var h = new StewardCaseGHarness();
        h.Host.ServiceTick(h.Kernel);
        var intent = Assert.Single(await h.Intents.ListForGoalAsync(h.Goal.Id.Value));
        var payload = Payload(intent);
        var store = h.Intents;
        if (scenario is "missing-case" or "duplicate-case" or "wrong-case" or "wrong-precedent")
        {
            payload = scenario switch
            {
                "missing-case" => payload with { EvidenceReferences = payload.EvidenceReferences
                    .Where(reference => reference != "steward-case=G").ToArray() },
                "duplicate-case" => payload with { EvidenceReferences = payload.EvidenceReferences
                    .Append("steward-case=G").ToArray() },
                "wrong-case" => payload with { EvidenceReferences = payload.EvidenceReferences
                    .Select(reference => reference == "steward-case=G" ? "steward-case=D" : reference).ToArray() },
                _ => payload with { Precedent = "steward-case=D trigger=wrong" }
            };
            // Replay the host's intent with only the admission discriminant changed.
            store = new SqliteOperatorIntentStore(Path.Combine(h.Root, "rejection-intents.db"),
                Path.Combine(h.Root, "rejection-logs"));
            intent = await store.EnqueueAsync(intent with { PayloadJson = JsonSerializer.Serialize(payload, Json) });
        }
        if (scenario == "recent-event")
        {
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            h.Kernel.RecordGoalPolicyDecision(h.Goal.Id, "New acceptance activity");
        }
        var before = SnapshotWithoutTimeline(h);
        var decisions = CollaborationItemStore.ForDirectory(h.Root);
        var coordinator = Coordinator(h, store, decisions, _ => scenario == "live");

        var result = coordinator.ExecutePending(h.Kernel, h.Goal);

        Assert.False(result.MutatedGoalState);
        Assert.True(result.RejectedAdjudication);
        Assert.Equal(before, SnapshotWithoutTimeline(h));
        var state = await decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal(EffectReceiptStatus.Rejected, state!.Effect!.Status);
        Assert.Equal(reason, state.Effect.Result);
        if (scenario == "live")
        {
            h.Host.ServiceTick(h.Kernel);
            Assert.Equal(0, h.Triggers.CountRemedies(Kind));
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Unavailable_version_services_trigger_without_model_dispatch(bool throws)
    {
        using var h = new StewardCaseGHarness(version: _ => throws ?
            throw new IOException("version unavailable") : null);
        var trigger = Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow));
        h.Host.ServiceTick(h.Kernel);
        h.Host.ServiceTick(h.Kernel);
        Assert.Empty(await h.Intents.ListForGoalAsync(h.Goal.Id.Value));
        Assert.Equal(0, h.Triggers.CountRemedies(Kind));
        Assert.Equal("serviced", h.Triggers.Observe(trigger).Status);
        Assert.Equal("no-action", h.Triggers.Observe(trigger).Outcome);
        Assert.Contains(throws ? "case-G-failure:IOException" : "goal-version-unavailable",
            File.ReadAllText(h.ConductPath));
        Assert.Equal(0, h.Model.Calls);
        Assert.Null(h.Host.CurrentRound);
    }

    private static AdjudicateOperatorIntentPayload Payload(OperatorIntentRecord intent) =>
        JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(intent.PayloadJson, Json)!;

    private static string SnapshotWithoutTimeline(StewardCaseGHarness h) =>
        JsonSerializer.Serialize(h.Kernel.ExportGoalSnapshot(h.Goal.Id) with { Timeline = [] }, Json);

    private static OperatorIntentCoordinator Coordinator(StewardCaseGHarness h, SqliteOperatorIntentStore store,
        CollaborationItemStore decisions, Func<GoalId, bool> live) =>
        new(store, utcNow: () => h.Clock.UtcNow, decisions: decisions, goalStateVersionResolver: _ => 7,
            evidenceResolver: new AdjudicationEvidenceResolver(h.Root), hasLiveAcceptanceAttempt: live);
}
