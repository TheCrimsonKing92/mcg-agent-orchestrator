using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorJudgePanelBudgetTests
{
    [Fact]
    public void Enrollment_cap_uses_windows_of_fifty_new_goals_and_survives_restart()
    {
        using var h = new PanelTestHarness();
        var goals = Enumerable.Range(0, 100).Select(index => new PanelGoalEnrollment($"goal-{index:D3}", h.Time.UtcNow.AddSeconds(index + 1))).ToArray();
        h.Store.Start(h.Time.UtcNow, goals);
        // The harness goal is ordinal one; goal-049 starts the next window.
        for (var index = 0; index < ConductorJudgePanelBudgets.MaxCasesPerEnrollmentWindow; index++)
        {
            var key = new PanelCaseKey(goals[index].GoalId, "candidate", "base", "criteria", "trigger", "kind", "packet");
            Assert.Equal("pending", h.Store.Enqueue(key).Status);
        }
        var skipped = h.Store.Enqueue(new(goals[20].GoalId, "candidate", "base", "criteria", "trigger", "kind", "packet"));
        Assert.Equal(PanelCaseTerminal.BudgetSkip, skipped.Terminal);
        Assert.Equal("enrollment-window", skipped.Reason);
        // Claim one to make queue space without changing enrollment usage.
        Assert.NotNull(h.Store.ClaimNext(h.Time.UtcNow));
        var reopened = new ConductorJudgePanelCaseStore(Path.Combine(h.Root, "panel.db"));
        reopened.Start(h.Time.UtcNow.AddDays(1), goals);
        var next = reopened.Enqueue(new(goals[49].GoalId, "candidate", "base", "criteria", "trigger", "kind", "packet"));
        Assert.Equal(1, next.EnrollmentWindow);
        Assert.Equal("pending", next.Status);
        Assert.Equal(0, skipped.EnrollmentWindow);
        Assert.Equal(PanelCaseTerminal.BudgetSkip, reopened.Enqueue(skipped.Key).Terminal);
    }

    [Fact]
    public void Queue_cap_and_pre_start_goal_are_recorded_as_budget_skips()
    {
        using var h = new PanelTestHarness();
        for (var index = 0; index < ConductorJudgePanelBudgets.MaxQueuedCases; index++) h.Enqueue("queue-" + index);
        var nextGoal = new PanelGoalEnrollment("next-window", h.Time.UtcNow.AddSeconds(100));
        h.Store.Start(h.Time.UtcNow, Enumerable.Range(0, 50)
            .Select(index => new PanelGoalEnrollment("filler-" + index, h.Time.UtcNow.AddSeconds(index + 1))).Append(nextGoal));
        var queued = h.Store.Enqueue(new(nextGoal.GoalId, "candidate", "base", "criteria", "trigger", "kind", "packet"));
        Assert.Equal(PanelCaseTerminal.BudgetSkip, queued.Terminal);
        Assert.Equal("queue-cap", queued.Reason);
        h.Store.Start(h.Time.UtcNow, [new("old-goal", h.Time.UtcNow.AddDays(-1))]);
        var old = h.Store.Enqueue(new("old-goal", "candidate", "base", "criteria", "trigger", "kind", "packet"));
        Assert.Equal(PanelCaseTerminal.BudgetSkip, old.Terminal);
        Assert.Equal("goal-not-enrolled-after-panel-start", old.Reason);
    }

    [Fact]
    public void Claim_ownership_and_per_case_call_cap_are_durable()
    {
        using var h = new PanelTestHarness();
        var first = h.Enqueue("one"); h.Enqueue("two");
        var claim = h.Store.ClaimNext(h.Time.UtcNow)!;
        Assert.Equal(first.Id, claim.Id);
        var reopened = new ConductorJudgePanelCaseStore(Path.Combine(h.Root, "panel.db"));
        Assert.Null(reopened.ClaimNext(h.Time.UtcNow));
        Assert.True(reopened.ReserveCall(claim, "sol"));
        Assert.True(reopened.ReserveCall(claim, "sonnet"));
        Assert.False(reopened.ReserveCall(claim, "third"));
        Assert.False(reopened.ReserveCall(claim, "sol"));
        Assert.False(reopened.ReserveCall(claim with { ClaimToken = "different-owner" }, "other"));
    }
}
