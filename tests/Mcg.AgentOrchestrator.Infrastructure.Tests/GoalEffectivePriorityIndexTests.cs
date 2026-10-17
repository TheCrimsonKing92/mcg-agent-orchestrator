using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalEffectivePriorityIndexTests
{
    [Fact]
    public void IntentUpdatesWholeDependencyChainBeforeTickAndPersistsIt()
    {
        var (kernel, h, d, e) = Chain();
        kernel.SetGoalPriority(h.Id, GoalPriority.High, "test", "raise");
        foreach (var goal in new[] { d, e })
        {
            Assert.Equal(GoalPriority.High, GoalEffectivePriorityIndex.StoredLevel(goal));
            Assert.Equal(h.Id.Value, goal.PriorityScheduling!.InheritedFromGoalId);
        }
        var changed = Apply(kernel);
        Assert.Contains(d.Id, changed);
        Assert.Contains(e.Id, changed);
        kernel.SetGoalPriority(h.Id, GoalPriority.Normal, "test", "lower");
        Assert.Equal(GoalPriority.Normal, GoalEffectivePriorityIndex.StoredLevel(d));
        Assert.Equal(GoalPriority.Normal, GoalEffectivePriorityIndex.StoredLevel(e));
        Assert.Null(d.PriorityScheduling);
        Assert.Null(e.PriorityScheduling);
    }

    [Fact]
    public void CliRemovalRecordsBothSeedsAndTickDropsInheritance()
    {
        var (kernel, h, d, e) = Chain();
        var drift = new StrongBox<DateTimeOffset?>();
        kernel.SetGoalPriority(h.Id, GoalPriority.High, "test", "raise");
        Apply(kernel, drift);
        kernel.RemoveGoalDependency(h.Id, d.Id);
        Assert.Equal(GoalPriority.High, GoalEffectivePriorityIndex.StoredLevel(d));
        Assert.Contains(h.Id.Value, h.PriorityScheduling!.PendingSeedGoalIds!);
        Assert.Contains(d.Id.Value, h.PriorityScheduling.PendingSeedGoalIds!);
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var changed = Apply(restored, drift);
        Assert.Contains(d.Id, changed);
        Assert.Contains(e.Id, changed);
        Assert.Equal(GoalPriority.Normal, GoalEffectivePriorityIndex.StoredLevel(restored.GetGoal(d.Id)));
        Assert.Equal(GoalPriority.Normal, GoalEffectivePriorityIndex.StoredLevel(restored.GetGoal(e.Id)));
    }

    [Fact]
    public void CompletedDependentStopsDonatingAfterSeededTick()
    {
        var (kernel, h, d, e) = Chain();
        var drift = new StrongBox<DateTimeOffset?>();
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == h.Id.Value ? goal with { Status = GoalStatus.Verified } : goal).ToArray()
        });
        h = kernel.GetGoal(h.Id);
        kernel.SetGoalPriority(h.Id, GoalPriority.High, "test", "raise");
        Apply(kernel, drift);
        kernel.CompleteGoal(h.Id, "landed");
        Assert.Contains(h.Id.Value, h.PriorityScheduling!.PendingSeedGoalIds!);
        Apply(kernel, drift);
        Assert.Equal(GoalPriority.Normal, GoalEffectivePriorityIndex.StoredLevel(kernel.GetGoal(d.Id)));
        Assert.Equal(GoalPriority.Normal, GoalEffectivePriorityIndex.StoredLevel(kernel.GetGoal(e.Id)));
    }

    [Fact]
    public void HighEdgeAdditionAndClearWaitForSeededTick()
    {
        var (kernel, h, d, e) = Chain();
        kernel.RemoveGoalDependency(h.Id, d.Id);
        kernel.SetGoalPriority(h.Id, GoalPriority.High, "test", "raise");
        var drift = new StrongBox<DateTimeOffset?>();
        Apply(kernel, drift);
        kernel.SetGoalDependency(h.Id, d.Id);
        Assert.Equal(GoalPriority.Normal, GoalEffectivePriorityIndex.StoredLevel(d));
        Assert.Contains(d.Id.Value, h.PriorityScheduling!.PendingSeedGoalIds!);
        Apply(kernel, drift);
        Assert.Equal(GoalPriority.High, GoalEffectivePriorityIndex.StoredLevel(d));
        Assert.Equal(GoalPriority.High, GoalEffectivePriorityIndex.StoredLevel(e));
        var revision = d.PriorityScheduling!.GraphRevision;
        kernel.ClearGoalDependencies(h.Id);
        Assert.Equal(GoalPriority.High, GoalEffectivePriorityIndex.StoredLevel(d));
        Assert.Contains(d.Id.Value, h.PriorityScheduling!.PendingSeedGoalIds!);
        var changed = Apply(kernel, drift);
        Assert.Contains(d.Id, changed);
        Assert.Contains(e.Id, changed);
        Assert.Equal(GoalPriority.Normal, GoalEffectivePriorityIndex.StoredLevel(d));
        Assert.Equal(GoalPriority.Normal, GoalEffectivePriorityIndex.StoredLevel(e));
        Assert.True(revision > 0);
    }

    [Fact]
    public void NormalMutationsHaveNoSeedsOrSnapshotField()
    {
        var (kernel, h, d, e) = Chain();
        kernel.RemoveGoalDependency(h.Id, d.Id);
        kernel.ClearGoalDependencies(d.Id);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with { Status = GoalStatus.Verified }).ToArray()
        });
        kernel.CompleteGoal(h.Id, "landed");
        Assert.All(kernel.Goals, goal => Assert.Null(goal.PriorityScheduling));
        Apply(kernel);
        Assert.All(kernel.ExportSnapshot().Goals, goal => Assert.Null(goal.PriorityScheduling));
    }

    [Fact]
    public void FirstDriftCheckRepairsOnceAndRechecksAfterAnHour()
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("stale");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [snapshot.Goals.Single() with { PriorityScheduling = new("high") }]
        });
        var now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var state = new StrongBox<DateTimeOffset?>();
        var lines = new List<string>();
        var changed = GoalEffectivePriorityIndex.ApplyPending(kernel.Goals, lines.Add, () => now, state);
        Assert.Single(changed);
        Assert.StartsWith("PRIORITY_DRIFT goal=", Assert.Single(lines));
        Assert.Equal(GoalPriority.Normal, GoalEffectivePriorityIndex.StoredLevel(kernel.Goals.Single()));
        now += TimeSpan.FromMinutes(59);
        GoalEffectivePriorityIndex.ApplyPending(kernel.Goals, lines.Add, () => now, state);
        Assert.Single(lines);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), state.Value);
        now += TimeSpan.FromMinutes(1);
        GoalEffectivePriorityIndex.ApplyPending(kernel.Goals, lines.Add, () => now, state);
        Assert.Equal(now, state.Value);
        Assert.Single(lines);
    }

    private static (AgentOrchestratorKernel Kernel, Goal H, Goal D, Goal E) Chain()
    {
        var kernel = new AgentOrchestratorKernel();
        var h = kernel.CreateGoal("H");
        var d = kernel.CreateGoal("D");
        var e = kernel.CreateGoal("E");
        kernel.SetGoalDependency(h.Id, d.Id);
        kernel.SetGoalDependency(d.Id, e.Id);
        return (kernel, h, d, e);
    }

    private static IReadOnlySet<GoalId> Apply(AgentOrchestratorKernel kernel, StrongBox<DateTimeOffset?>? drift = null) =>
        GoalEffectivePriorityIndex.ApplyPending(kernel.Goals, _ => { }, () => DateTimeOffset.Parse("2026-10-01T00:00:00Z"), drift ?? new());
}
