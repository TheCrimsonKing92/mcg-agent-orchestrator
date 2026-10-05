using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalParkAttentionOutboxDeliveryTests : CliGoalEffectOutboxTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("park")]
    [Xunit.InlineData("stop-park")]
    public async Task ImmediateFailure_CommitsPark_AnotherWriterResolvesOnce(string command)
    {
        using var seed = await Seed.Create();
        await Raise(seed);
        using (new PathBlocker(AttentionPath(seed)))
        {
            var result = EffectTransition(seed, command);
            AssertEffectFailure(result, "attention resolution", GoalStatus.Parked);
            Xunit.Assert.Equal(GoalStatus.Parked, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
            Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(ParkKind));
        }

        var retry = ParkOther(seed);

        Xunit.Assert.Null(retry.Error);
        var resolved = await Item(seed);
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
        Xunit.Assert.Equal("Goal parked: Operator transition", resolved.Resolution);
        Xunit.Assert.NotNull(resolved.ResolvedAt);
        Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync(ParkKind));
        Xunit.Assert.Null(ParkOther(seed).Error);
        AssertItemUnchanged(resolved, await Item(seed));
    }

    [Xunit.Fact]
    public async Task RetryAfterUnpark_CompletesSkipped_LeavesAttentionOpen()
    {
        using var seed = await Seed.Create();
        await Raise(seed);
        OrchestratorStateOutboxMessage message;
        using (new PathBlocker(AttentionPath(seed)))
        {
            AssertEffectFailure(EffectTransition(seed, "park"), "attention resolution", GoalStatus.Parked);
            message = Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(ParkKind));
            var unpark = Transition(seed, "unpark");
            Xunit.Assert.Null(unpark.Error);
            Xunit.Assert.Equal(GoalStatus.Active, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
            Xunit.Assert.NotNull(await seed.Repository.GetOutboxStateAsync(message.Id));
        }
        await Raise(seed, "late");
        await SetRaisedAt(seed, "late", ParkedAt(message).AddMinutes(1));
        var late = await Item(seed, "late");
        var early = await Item(seed);
        var previousError = Console.Error;
        using var errors = new StringWriter();
        try
        {
            Console.SetError(errors);
            Xunit.Assert.Null(ParkOther(seed).Error);
        }
        finally { Console.SetError(previousError); }

        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(message.Id));
        AssertItemUnchanged(early, await Item(seed));
        AssertItemUnchanged(late, await Item(seed, "late"));
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, late.Status);
        Xunit.Assert.Contains($"park attention resolution outbox message '{message.Id}' skipped: goal no longer Parked", errors.ToString());
    }

    [Xunit.Fact]
    public async Task RetryWhileParked_ResolvesOnlyItemsAtOrBeforeCommit()
    {
        using var seed = await Seed.Create();
        await Raise(seed);
        OrchestratorStateOutboxMessage message;
        using (new PathBlocker(AttentionPath(seed)))
        {
            AssertEffectFailure(EffectTransition(seed, "park"), "attention resolution", GoalStatus.Parked);
            message = Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(ParkKind));
        }
        var commit = ParkedAt(message);
        await SetRaisedAt(seed, "early", commit.AddMinutes(-1));
        await Raise(seed, "boundary");
        await SetRaisedAt(seed, "boundary", commit);
        await Raise(seed, "late");
        await SetRaisedAt(seed, "late", commit.AddMinutes(1));

        Xunit.Assert.Null(ParkOther(seed).Error);

        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, (await Item(seed)).Status);
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, (await Item(seed, "boundary")).Status);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, (await Item(seed, "late")).Status);
        Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync(ParkKind));
    }
}
