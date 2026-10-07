using Mcg.AgentOrchestrator.Core;

// Parallel-safe: only in-memory snapshots and explicit report windows.
public sealed class RoundValueReportTests
{
    [Theory]
    [InlineData(GoalStatus.Completed, AgentRole.Developer, "classifier-false-fail-bridge")]
    [InlineData(GoalStatus.Completed, AgentRole.Reviewer, "classifier-false-fail-bridge")]
    [InlineData(GoalStatus.Cancelled, AgentRole.Developer, "abandoned-goal")]
    public void Build_GuardFailure_UsesTerminalAndReviewerPrecedence(
        GoalStatus status, AgentRole role, string expectedCause)
    {
        var goals = GuardGoal(status, role);
        var report = RoundValueReport.Build(goals, RoundValueFixture.Since, RoundValueFixture.Until);
        var bridge = RoundValueClassifier.Classify(goals).Last();

        Assert.Equal(RoundValueClass.Wasted, bridge.ValueClass);
        Assert.Equal(expectedCause, bridge.WasteCause);
        Assert.Equal(new RoundValueCauseShare(expectedCause, 1, 1.0 / report.Window.Rounds),
            Assert.Single(report.WasteByCause));
    }

    [Fact]
    public void Build_Baseline_ZeroFillsMissingCausesAndUsesWindowDenominators()
    {
        var report = RoundValueReport.Build(RoundValueFixture.Create().Goals,
            RoundValueFixture.At("2026-09-25T00:00:00Z"), RoundValueFixture.Until,
            RoundValueFixture.Since, RoundValueFixture.At("2026-09-25T00:00:00Z"));

        var comparison = Assert.IsType<RoundValueBaselineComparison>(report.Baseline);
        Assert.Equal(RoundValueFixture.Since, comparison.Since);
        Assert.Equal(RoundValueFixture.At("2026-09-25T00:00:00Z"), comparison.Until);
        Assert.Null(comparison.CurrentRoundsPerLanding);
        Assert.Equal(8, comparison.BaselineRoundsPerLanding);
        Assert.Equal([
            new RoundValueCauseDelta("abandoned-goal", 2, 1, 0, 0, 2, 1),
            new RoundValueCauseDelta("flake-or-apparatus", 0, 0, 1, 0.125, -1, -0.125),
            new RoundValueCauseDelta("unchanged-commit-review", 0, 0, 1, 0.125, -1, -0.125)
        ], comparison.Causes);
    }

    [Theory]
    [InlineData(true, "classifier-false-fail-bridge")]
    [InlineData(false, null)]
    public void Classify_PendingGuard_RequiresSuccessPairedToLaterRound(bool laterRound, string? cause)
    {
        const string goalId = "ffffffffffffffffffffffffffffffff";
        const string taskId = "planner-guard";
        var at = RoundValueFixture.Since.AddHours(1);
        var successAt = laterRound ? at.AddHours(1).AddMinutes(15) : at.AddMinutes(30);
        var guard = new TaskVerificationSnapshot("command", "root", 1, "output",
            "DID NOT PRODUCE RELEVANT FILE-CHANGE EVIDENCE", at.AddMinutes(15));
        var success = new TaskVerificationSnapshot("command", "root", 0, "planner completed", "", successAt);
        var dispatches = new List<TaskDispatchSnapshot> { RoundValueFixture.Dispatch(at.ToString("O")) };
        if (laterRound) dispatches.Add(RoundValueFixture.Dispatch(at.AddHours(1).ToString("O")));
        var task = RoundValueFixture.Task(taskId, AgentRole.Planner, WorkTaskStatus.Completed, dispatches.ToArray()) with
        {
            VerificationHistory = [guard, success], LastVerification = success
        };
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot(goalId, "pending", GoalStatus.Active, [task],
            [
                new(goalId, taskId, ProgressKind.TaskFailed, "guard failure", guard.CompletedAt),
                new(goalId, taskId, ProgressKind.TaskCompleted, "done", successAt)
            ])], []));
        Assert.Equal(DispatchOutcomeKind.VerifiedSuccess,
            DispatchFailureClassifier.Classify(kernel.Goals.Single().Tasks.Single(),
                kernel.Goals.Single().Tasks.Single().VerificationHistory.Last()).Kind);
        var round = RoundValueClassifier.Classify(kernel.Goals).First();
        Assert.Equal(cause, round.WasteCause);
        Assert.Equal(laterRound ? RoundValueClass.Wasted : RoundValueClass.Productive, round.ValueClass);
    }

    private static IReadOnlyList<Goal> GuardGoal(GoalStatus status, AgentRole role)
    {
        const string goalId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string taskId = "guard-task";
        var firstAt = RoundValueFixture.Since.AddHours(1);
        var guardAt = role == AgentRole.Reviewer ? firstAt.AddHours(1) : firstAt;
        var guardVerification = new TaskVerificationSnapshot("command", "root", 1, "worker output",
            "Developer/Tester dispatch exited 0 but did not produce required relevant file-change evidence",
            guardAt.AddMinutes(15));
        var dispatches = role == AgentRole.Reviewer
            ? new[] { RoundValueFixture.Dispatch(firstAt.ToString("O"), "same"),
                RoundValueFixture.Dispatch(guardAt.ToString("O"), "same") }
            : new[] { RoundValueFixture.Dispatch(guardAt.ToString("O")) };
        var task = RoundValueFixture.Task(taskId, role, WorkTaskStatus.Completed, dispatches) with
        {
            VerificationHistory = [guardVerification], LastVerification = guardVerification
        };
        var events = new List<ProgressEventSnapshot>();
        if (role == AgentRole.Reviewer)
            events.Add(new(goalId, taskId, ProgressKind.TaskCompleted, "done", firstAt.AddMinutes(15)));
        events.Add(new(goalId, taskId, ProgressKind.TaskFailed, "guard failure", guardVerification.CompletedAt));
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot(goalId, "guard", status, [task], events)], [])).Goals.ToArray();
    }

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
