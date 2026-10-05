using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory snapshots with fixed timestamps.
public sealed class RoundValueReportCascadeRouteTests
{
    [Fact]
    public void Build_MatchesEachCohortRoundToItsDispatchAndSkipsUnmarkedRounds()
    {
        var at = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        TaskDispatchSnapshot Dispatch(int n, string? decision) => new("worker", "command", "root", at.AddMinutes(n),
            ModelSelectionReason: decision is null ? "ordinary developer" : $"reason; cascade={decision} rule=tester-cheap-first");
        var tester = new TaskSnapshot("tester", "test", AgentRole.Tester, WorkTaskStatus.Completed,
            null, null, null, [], null, null, DispatchHistory: [Dispatch(1, "cheap"), Dispatch(2, "escalated"), Dispatch(3, "primary")]);
        var developer = new TaskSnapshot("developer", "build", AgentRole.Developer, WorkTaskStatus.Completed,
            null, null, null, [], null, null, DispatchHistory: [Dispatch(0, null)]);
        var goal = new GoalSnapshot("cascade-goal", "goal", GoalStatus.Completed, [tester, developer],
            [new("cascade-goal", "tester", ProgressKind.TaskRetried,
                "auto-review-retry round 2: corrected test evidence", at.AddMinutes(2).AddSeconds(30)),
             new("cascade-goal", "tester", ProgressKind.TaskCompleted, "done", at.AddMinutes(4))]);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([goal], []));
        var report = RoundValueReport.Build(kernel.Goals, at, at.AddDays(1));
        Assert.Equal(4, report.Window.Rounds);
        Assert.Equal([new RoundValueCascadeRoute("cheap", 1, 0, 0, 1),
            new RoundValueCascadeRoute("escalated", 1, 0, 0, 1),
            new RoundValueCascadeRoute("primary", 1, 1, 0, 0)], report.CascadeRoutes);
        Assert.Equal(["cheap", "escalated", "primary"], report.CascadeRoutes.Select(r => r.Decision));
        Assert.All(report.CascadeRoutes, r =>
        {
            Assert.Equal(1, r.Rounds);
            Assert.Equal(r.Rounds, r.Productive + r.Overhead + r.Wasted);
        });
        Assert.Equal(3, report.CascadeRoutes.Sum(r => r.Rounds));
        Assert.Empty(RoundValueReport.Build(kernel.Goals, at.AddDays(1), at.AddDays(2)).CascadeRoutes);
    }

    [Fact]
    public void Build_UnmarkedFixture_HasNoCascadeSection() =>
        Assert.Empty(RoundValueReport.Build(RoundValueFixture.Create().Goals, RoundValueFixture.Since, RoundValueFixture.Until).CascadeRoutes);
}
