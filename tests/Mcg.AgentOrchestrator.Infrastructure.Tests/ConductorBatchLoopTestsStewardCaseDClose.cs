using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all stores and files belong to a unique StewardCaseDHarness root.
public sealed class ConductorBatchLoopTestsStewardCaseDClose
{
    [Xunit.Fact]
    public async Task Case_D_close_completes_Developer_with_verbatim_result_and_one_regate()
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed(rejection: false);
        ReplyClose(harness);

        await Harvest(harness);

        var intent = Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        var payload = JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal("close", payload.Shape);
        Assert.Equal("steward", intent.Actor);
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Assert.Equal("conductor-steward", intent.Channel);
        Assert.Equal(OperatorIntentAdjudication.StewardAssurance, intent.AuthenticationAssurance);
        Assert.Contains(StewardCaseDHarness.WorkerResult, payload.Text, StringComparison.Ordinal);
        Assert.StartsWith("Candidate rerun passed; failure is outside changed paths.", payload.Text, StringComparison.Ordinal);
        Assert.Contains("steward-case=D", payload.EvidenceReferences);
        Assert.Equal(WorkTaskStatus.Failed, harness.Task.Status);
        Assert.Equal(0, AcceptanceFailingTestIndex.CountRegates(harness.Index.Read(), harness.Goal.Id.Value));

        var result = harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal);
        harness.Coordinator.CompletePersisted([harness.Goal.Id]);

        Assert.True(result.MutatedGoalState);
        Assert.Equal(WorkTaskStatus.Completed, harness.Task.Status);
        Assert.True(harness.Task.LastVerification?.Succeeded);
        Assert.Equal(GoalStatus.Verified, harness.Goal.Status); // The fresh gate still owns landing.
        var record = Assert.Single(harness.Index.Read().Where(row => row.Kind == AcceptanceFailingTestIndexKinds.ApparatusRegate));
        Assert.Equal(harness.Goal.Id.Value, record.GoalId);
        Assert.Equal(StewardCaseDHarness.Sha, record.CandidateSha);
        Assert.Equal(StewardCaseDHarness.TestIdentity, record.TestIdentity);
        var decision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal(EffectReceiptStatus.Applied, decision?.Effect?.Status);
        Assert.True(OperatorActorIdentity.TryParse(decision!.Receipt!.ActorId, out var actor, out var actorKind));
        Assert.Equal("steward", actor);
        Assert.Equal(OperatorActorKind.Agent, actorKind);
        Assert.Single(harness.Goal.Timeline.Where(item => item.OperatorIntentApplied?.Actor == "steward" &&
            item.OperatorIntentApplied.ActorKind == OperatorActorKind.Agent && item.OperatorIntentApplied.Outcome == "applied"));
    }

    [Xunit.Fact]
    public async Task Spent_regate_cap_escalates_without_submitting_close()
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed();
        harness.SeedRegates(harness.Sources.RegateCap);
        ReplyClose(harness);

        await Harvest(harness);

        Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Assert.Equal(WorkTaskStatus.Failed, harness.Task.Status);
        Assert.Equal(harness.Sources.RegateCap, AcceptanceFailingTestIndex.CountRegates(harness.Index.Read(), harness.Goal.Id.Value));
        Assert.Equal("steward-owner-question", harness.Goal.CurrentHold?.State);
        Assert.Contains("apparatus re-gate cap spent", harness.Goal.CurrentHold?.Blocker, StringComparison.Ordinal);
        var events = File.ReadAllLines(harness.ConductPath).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Contains(events, item => item.RootElement.GetProperty("eventKind").GetString() == "goal-escalation" &&
                item.RootElement.GetRawText().Contains("apparatus re-gate cap spent", StringComparison.Ordinal));
        }
        finally { foreach (var item in events) item.Dispose(); }
    }

    [Xunit.Fact]
    public async Task Repeated_case_D_on_same_candidate_asks_owner_after_one_applied_action()
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed();
        ReplyClose(harness);
        await Harvest(harness);
        harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal);
        harness.Coordinator.CompletePersisted([harness.Goal.Id]);
        harness.Host.ServiceTick(harness.Kernel); // Reconcile the submitted action into the existing bound.
        var trigger = harness.Detector.Detect(harness.Goal); // Completed tasks cannot trigger a close.
        Assert.Empty(trigger);

        harness.Seed();
        harness.Host.ServiceTick(harness.Kernel);

        Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Assert.Equal(1, harness.Model.Calls);
        Assert.Equal(1, AcceptanceFailingTestIndex.CountRegates(harness.Index.Read(), harness.Goal.Id.Value));
        Assert.Equal("steward-owner-question", harness.Goal.CurrentHold?.State);
        Assert.Equal(WorkTaskStatus.Failed, harness.Task.Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData("route")]
    [Xunit.InlineData("reopen-regate")]
    [Xunit.InlineData("verify-manual")]
    public async Task Case_D_other_action_shapes_ask_owner_without_an_intent(string kind)
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed();
        harness.Model.Reply(JsonSerializer.Serialize(new
        {
            kind, targetTaskId = harness.Task.Id.Value, text = "diagnosis", instruction = "instruction",
            cause = "NewTestFinding", reversibility = "reversible", evidenceReferences = new[] { "receipt=1" }
        }));

        await Harvest(harness);

        Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Assert.Equal("steward-owner-question", harness.Goal.CurrentHold?.State);
        Assert.Contains("disallowed-action", harness.Goal.CurrentHold?.Blocker, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, harness.Task.Status);
    }

    private static void ReplyClose(StewardCaseDHarness harness) => harness.Model.Reply(JsonSerializer.Serialize(new
    {
        kind = "close", targetTaskId = harness.Task.Id.Value,
        text = "Candidate rerun passed; failure is outside changed paths.",
        evidenceReferences = new[] { $"failing-test={StewardCaseDHarness.TestIdentity}" }
    }));

    private static async Task Harvest(StewardCaseDHarness harness)
    {
        harness.Host.ServiceTick(harness.Kernel);
        Assert.NotNull(harness.Host.CurrentRound);
        try { await harness.Host.CurrentRound!.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException ex) { throw new InvalidOperationException("Steward case D model round did not complete.", ex); }
        Assert.Equal(1, harness.Model.Calls);
        harness.Host.ServiceTick(harness.Kernel);
    }
}
