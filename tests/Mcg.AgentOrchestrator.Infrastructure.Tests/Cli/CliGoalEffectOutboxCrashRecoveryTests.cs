using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalEffectOutboxCrashRecoveryTests : CliGoalEffectOutboxTestSupport
{
    [Xunit.Fact]
    public async Task ParkEffectBeforeMark_RetryPreservesResolutionAndSkipsLaterItems()
    {
        using var seed = await Seed.Create();
        await Raise(seed);
        OrchestratorStateOutboxMessage message;
        using (new PathBlocker(AttentionPath(seed)))
        {
            AssertEffectFailure(EffectTransition(seed, "park"), "attention resolution", GoalStatus.Parked);
            message = Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(ParkKind));
        }
        var store = CollaborationItemStore.ForDirectory(seed.Workspace.OrchestratorDirectory);
        // Execute the existing effect but leave the committed delivery unmarked, as a crash would.
        Xunit.Assert.Equal(1, await store.ResolveOpenForGoalAsync(seed.GoalId.Value, "Goal parked: Operator transition"));
        var resolved = await Item(seed);
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
        Xunit.Assert.NotNull(resolved.ResolvedAt);
        await Raise(seed, "late");
        await SetRaisedAt(seed, "late", ParkedAt(message).AddMinutes(1));
        Xunit.Assert.NotNull(await seed.Repository.GetOutboxStateAsync(message.Id));

        Xunit.Assert.Null(ParkOther(seed).Error);

        AssertItemUnchanged(resolved, await Item(seed));
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, (await Item(seed, "late")).Status);
        Xunit.Assert.Single((await store.ListAsync(seed.GoalId.Value)).Where(item => item.Status == CollaborationItemStatus.Resolved));
        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(message.Id));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AbandonEffectBeforeMark_RetryPreservesJournalAndWorktreeResult(bool live)
    {
        using var seed = await Seed.Create();
        using var cleanup = new GitFixtureCleanup(seed);
        var path = AddWorktree(seed);
        if (live) await AddLiveDispatch(seed);
        OrchestratorStateOutboxMessage message;
        using (new PathBlocker(GoalOperationJournal.PathFor(seed.Root, seed.GoalId)))
        {
            AssertEffectFailure(EffectTransition(seed, "abandon"), "abandon cleanup", GoalStatus.Cancelled);
            message = Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(AbandonKind));
        }
        var kernel = await seed.Repository.LoadGoalsAsync([seed.GoalId]);
        var goal = kernel.GetGoal(seed.GoalId);
        var hooks = WorktreeCleanupContext.Load(attentionStoreDirectory: seed.Workspace.OrchestratorDirectory).Hooks;
        if (live)
            GoalAbandonPlanner.RecordAbandonedTerminalDisposition(goal, seed.Workspace, "Operator transition");
        else
            _ = GoalAbandonPlanner.CompleteAfterCommit(kernel, goal, seed.Workspace, "Operator transition", hooks);
        var before = Journal(seed);
        Xunit.Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(before));
        Xunit.Assert.Equal(live, Directory.Exists(path));
        Xunit.Assert.NotNull(await seed.Repository.GetOutboxStateAsync(message.Id));

        Xunit.Assert.Null(ParkOther(seed).Error);

        var after = Journal(seed);
        Xunit.Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(after));
        Xunit.Assert.Equal(before.LatestByOperation, after.LatestByOperation);
        Xunit.Assert.Equal(before.Entries.Count, after.Entries.Count);
        Xunit.Assert.Equal(live, Directory.Exists(path));
        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(message.Id));
        if (!live)
        {
            var clean = GoalWorktrees.RemoveTerminal(seed.Root, seed.GoalId, kernel, hooks);
            Xunit.Assert.True(clean.IsComplete, clean.Message);
            Xunit.Assert.Equal("Workspace already clean; nothing to remove.", clean.Message);
            Xunit.Assert.Null(GoalWorktrees.TryResolve(seed.Root, seed.GoalId));
        }
    }
}
