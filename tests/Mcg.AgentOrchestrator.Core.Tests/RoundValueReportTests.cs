using Mcg.AgentOrchestrator.Core;

// Parallel-safe: only in-memory snapshots and explicit report windows.
public sealed class RoundValueReportTests
{
    [Fact]
    public void Build_Fixture_CohortExcludesPendingAndOutsideFinalDispatch()
    {
        var report = RoundValueReport.Build(RoundValueFixture.Create().Goals,
            RoundValueFixture.Since, RoundValueFixture.Until);

        Assert.Equal(new RoundValueTotals(1, 1, 10, 4, 2, 4, 10, 0.4, 1500, 600, 150, 8), report.Window);
        Assert.Equal([new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 25)], report.Days.Select(d => d.Day));
        Assert.Equal(new RoundValueTotals(1, 0, 8, 4, 2, 2, 8, 0.25, 1000, 400, 100, 7), report.Days[0].Totals);
        Assert.Equal(new RoundValueTotals(0, 1, 2, 0, 0, 2, null, 1, 500, 200, 50, 1), report.Days[1].Totals);
        Assert.Equal([new RoundValueCauseShare("abandoned-goal", 2, 0.2),
            new RoundValueCauseShare("flake-or-apparatus", 1, 0.1),
            new RoundValueCauseShare("unchanged-commit-review", 1, 0.1)], report.WasteByCause);
        Assert.Equal(1, report.PendingGoals);
        Assert.Equal(2, report.PendingRounds);
    }

    [Fact]
    public void Build_LastDispatchAtSince_IncludesHistoryBeforeWindowOnUtcDay()
    {
        var report = RoundValueReport.Build(RoundValueFixture.Create().Goals,
            RoundValueFixture.At("2026-09-26T03:00:00+02:00"), RoundValueFixture.At("2026-09-27T00:00:00Z"));

        Assert.Equal(new RoundValueTotals(1, 0, 2, 2, 0, 0, 2, 0, 0, 0, 0, 2), report.Window);
        Assert.Equal(new DateOnly(2026, 9, 26), Assert.Single(report.Days).Day);
        Assert.Empty(report.WasteByCause);
        Assert.Equal(0, report.PendingGoals);
        Assert.Equal(0, report.PendingRounds);
    }

    [Fact]
    public void Build_LastDispatchAtUntil_ExcludesWholeGoal()
    {
        var report = RoundValueReport.Build(RoundValueFixture.Create().Goals,
            RoundValueFixture.At("2026-09-25T04:00:00Z"), RoundValueFixture.At("2026-09-26T01:00:00Z"));
        Assert.Equal(new RoundValueTotals(0, 0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0), report.Window);
        Assert.Empty(report.Days);
        Assert.Empty(report.WasteByCause);
    }

    [Fact]
    public void Build_PendingWindow_CountsOnlyDispatchesInWindow()
    {
        var report = RoundValueReport.Build(RoundValueFixture.Create().Goals,
            RoundValueFixture.At("2026-09-25T03:00:00Z"), RoundValueFixture.At("2026-09-25T04:00:00Z"));
        Assert.Equal(1, report.PendingGoals);
        Assert.Equal(1, report.PendingRounds);
        Assert.Equal(0, report.Window.Rounds);
    }

    [Fact]
    public void Build_NoRounds_ReturnsEmptyTotals()
    {
        var task = RoundValueFixture.Task("undispatched", AgentRole.Developer, WorkTaskStatus.Completed);
        var goal = new GoalSnapshot("empty", "empty", GoalStatus.Completed, [task], []);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([goal], []));
        var report = RoundValueReport.Build(kernel.Goals, RoundValueFixture.Since, RoundValueFixture.Until);
        Assert.Equal(new RoundValueTotals(0, 0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0), report.Window);
        Assert.Empty(report.Days);
        Assert.Equal(0, report.PendingGoals);
    }
}
