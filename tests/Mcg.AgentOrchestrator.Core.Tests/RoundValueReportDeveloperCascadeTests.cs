using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory snapshots with fixed timestamps.
public sealed class RoundValueReportDeveloperCascadeTests
{
    [Fact]
    public void LandedDeveloperGoal_CountsPrimaryCheapAndEscalatedExactlyOnce()
    {
        var at = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        TaskDispatchSnapshot Dispatch(int n, string decision, string rule, string? ids = null) => new(
            decision == "cheap" ? "codex-spark" : "codex-cli", "command", "root", at.AddMinutes(n),
            ModelSelectionReason: new CascadeRouteMarker(decision, rule, ids is null ? [] : [ids]).AppendTo("provider-constrained: Developer remains on OpenAI"));
        var developer = new TaskSnapshot("developer", "build", AgentRole.Developer, WorkTaskStatus.Completed,
            null, null, null, [], null, null, DispatchHistory:
            [Dispatch(0, "primary", "first-round"), Dispatch(1, "cheap", "structural-ratchet", "src/Alpha.cs"),
             Dispatch(2, "escalated", "mechanical-finding-persisted")]);
        var goal = new GoalSnapshot("goal", "goal", GoalStatus.Completed, [developer],
            [new("goal", "developer", ProgressKind.TaskCompleted, "done", at.AddMinutes(3))], ResultCommit: "landed");
        var kernel = AgentOrchestratorKernel.FromSnapshot(new([goal], []));
        Assert.All(kernel.Goals.Single().Tasks, t => Assert.Equal(AgentRole.Developer, t.RequiredRole));
        var report = RoundValueReport.Build(kernel.Goals, at, at.AddDays(1));
        Assert.Equal(3, report.Window.Rounds);
        Assert.Equal(["cheap", "escalated", "primary"], report.CascadeRoutes.Select(r => r.Decision));
        Assert.All(report.CascadeRoutes, r => Assert.Equal(1, r.Rounds));
        Assert.Equal(3, report.CascadeRoutes.Sum(r => r.Rounds));
    }
}
