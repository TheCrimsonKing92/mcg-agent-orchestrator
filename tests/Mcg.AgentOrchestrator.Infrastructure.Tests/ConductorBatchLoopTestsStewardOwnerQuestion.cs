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
        Xunit.Assert.Contains("Please", harness.Goal.CurrentHold.Blocker);
        var conduct = File.ReadAllText(harness.ConductPath);
        Xunit.Assert.Contains("goal-escalation", conduct);
        Xunit.Assert.Contains("steward-owner-question", conduct);
        var lifecycle = Directory.GetFiles(Path.Combine(harness.Root, "lifecycle"), "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText).ToArray();
        Xunit.Assert.Contains(lifecycle, text => text.Contains("GoalEscalated", StringComparison.Ordinal) &&
            text.Contains("steward-owner-question", StringComparison.Ordinal));
        Xunit.Assert.Empty(Directory.GetFiles(harness.Root, "*inbox*", SearchOption.AllDirectories));
    }
}
