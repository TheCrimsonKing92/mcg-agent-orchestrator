using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalAbandonCleanupOutboxDeliveryTests : CliGoalEffectOutboxTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("abandon", false)]
    [Xunit.InlineData("stop-abandon", false)]
    [Xunit.InlineData("abandon", true)]
    public async Task JournalFailure_CommitsAbandon_RetryHonorsCleanupBranch(string command, bool live)
    {
        using var seed = await Seed.Create();
        using var cleanup = new GitFixtureCleanup(seed);
        var path = AddWorktree(seed);
        if (live) await AddLiveDispatch(seed);
        using (new PathBlocker(GoalOperationJournal.PathFor(seed.Root, seed.GoalId)))
        {
            AssertEffectFailure(EffectTransition(seed, command), "abandon cleanup", GoalStatus.Cancelled);
            Xunit.Assert.Equal(GoalStatus.Cancelled, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
            Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(AbandonKind));
            // The lifecycle projection ran first even though cleanup failed.
            Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync("goal-lifecycle-event"));
            Xunit.Assert.True(Directory.Exists(path));
        }

        var retry = ParkOther(seed);

        Xunit.Assert.Null(retry.Error);
        Xunit.Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(Journal(seed)));
        Xunit.Assert.Equal(live, Directory.Exists(path));
        if (live)
        {
            Xunit.Assert.Equal(path, GoalWorktrees.TryResolve(seed.Root, seed.GoalId));
            Xunit.Assert.Null((await seed.Repository.LoadGoalAsync(seed.GoalId))!.Tasks.Single().LastProcess!.CompletedAt);
        }
        else
            Xunit.Assert.Null(GoalWorktrees.TryResolve(seed.Root, seed.GoalId));
        Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync(AbandonKind));
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
    }
}
