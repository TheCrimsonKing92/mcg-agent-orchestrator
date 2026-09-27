using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStewardOwnerQuestion
{
    [Xunit.Theory]
    [Xunit.InlineData("irreversible")]
    [Xunit.InlineData("reopen-regate")]
    [Xunit.InlineData("wrong-cause")]
    [Xunit.InlineData("wrong-task")]
    [Xunit.InlineData("ask-owner")]
    public async Task Unsafe_or_owner_adjudication_holds_goal_without_an_intent_or_decision_request(string proposal)
    {
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        var expectedText = proposal == "ask-owner" ? "Please decide the repair" : "Please review this proposal";
        var output = proposal == "ask-owner"
            ? JsonSerializer.Serialize(new { kind = "ask-owner", question = "Please decide the repair",
                evidenceReferences = new[] { "worker-output=receipt-1" } })
            : JsonSerializer.Serialize(new
            {
                kind = proposal == "reopen-regate" ? "reopen-regate" : "route",
                targetTaskId = proposal == "wrong-task" ? TaskId.New().Value : harness.Task.Id.Value,
                cause = proposal == "wrong-cause" ? "MainDriftConflict" : "ContractClarification",
                text = "Please review this proposal",
                instruction = "Apply the repair",
                evidenceReferences = new[] { "worker-output=receipt-1" },
                reversibility = proposal == "irreversible" ? "irreversible" : "reversible"
            });
        harness.Model.Reply(output);
        harness.Host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;
        await harness.Host.CurrentRound!;
        harness.Host.ServiceTick(harness.Kernel);

        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Empty(await harness.Decisions.ListDecisionRequestsAsync(harness.Goal.Id.Value));
        Xunit.Assert.Equal(WorkTaskStatus.Failed, harness.Task.Status);
        Xunit.Assert.NotNull(harness.Goal.CurrentHold);
        Xunit.Assert.Equal("steward-owner-question", harness.Goal.CurrentHold.State);
        Xunit.Assert.Contains("worker-output=receipt-1", harness.Goal.CurrentHold.Blocker);
        Xunit.Assert.Contains(expectedText, harness.Goal.CurrentHold.Blocker);
        var escalation = File.ReadAllLines(harness.ConductPath)
            .Select(line => JsonDocument.Parse(line))
            .Single(document => document.RootElement.GetProperty("eventKind").GetString() == "goal-escalation");
        var conductDetail = escalation.RootElement.GetProperty("detail").GetString();
        Xunit.Assert.Contains("steward-owner-question", conductDetail);
        Xunit.Assert.Contains("worker-output=receipt-1", conductDetail);
        Xunit.Assert.Contains(expectedText, conductDetail);
        var lifecycle = File.ReadAllLines(Path.Combine(harness.Root, "lifecycle", $"{harness.Goal.Id.Value}.jsonl"))
            .Select(line => JsonDocument.Parse(line))
            .Single(document => document.RootElement.GetProperty("eventType").GetString() == "GoalEscalated");
        Xunit.Assert.Equal("steward-owner-question", lifecycle.RootElement.GetProperty("source").GetString());
        var lifecycleReason = lifecycle.RootElement.GetProperty("reason").GetString();
        Xunit.Assert.Contains("worker-output=receipt-1", lifecycleReason);
        Xunit.Assert.Contains(expectedText, lifecycleReason);
        Xunit.Assert.Empty(Directory.GetFiles(harness.Root, "*inbox*", SearchOption.AllDirectories));
    }
}
