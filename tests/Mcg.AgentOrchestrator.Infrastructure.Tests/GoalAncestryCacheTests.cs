using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using AncestryFixture = TerminalGoalSweepAncestryReuseTests.AncestryFixture;

// Parallel-safe: cache state and metadata fixtures are private to each test.
public sealed class GoalAncestryCacheTests
{
    [Xunit.Fact]
    public void Run_MainChanges_QueriesNewPairAndDiscardsPreviousMain()
    {
        using var fixture = new AncestryFixture();
        fixture.AddObligation();
        var cache = new TerminalGoalSweepCache();
        var previousMain = fixture.Metadata.MainSha;
        fixture.Run(cache);
        Assert.Equal(1, fixture.AncestrySpawnCount);
        Assert.True(cache.Ancestry.TryGet(previousMain, fixture.Metadata.TipSha, out var oldFact));
        Assert.False(oldFact);

        fixture.Metadata.Change("integration");
        fixture.ExitCode = 0;
        fixture.WriteAttempt("passed", fixture.Metadata.TipSha);
        fixture.Run(cache);

        Assert.Equal(2, fixture.AncestrySpawnCount);
        Assert.Equal(fixture.AncestryCommand(fixture.Metadata.TipSha),
            fixture.Metadata.Commands.Last(AncestryFixture.IsAncestryCommand));
        AssertRecorded(fixture, fixture.Metadata.TipSha);
        Assert.Equal(1, cache.Ancestry.Count);
        Assert.True(cache.Ancestry.TryGet(fixture.Metadata.MainSha, fixture.Metadata.TipSha, out var newFact));
        Assert.True(newFact);
        Assert.False(cache.Ancestry.TryGet(previousMain, fixture.Metadata.TipSha, out _));
        Assert.False(cache.Ancestry.TryGet(fixture.Metadata.MainSha, fixture.Metadata.TipSha, out _));
    }

    [Xunit.Fact]
    public void Run_GoalTipChanges_QueriesNewCandidateAndReportsNewFact()
    {
        using var fixture = new AncestryFixture();
        fixture.AddObligation();
        var cache = new TerminalGoalSweepCache();
        var previousTip = fixture.Metadata.TipSha;
        fixture.Run(cache);
        Assert.Equal(1, fixture.AncestrySpawnCount);

        fixture.Metadata.Change("goal-text");
        fixture.ExitCode = 0;
        fixture.BindCandidate(fixture.Metadata.TipSha);
        fixture.WriteAttempt("passed", fixture.Metadata.TipSha);
        fixture.Run(cache);

        Assert.Equal(2, fixture.AncestrySpawnCount);
        Assert.Equal(fixture.AncestryCommand(fixture.Metadata.TipSha),
            fixture.Metadata.Commands.Last(AncestryFixture.IsAncestryCommand));
        AssertRecorded(fixture, fixture.Metadata.TipSha);
        Assert.True(cache.Ancestry.TryGet(fixture.Metadata.MainSha, previousTip, out var oldFact));
        Assert.False(oldFact);
        Assert.True(cache.Ancestry.TryGet(fixture.Metadata.MainSha, fixture.Metadata.TipSha, out var newFact));
        Assert.True(newFact);
        Assert.Equal(2, cache.Ancestry.Count);
        AssertMainChangeDiscards(cache.Ancestry, fixture.Metadata.MainSha, previousTip);
    }

    [Xunit.Fact]
    public void Run_ReceiptCandidateChanges_QueriesDistinctCandidateAndReportsNewFact()
    {
        using var fixture = new AncestryFixture { ExitCode = 0 };
        fixture.AddObligation();
        var cache = new TerminalGoalSweepCache();
        var previousCandidate = new string('3', 40);
        fixture.CandidateExitCodes[previousCandidate] = 1;
        fixture.WriteAttempt("a-passed", previousCandidate);
        fixture.Run(cache);
        fixture.AssertPending();
        Assert.Equal(2, fixture.AncestrySpawnCount);
        Assert.True(cache.Ancestry.TryGet(fixture.Metadata.MainSha, previousCandidate, out var oldFact));
        Assert.False(oldFact);

        var nextCandidate = new string('4', 40);
        fixture.BindCandidate(nextCandidate);
        fixture.WriteAttempt("b-passed", nextCandidate);
        fixture.Run(cache);

        Assert.Equal(3, fixture.AncestrySpawnCount);
        Assert.Equal(fixture.AncestryCommand(nextCandidate),
            fixture.Metadata.Commands.Last(AncestryFixture.IsAncestryCommand));
        AssertRecorded(fixture, nextCandidate);
        Assert.True(cache.Ancestry.TryGet(fixture.Metadata.MainSha, nextCandidate, out var newFact));
        Assert.True(newFact);
        Assert.Equal(3, cache.Ancestry.Count);
        AssertMainChangeDiscards(cache.Ancestry, fixture.Metadata.MainSha, previousCandidate);
    }

    [Xunit.Theory]
    [Xunit.InlineData(128)]
    [Xunit.InlineData(-1)]
    public void Run_IndeterminateExit_RetriesInsteadOfCachingFalse(int exitCode)
    {
        using var fixture = new AncestryFixture { ExitCode = exitCode };
        fixture.AddObligation();
        var cache = new TerminalGoalSweepCache();
        fixture.Run(cache);
        fixture.Run(cache);

        Assert.Equal(2, fixture.AncestrySpawnCount);
        Assert.Equal(0, cache.Ancestry.Count);
        Assert.False(cache.Ancestry.TryGet(fixture.Metadata.MainSha, fixture.Metadata.TipSha, out _));
        fixture.AssertPending();

        fixture.ExitCode = 0;
        fixture.WriteAttempt("passed", fixture.Metadata.TipSha);
        fixture.Run(cache);
        Assert.Equal(3, fixture.AncestrySpawnCount);
        AssertRecorded(fixture, fixture.Metadata.TipSha);
    }

    [Xunit.Fact]
    public void Run_ExitOne_ReusesStoredFalseWithoutChangingGoal()
    {
        using var fixture = new AncestryFixture();
        fixture.AddObligation();
        var cache = new TerminalGoalSweepCache();
        fixture.Run(cache);
        fixture.Run(cache);

        Assert.Equal(1, fixture.AncestrySpawnCount);
        Assert.Equal(1, cache.Ancestry.Count);
        Assert.True(cache.Ancestry.TryGet(fixture.Metadata.MainSha, fixture.Metadata.TipSha, out var fact));
        Assert.False(fact);
        fixture.AssertPending();
    }

    [Xunit.Fact]
    public void Run_NullCache_QueriesAncestryEveryTick()
    {
        using var fixture = new AncestryFixture();
        fixture.AddObligation();
        for (var tick = 0; tick < 3; tick++)
        {
            fixture.Run(null);
            Assert.Equal(tick + 1, fixture.AncestrySpawnCount);
            fixture.AssertPending();
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void TryGet_CommitIdCaseDiffers_MissesOrdinalKey(bool changeMain)
    {
        var cache = new GoalAncestryCache();
        cache.Store("main-a", "candidate-a", true);
        Assert.False(cache.TryGet(changeMain ? "MAIN-A" : "main-a",
            changeMain ? "candidate-a" : "CANDIDATE-A", out _));
        if (changeMain)
        {
            Assert.Equal(0, cache.Count);
            Assert.False(cache.TryGet("main-a", "candidate-a", out _));
        }
        else
        {
            Assert.True(cache.TryGet("main-a", "candidate-a", out var fact));
            Assert.True(fact);
        }
    }

    [Xunit.Fact]
    public void Store_DuplicateCandidatesAndNewMain_BoundsEntriesToCurrentMain()
    {
        var cache = new GoalAncestryCache();
        cache.Store("main-a", "candidate-a", true);
        cache.Store("main-a", "candidate-a", false);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("main-a", "candidate-a", out var updated));
        Assert.False(updated);
        cache.Store("main-a", "candidate-b", true);
        Assert.Equal(2, cache.Count);

        cache.Store("main-b", "candidate-c", true);
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet("main-b", "candidate-a", out _));
        Assert.False(cache.TryGet("main-b", "candidate-b", out _));
        Assert.True(cache.TryGet("main-b", "candidate-c", out var current));
        Assert.True(current);
        Assert.False(cache.TryGet("main-a", "candidate-a", out _));
        Assert.Equal(0, cache.Count);
    }

    private static void AssertRecorded(AncestryFixture fixture, string candidate)
    {
        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        var obligation = Assert.Single(fixture.Goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
        Assert.Equal(candidate, obligation.CandidateSha);
    }

    private static void AssertMainChangeDiscards(GoalAncestryCache cache, string main, string candidate)
    {
        Assert.False(cache.TryGet(new string('9', 40), candidate, out _));
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(main, candidate, out _));
    }
}
