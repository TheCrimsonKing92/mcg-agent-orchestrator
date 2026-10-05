using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalLifecycleOutboxValidationTests : CliGoalLifecycleOutboxTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("mismatched-kind")]
    [Xunit.InlineData("unsupported-kind")]
    public async Task InvalidContent_IsQuarantinedWithExistingWarning_WithoutEventsLine(string poison)
    {
        using var seed = await Seed.Create();
        var kind = poison == "unsupported-kind" ? ProgressKind.TaskCompleted : ProgressKind.GoalSuperseded;
        var message = GoalLifecycleEventOutbox.CreateMessage(new ProgressEvent(
            seed.GoalId, null, kind, "Poison", DateTimeOffset.UnixEpoch));
        // Pin the discriminators independently of CreateMessage so the old validation is exercised too.
        var payload = JsonNode.Parse(message.PayloadJson)!;
        payload["eventKind"] = poison == "mismatched-kind" ? "GoalCancelled" : "TaskCompleted";
        message = message with { PayloadJson = payload.ToJsonString() };
        await seed.Repository.EnsureOutboxMessageAsync(message);
        CommandResult? result = null;

        var stderr = AsyncLocalConsoleRouter.CaptureError(() => result = ParkOther(seed));

        Xunit.Assert.Null(result!.Error);
        Xunit.Assert.True(result.Changed);
        Xunit.Assert.Contains(
            $"Warning: goal lifecycle event outbox message '{message.Id}' was quarantined: Invalid goal lifecycle event payload:", stderr);
        Xunit.Assert.Equal(OrchestratorStateOutboxStatus.Quarantined,
            (await seed.Repository.GetOutboxStateAsync(message.Id))!.Status);
        Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync(GoalLifecycleEventOutbox.Kind));
        Xunit.Assert.False(File.Exists(seed.EventPath));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task StoredCancellation_WriterDeliversWithLegacyOrMissingDiscriminator(bool missingKind)
    {
        using var seed = await Seed.Create();
        var message = GoalLifecycleEventOutbox.CreateMessage(new ProgressEvent(
            seed.GoalId, null, ProgressKind.GoalCancelled, "Legacy cancellation", DateTimeOffset.UnixEpoch));
        using (var document = JsonDocument.Parse(message.PayloadJson))
            Xunit.Assert.Equal("GoalCancelled", document.RootElement.GetProperty("eventKind").GetString());
        if (missingKind)
        {
            var payload = JsonNode.Parse(message.PayloadJson)!.AsObject();
            payload.Remove("eventKind");
            message = message with { PayloadJson = payload.ToJsonString() };
        }
        await seed.Repository.EnsureOutboxMessageAsync(message);

        Xunit.Assert.Null(ParkOther(seed).Error);

        AssertOneLine(seed, "GoalCancelled", message.Id);
        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(message.Id));
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
    }

    [Xunit.Theory]
    [Xunit.InlineData(ProgressKind.GoalCancelled)]
    [Xunit.InlineData(ProgressKind.GoalSuperseded)]
    [Xunit.InlineData(ProgressKind.GoalPolicyDecision)]
    public async Task AcceptedKind_MustMatchDiscriminatorAndGoal(ProgressKind kind)
    {
        using var seed = await Seed.Create();
        var message = GoalLifecycleEventOutbox.CreateMessage(new ProgressEvent(
            seed.GoalId, null, kind, "Committed event", DateTimeOffset.UnixEpoch));
        using var document = JsonDocument.Parse(message.PayloadJson);
        Xunit.Assert.Equal(kind.ToString(), document.RootElement.GetProperty("eventKind").GetString());
        await seed.Repository.EnsureOutboxMessageAsync(message);

        Xunit.Assert.Null(ParkOther(seed).Error);

        var eventType = kind == ProgressKind.GoalPolicyDecision ? "GoalLifecycleDecision" : kind.ToString();
        AssertOneLine(seed, eventType, message.Id);
        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(message.Id));
    }
}
