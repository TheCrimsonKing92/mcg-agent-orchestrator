using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalEffectOutboxValidationTests : CliGoalEffectOutboxTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("goal-park-attention-resolution", "{")]
    [Xunit.InlineData("goal-park-attention-resolution", "{}")]
    [Xunit.InlineData("goal-abandon-after-commit", "{")]
    [Xunit.InlineData("goal-abandon-after-commit", "{}")]
    public async Task InvalidPayload_Quarantines_AnotherWriterContinues(string kind, string payload)
    {
        using var seed = await Seed.Create();
        var message = new OrchestratorStateOutboxMessage(Guid.NewGuid().ToString("N"), kind, payload,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        await seed.Repository.EnsureOutboxMessageAsync(message);

        Xunit.Assert.Null(ParkOther(seed).Error);

        Xunit.Assert.Equal(OrchestratorStateOutboxStatus.Quarantined,
            (await seed.Repository.GetOutboxStateAsync(message.Id))!.Status);
        Xunit.Assert.False(GoalOperationJournal.HasRetiredTerminalDisposition(Journal(seed)));
    }

    [Xunit.Fact]
    public async Task AbandonDelivery_WhenGoalIsActive_CompletesWithoutEffect()
    {
        using var seed = await Seed.Create();
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var payload = JsonSerializer.Serialize(new
        {
            goalId = seed.GoalId.Value, committedAt = at, reason = "Operator transition",
            leaveLiveDispatchesRunning = false, lifecycleMessageId = (string?)null
        });
        var message = new OrchestratorStateOutboxMessage(Guid.NewGuid().ToString("N"), AbandonKind, payload, at);
        await seed.Repository.EnsureOutboxMessageAsync(message);

        Xunit.Assert.Null(ParkOther(seed).Error);

        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(message.Id));
        Xunit.Assert.Equal(GoalStatus.Active, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        Xunit.Assert.False(GoalOperationJournal.HasRetiredTerminalDisposition(Journal(seed)));
    }

    [Xunit.Fact]
    public async Task PendingLifecycle_BlocksCleanupUntilProjectionSucceeds()
    {
        using var seed = await Seed.Create();
        Directory.CreateDirectory(seed.EventPath);
        var result = EffectTransition(seed, "abandon");
        Xunit.Assert.IsType<Mcg.AgentOrchestrator.App.Cli.CliCommandHandlers.GoalLifecycleProjectionException>(result.Error);
        var cleanup = Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(AbandonKind));
        Xunit.Assert.False(GoalOperationJournal.HasRetiredTerminalDisposition(Journal(seed)));

        Xunit.Assert.Null(ParkOther(seed).Error);
        Xunit.Assert.NotNull(await seed.Repository.GetOutboxStateAsync(cleanup.Id));
        Xunit.Assert.False(GoalOperationJournal.HasRetiredTerminalDisposition(Journal(seed)));
        Directory.Delete(seed.EventPath);

        Xunit.Assert.Null(ParkOther(seed).Error);
        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(cleanup.Id));
        Xunit.Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(Journal(seed)));
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
    }
}
