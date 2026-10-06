public sealed class ConductorJudgePanelCursorRelaunchTests
{
    [Fact]
    public async Task RelaunchResumesAndDeletedCursorsRescanOnce()
    {
        using var h = new PanelIncrementalTestFixture();
        await h.Fixture.SaveCriteria();
        h.Escalate(1);
        h.Tick();
        var original = Assert.Single(h.Fixture.Panel.Store.Cases());
        h.Relaunch();
        h.Escalate(2, "PRE_TESTER_RED_LOOP");
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(1, h.Reads.EventLines);
        Assert.Equal(1, h.Reads.ParsedLines);
        Assert.Equal(1, h.Reads.GoalReads);
        var cases = h.Fixture.Panel.Store.Cases();
        Assert.Equal(2, cases.Count);
        Assert.Contains(original, cases);
        var added = Assert.Single(cases, item => item.Key.TriggerId.EndsWith(":2", StringComparison.Ordinal));
        Assert.Equal(h.FullReadKey(added.Key.TriggerId), added.Key);
        h.LedgerSql("DELETE FROM panel_source_cursors");
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(2, h.Reads.EventLines);
        Assert.Equal(2, h.Reads.ParsedLines);
        Assert.Equal(0, h.Reads.GoalReads);
        Assert.Equal(cases, h.Fixture.Panel.Store.Cases());
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(0, h.Reads.EventLines);
        Assert.Equal(0, h.Reads.GoalReads);
    }

    [Fact]
    public async Task TruncationAndReplacementRescanWithoutLosingCases()
    {
        using var h = new PanelIncrementalTestFixture();
        await h.Fixture.SaveCriteria();
        h.Escalate(1);
        h.Escalate(2, "PRE_TESTER_RED_LOOP");
        h.Tick();
        var cases = h.Fixture.Panel.Store.Cases();
        Assert.Equal(2, cases.Count);
        File.WriteAllText(h.TimelinePath, h.Line(1, "PRE_REVIEW_RED_UNCHANGED_CANDIDATE") + "\n");
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(1, h.Reads.EventLines);
        Assert.Equal(0, h.Reads.GoalReads);
        Assert.Equal(cases, h.Fixture.Panel.Store.Cases());

        var replacement = h.TimelinePath + ".replacement";
        File.WriteAllText(replacement, h.Line(2, "PRE_TESTER_RED_LOOP") + "\n" + new string(' ', 500) + "\n");
        File.Move(replacement, h.TimelinePath, overwrite: true);
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(2, h.Reads.EventLines);
        Assert.Equal(0, h.Reads.GoalReads);
        Assert.Equal(cases, h.Fixture.Panel.Store.Cases());
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(0, h.Reads.EventLines);
    }

    [Fact]
    public async Task ReplacementWithPreservedMetadataAndPrefixRescans()
    {
        using var h = new PanelIncrementalTestFixture();
        await h.Fixture.SaveCriteria();
        var padding = "{\"eventType\":\"GoalCreated\",\"message\":\"" + new string('x', 5000) + "\"}\n";
        Directory.CreateDirectory(h.Fixture.Events);
        var original = h.Line(1, "PRE_REVIEW_RED_UNCHANGED_CANDIDATE") + "\n" + padding +
            h.Line(2, "PRE_TESTER_RED_LOOP") + "\n" + padding;
        File.WriteAllText(h.TimelinePath, original);
        h.Tick();
        var cases = h.Fixture.Panel.Store.Cases();
        Assert.Equal(2, cases.Count);
        var info = new FileInfo(h.TimelinePath);
        var created = info.CreationTimeUtc;
        var written = info.LastWriteTimeUtc;
        var replacement = h.TimelinePath + ".replacement";
        File.WriteAllText(replacement, original.Replace("\"cursor\":2", "\"cursor\":3", StringComparison.Ordinal));
        File.SetCreationTimeUtc(replacement, created);
        File.SetLastWriteTimeUtc(replacement, written);
        File.Move(replacement, h.TimelinePath, overwrite: true);
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(4, h.Reads.EventLines);
        Assert.Equal(4, h.Reads.ParsedLines);
        Assert.Equal(cases, h.Fixture.Panel.Store.Cases());
    }

    [Fact]
    public async Task ConductRotationRescansAndEnrollsOnlyTheFreshDecision()
    {
        using var h = new PanelIncrementalTestFixture();
        await h.Fixture.SaveCriteria();
        h.Fixture.Conduct("goal-escalation", h.Text("PRE_REVIEW_RED_UNCHANGED_CANDIDATE"));
        h.Tick();
        var first = Assert.Single(h.Fixture.Panel.Store.Cases());
        File.Move(h.Fixture.Panel.ConductPath, Path.Combine(h.Fixture.Panel.Root, "conduct-events-20300101.log"));
        h.Fixture.Panel.Time.UtcNow += TimeSpan.FromSeconds(1);
        h.Fixture.Conduct("goal-escalation", h.Text("PRE_TESTER_RED_LOOP"));
        h.Relaunch();
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(2, h.Reads.EventLines);
        Assert.Equal(2, h.Reads.ParsedLines);
        Assert.Equal(1, h.Reads.GoalReads);
        var cases = h.Fixture.Panel.Store.Cases();
        Assert.Equal(2, cases.Count);
        Assert.Contains(first, cases);
        var added = Assert.Single(cases, item => item.Id != first.Id);
        Assert.Equal(h.FullReadKey(added.Key.TriggerId), added.Key);
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(0, h.Reads.EventLines);
        Assert.Equal(0, h.Reads.GoalReads);
    }
}
