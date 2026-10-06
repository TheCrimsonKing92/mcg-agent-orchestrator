public sealed class ConductorJudgePanelApparatusCursorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PriorTimelineCopyPairsAcrossTicksAndRelaunch(bool relaunch)
    {
        using var h = new PanelIncrementalTestFixture();
        await h.Fixture.SaveCriteria();
        var f = h.Fixture;
        f.Timeline(1, "Acceptance RED classified as apparatus (test-host); restored Verified for re-gate 1/2.", decision: true);
        h.Tick();
        var first = Assert.Single(f.Panel.Store.Cases());
        if (relaunch) h.Relaunch();
        f.Panel.Time.UtcNow += TimeSpan.FromSeconds(1);
        f.Conduct("goal", $"Acceptance_RED_classified_as_apparatus_(test-host) candidate_sha={f.Panel.Candidate} base_sha={f.BaseSha} Re-gating on the next conduct tick (1/2)", goalId: f.GoalId[..8]);
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(1, h.Reads.EventLines);
        Assert.Equal(0, h.Reads.GoalReads); // a suppressed copy never reaches enrollment
        Assert.Equal(first, Assert.Single(f.Panel.Store.Cases()));
        var fullRead = Assert.Single(f.Sources.Read());
        Assert.Equal(first.Key.TriggerId, fullRead.TriggerId);
        Assert.Equal(h.FullReadKey(fullRead.TriggerId), first.Key);

        // A rotated copy must not consume a second timeline ordinal, including after relaunch.
        File.Move(f.Panel.ConductPath, Path.Combine(f.Panel.Root, "conduct-events-20300101.log"));
        h.Relaunch();
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(1, h.Reads.EventLines);
        Assert.Equal(0, h.Reads.GoalReads);
        Assert.Equal(first, Assert.Single(f.Panel.Store.Cases()));
        Assert.Equal(first.Key.TriggerId, Assert.Single(f.Sources.Read()).TriggerId);
    }

    [Fact]
    public async Task PairedConductCannotConsumeAnotherTimelineCopyOnRescan()
    {
        using var h = new PanelIncrementalTestFixture();
        await h.Fixture.SaveCriteria();
        var f = h.Fixture;
        f.Timeline(1, "Acceptance RED classified as apparatus (test-host); restored Verified for re-gate 1/2.", decision: true);
        h.Tick();
        f.Panel.Time.UtcNow += TimeSpan.FromSeconds(1);
        f.Conduct("goal", "Acceptance RED classified as apparatus (test-host); Re-gating on the next conduct tick (1/2)", goalId: f.GoalId[..8]);
        h.Tick();
        f.Panel.Time.UtcNow += TimeSpan.FromSeconds(1);
        f.Timeline(2, "Acceptance RED classified as apparatus (test-host); restored Verified for re-gate 1/2.", decision: true);
        h.Tick();
        File.Move(f.Panel.ConductPath, Path.Combine(f.Panel.Root, "conduct-events-20300101.log"));
        h.Relaunch();
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(1, h.Reads.EventLines);
        Assert.Equal(0, h.Reads.GoalReads);
        var state = f.Panel.Store.LoadSourceState();
        Assert.NotNull(state.Retained.Apparatus[$"goal-event:{f.GoalId}:1"].PairedBy);
        Assert.Null(state.Retained.Apparatus[$"goal-event:{f.GoalId}:2"].PairedBy);
        Assert.Single(f.Panel.Store.Cases()); // the existing duplicate-dispute rule is unchanged
    }
}
