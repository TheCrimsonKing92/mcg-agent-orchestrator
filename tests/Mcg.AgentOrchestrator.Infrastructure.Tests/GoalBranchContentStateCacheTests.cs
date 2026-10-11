using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns its cache and uses no I/O or shared state.
public sealed class GoalBranchContentStateCacheTests
{
    [Xunit.Theory(DisplayName = "Conclusive states are returned for the same commit triple")]
    [Xunit.InlineData("NoCommitsAhead")]
    [Xunit.InlineData("EquivalentToMain")]
    [Xunit.InlineData("AbsentFromMain")]
    public void ConclusiveStateIsReturnedForSameTriple(string stateName)
    {
        var cache = new GoalBranchContentStateCache();
        var state = Enum.Parse<GoalBranchContentState>(stateName);

        cache.Store("main", "goal/branch", "tip", state);

        Assert.True(cache.TryGet("main", "goal/branch", "tip", out var actual));
        Assert.Equal(state, actual);
    }

    [Xunit.Theory(DisplayName = "Every key component uses exact ordinal equality")]
    [Xunit.InlineData("other-main", "goal/branch", "tip")]
    [Xunit.InlineData("MAIN", "goal/branch", "tip")]
    [Xunit.InlineData("main", "goal/other", "tip")]
    [Xunit.InlineData("main", "goal/BRANCH", "tip")]
    [Xunit.InlineData("main", "goal/branch", "other-tip")]
    [Xunit.InlineData("main", "goal/branch", "TIP")]
    public void DifferentTripleMisses(string main, string branch, string tip)
    {
        var cache = new GoalBranchContentStateCache();
        cache.Store("main", "goal/branch", "tip", GoalBranchContentState.AbsentFromMain);

        Assert.False(cache.TryGet(main, branch, tip, out _));
    }

    [Xunit.Theory(DisplayName = "Unchecked and inconclusive results remain cache misses")]
    [Xunit.InlineData("Inconclusive")]
    [Xunit.InlineData("NotChecked")]
    public void UnresolvedStateIsNeverStored(string stateName)
    {
        var cache = new GoalBranchContentStateCache();

        cache.Store("main", "goal/branch", "tip", Enum.Parse<GoalBranchContentState>(stateName));

        Assert.False(cache.TryGet("main", "goal/branch", "tip", out _));
    }

    [Xunit.Fact(DisplayName = "A lookup on a new main clears every old main entry")]
    public void TryGetNewMainDropsOldEntriesAndAdoptsNewMain()
    {
        var cache = new GoalBranchContentStateCache();
        cache.Store("old-main", "goal/one", "tip-one", GoalBranchContentState.AbsentFromMain);
        cache.Store("old-main", "goal/two", "tip-two", GoalBranchContentState.NoCommitsAhead);

        Assert.False(cache.TryGet("new-main", "goal/one", "tip-one", out _));
        cache.Store("new-main", "goal/one", "tip-one", GoalBranchContentState.EquivalentToMain);
        cache.Store("new-main", "goal/two", "tip-two", GoalBranchContentState.NoCommitsAhead);
        Assert.True(cache.TryGet("new-main", "goal/one", "tip-one", out var state));
        Assert.Equal(GoalBranchContentState.EquivalentToMain, state);
        Assert.True(cache.TryGet("new-main", "goal/two", "tip-two", out _));

        Assert.False(cache.TryGet("old-main", "goal/one", "tip-one", out _));
        Assert.False(cache.TryGet("old-main", "goal/two", "tip-two", out _));
    }

    [Xunit.Theory(DisplayName = "Storing on a new main clears old entries even for unresolved states")]
    [Xunit.InlineData("AbsentFromMain")]
    [Xunit.InlineData("Inconclusive")]
    [Xunit.InlineData("NotChecked")]
    public void StoreNewMainDropsOldEntries(string stateName)
    {
        var cache = new GoalBranchContentStateCache();
        cache.Store("old-main", "goal/one", "tip-one", GoalBranchContentState.AbsentFromMain);
        cache.Store("old-main", "goal/two", "tip-two", GoalBranchContentState.NoCommitsAhead);

        cache.Store("new-main", "goal/one", "tip-one", Enum.Parse<GoalBranchContentState>(stateName));

        Assert.False(cache.TryGet("old-main", "goal/one", "tip-one", out _));
        Assert.False(cache.TryGet("old-main", "goal/two", "tip-two", out _));
    }
}
