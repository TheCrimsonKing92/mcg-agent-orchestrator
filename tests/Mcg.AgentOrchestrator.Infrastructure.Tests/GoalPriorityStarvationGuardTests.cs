using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalPriorityStarvationGuardTests
{
    [Fact]
    public void ThreeGateOvertakesRaiseWaiterAndServingResetsIt()
    {
        var kernel = new AgentOrchestratorKernel();
        var waiter = kernel.CreateGoal("N");
        var highs = Enumerable.Range(1, 3).Select(tick => kernel.CreateGoal($"H{tick}")).ToArray();
        var snapshot = kernel.ExportSnapshot();
        var start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select((goal, i) => goal with
            {
                Timeline = goal.Timeline.Select(evt => evt with { OccurredAt = start.AddMinutes(i) }).ToArray()
            }).ToArray()
        });
        waiter = kernel.GetGoal(waiter.Id);
        highs = highs.Select(goal => kernel.GetGoal(goal.Id)).ToArray();
        var guard = new GoalPriorityStarvationGuard();
        var lines = new List<string>();
        var changed = new HashSet<GoalId>();
        for (var tick = 1; tick <= 3; tick++)
        {
            var high = highs[tick - 1];
            kernel.SetGoalPriority(high.Id, GoalPriority.High, "test", "setup");
            guard.ObserveGateTick(GoalSchedulingOrder.ForAcceptance([waiter, high]), GoalSchedulingOrder.BaseAcceptance([waiter, high]), new HashSet<string>(),
                new HashSet<string> { high.Id.Value }, tick, changed, lines.Add);
        }
        Assert.Equal(3, waiter.PriorityScheduling!.GateOvertakes);
        var admissions = lines.Where(line => line.StartsWith("PRIORITY_ADMISSION stage=gate")).ToArray();
        Assert.Equal(3, admissions.Length);
        Assert.All(admissions, line => Assert.Contains($"overtook={waiter.Id.Value[..8]}:normal", line));
        Assert.Contains($"PRIORITY_STARVATION_GUARD stage=gate tick=3 goal={waiter.Id.Value[..8]} overtakes=3", lines);
        Assert.Equal(waiter, GoalSchedulingOrder.ForAcceptance(kernel.Goals)[0]);
        guard.ObserveGateTick(GoalSchedulingOrder.ForAcceptance(kernel.Goals), GoalSchedulingOrder.BaseAcceptance(kernel.Goals), new HashSet<string>(),
            new HashSet<string> { waiter.Id.Value }, 4, changed, lines.Add);
        Assert.Equal(0, waiter.PriorityScheduling?.GateOvertakes ?? 0);
        Assert.Contains(waiter.Id, changed);
    }

    [Fact]
    public void InlineCompletionKeepsAdmissionBaseOrderForOvertakeEvidence()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        var kernel = new AgentOrchestratorKernel(clock);
        var waiter = kernel.CreateGoal("Normal waiter");
        clock.UtcNow += TimeSpan.FromMinutes(1);
        var high = kernel.CreateGoal("High admission");
        kernel.SetGoalPriority(high.Id, GoalPriority.High, "test", "setup");
        var ordered = GoalSchedulingOrder.ForAcceptance(kernel.Goals);
        var baseOrder = GoalSchedulingOrder.BaseAcceptance(ordered);
        Assert.Equal(new[] { waiter, high }, baseOrder);
        Assert.Equal(new[] { high, waiter }, ordered);

        // Inline gates append Verified history in service order before the guard observes them.
        clock.UtcNow += TimeSpan.FromHours(1);
        kernel.RecordGoalPolicyDecision(high.Id, "Verified after inline acceptance");
        clock.UtcNow += TimeSpan.FromMinutes(1);
        kernel.RecordGoalPolicyDecision(waiter.Id, "Verified after inline acceptance");
        Assert.Equal(new[] { high, waiter }, GoalSchedulingOrder.BaseAcceptance(ordered));

        var lines = new List<string>();
        var changed = new HashSet<GoalId>();
        new GoalPriorityStarvationGuard().ObserveGateTick(ordered, baseOrder, new HashSet<string>(),
            new HashSet<string> { high.Id.Value, waiter.Id.Value }, 1, changed, lines.Add);
        var admission = Assert.Single(lines);
        Assert.StartsWith("PRIORITY_ADMISSION stage=gate", admission);
        Assert.Contains($"goal={high.Id.Value[..8]}", admission);
        Assert.Contains($"overtook={waiter.Id.Value[..8]}:normal", admission);
        Assert.Contains(waiter.Id, changed);
        Assert.Equal(0, waiter.PriorityScheduling?.GateOvertakes ?? 0);
    }

    [Fact]
    public void DispatchOvertakeChangesOnlyDispatchAndIsReturnedForPersistence()
    {
        var kernel = new AgentOrchestratorKernel();
        var waiter = kernel.CreateGoal("W");
        var high = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "H");
        kernel.SetGoalPriority(high.Id, GoalPriority.High, "test", "setup");
        var guard = new GoalPriorityStarvationGuard();
        var now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        GoalEffectivePriorityIndex.ApplyPending(kernel.Goals, _ => { }, () => now, guard.BeginTick(kernel, 1));
        var task = high.Tasks.Single();
        kernel.RecordTaskDispatch(high.Id, task.Id, new("test", "test.exe", "C:\\tmp", now));
        kernel.RecordTaskProcessStarted(high.Id, task.Id, new(42, "test.exe", "C:\\tmp", "out", "err", "exit", now, null, null));
        var lines = new List<string>();
        guard.ObserveDispatchTick(new HashSet<GoalId> { waiter.Id }, kernel, lines.Add);
        Assert.Equal(1, waiter.PriorityScheduling!.DispatchOvertakes);
        Assert.Equal(0, waiter.PriorityScheduling.GateOvertakes);
        Assert.Contains($"overtook={waiter.Id.Value[..8]}:normal", Assert.Single(lines));
        Assert.StartsWith("PRIORITY_ADMISSION stage=dispatch", lines[0]);
        Assert.Contains(waiter.Id, GoalEffectivePriorityIndex.ApplyPending(kernel.Goals, _ => { }, () => now, guard.DriftState));
        guard.ObserveDispatchTick(new HashSet<GoalId> { waiter.Id }, kernel, lines.Add);
        Assert.Single(lines);
    }

    [Fact]
    public void NormalAdmissionsAreSilentAndKeepCountersEmpty()
    {
        var kernel = new AgentOrchestratorKernel();
        var a = kernel.CreateGoal("A");
        var b = kernel.CreateGoal("B");
        var lines = new List<string>();
        new GoalPriorityStarvationGuard().ObserveGateTick([a, b], GoalSchedulingOrder.BaseAcceptance([a, b]), new HashSet<string>(),
            new HashSet<string> { b.Id.Value }, 1, [], lines.Add);
        Assert.Empty(lines);
        Assert.Null(a.PriorityScheduling);
        Assert.Null(b.PriorityScheduling);
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
