using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;

// Each case owns isolated producer files and a ledger; no concurrent processes or clocks.
public sealed class ConductorJudgePanelCursorAppendRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewlineAppendedAfterEofIsDiscovered(bool relaunch)
    {
        PanelIncrementalTestFixture? fixture = null;
        var appended = false;
        var reads = new PanelSourceReadCounter
        {
            OnEventLine = () =>
            {
                if (appended) return;
                // The only input is a torn record: this callback runs after Read returns 0.
                Assert.Equal(0, fixture!.Reads.ParsedLines);
                File.AppendAllText(fixture.TimelinePath, "\n");
                appended = true;
            }
        };
        using var h = new PanelIncrementalTestFixture(reads);
        fixture = h;
        await h.Fixture.SaveCriteria();
        Directory.CreateDirectory(h.Fixture.Events);
        var torn = h.Line(1, "PRE_REVIEW_RED_UNCHANGED_CANDIDATE");
        File.WriteAllText(h.TimelinePath, torn, new UTF8Encoding(false));

        h.Tick();
        Assert.True(appended);
        Assert.Equal(1, reads.EventLines);
        Assert.Equal(0, reads.ParsedLines);
        Assert.Equal(0, reads.GoalReads);
        Assert.Empty(h.Fixture.Panel.Store.Cases());
        var cursor = h.Fixture.Panel.Store.LoadSourceState().Cursors[Path.GetFullPath(h.TimelinePath)];
        Assert.Equal(0L, cursor.Offset);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(torn), cursor.Length);
        Assert.Equal(cursor.Length + 1, new FileInfo(h.TimelinePath).Length);

        if (relaunch) h.Relaunch();
        reads.Reset();
        h.Tick();
        Assert.Equal(1, reads.EventLines);
        Assert.Equal(1, reads.ParsedLines);
        Assert.Equal(1, reads.GoalReads);
        var opened = Assert.Single(h.Fixture.Panel.Store.Cases());
        Assert.Equal(h.FullReadKey($"goal-event:{h.Fixture.GoalId}:1"), opened.Key);

        reads.Reset();
        h.Tick();
        Assert.Equal(0, reads.EventLines);
        Assert.Equal(0, reads.ParsedLines);
        Assert.Equal(0, reads.GoalReads);
        Assert.Equal(opened, Assert.Single(h.Fixture.Panel.Store.Cases()));
    }
}
