using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class ReviewRetryBudgetLedgerTests
{
    [Fact]
    public void Equal_timestamps_preserve_append_order_for_shrink_and_operator_reset()
    {
        var frozen = ExerciseOrdering(advanceClock: false);
        var distinct = ExerciseOrdering(advanceClock: true);
        Assert.Equal(new[] { 1, 2, 1, 2, 1, 2 }, frozen.Rounds);
        Assert.Equal(new[] { 1, 2, 2, 3, 1, 2 }, frozen.LifetimeRounds);
        Assert.Equal(distinct.Rounds, frozen.Rounds);
        Assert.Equal(distinct.LifetimeRounds, frozen.LifetimeRounds);
        Assert.Equal(1, frozen.TimestampCount);
        Assert.True(distinct.TimestampCount > 1);
    }

    [Fact]
    public void Lifetime_receipt_activates_cap_contract_after_twenty_retries_even_when_latest_set_shrank()
    {
        var scenario = new LedgerScenario();
        for (var round = 1; round <= 21; round++)
        {
            scenario.Verify([Finding($"F-{round}")]);
            if (round < 21) scenario.AutoRetry();
        }
        var receipt = ReviewRetryCapReceipt.Create(scenario.Goal, 7);
        Assert.Equal(1, receipt.Round);
        Assert.Equal(21, receipt.LifetimeRound);
        Assert.Equal(21, receipt.LifetimeBackstop);
        Assert.False(receipt.IsAtConsecutiveCap);
        Assert.True(receipt.IsAtLifetimeBackstop);
        Assert.True(receipt.IsAtCap);
        Assert.True(JsonSerializer.Deserialize<ReviewRetryCapReceipt>(JsonSerializer.Serialize(receipt))!.IsAtCap);
    }

    [Theory]
    [InlineData(OperatorActorKind.Human, RetryRoundKind.Standard, "applied", true, true)]
    [InlineData(OperatorActorKind.Human, RetryRoundKind.Mechanical, "applied", true, false)]
    [InlineData(OperatorActorKind.Agent, RetryRoundKind.Standard, "applied", true, false)]
    [InlineData(OperatorActorKind.Human, null, "applied", true, false)]
    [InlineData(OperatorActorKind.Human, RetryRoundKind.Standard, "rejected", true, false)]
    [InlineData(OperatorActorKind.Human, RetryRoundKind.Standard, "applied", false, false)]
    public void Only_applied_human_standard_reviewer_record_resets_both_counts(
        OperatorActorKind actor, RetryRoundKind? kind, string outcome, bool reviewer, bool resets)
    {
        var scenario = new LedgerScenario();
        scenario.Verify([Finding("F-SAME")]);
        scenario.AutoRetry();
        scenario.Intent(actor, kind, outcome, reviewer);
        var receipt = ReviewRetryCapReceipt.Create(scenario.Goal, 7);
        Assert.Equal(resets ? 1 : 2, receipt.Round);
        Assert.Equal(resets ? 1 : 2, receipt.LifetimeRound);
        var restored = JsonSerializer.Deserialize<OperatorIntentAppliedPayload>(JsonSerializer.Serialize(
            scenario.Goal.Timeline.Last(evt => evt.OperatorIntentApplied is not null).OperatorIntentApplied))!;
        Assert.Equal(kind, restored.RetryRoundKind);
    }

    [Fact]
    public void Legacy_payload_and_receipt_preserve_their_previous_meaning()
    {
        var payload = JsonSerializer.Deserialize<OperatorIntentAppliedPayload>(
            "{\"IntentId\":\"old\",\"Verb\":\"retry\",\"TaskId\":\"reviewer\",\"Actor\":\"operator\",\"Channel\":\"cli\",\"Outcome\":\"applied\"}")!;
        Assert.Null(payload.RetryRoundKind);
        var receipt = JsonSerializer.Deserialize<ReviewRetryCapReceipt>("{\"Round\":6,\"StopRound\":7}")!;
        Assert.Null(receipt.LifetimeRound);
        Assert.Null(receipt.LifetimeBackstop);
        Assert.False(receipt.IsAtCap);
        Assert.True((receipt with { Round = 7 }).IsAtCap);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void Lifetime_limit_is_a_policy_decision_even_when_both_caps_are_reached(int round)
    {
        var scenario = new LedgerScenario();
        var developer = scenario.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var facts = new FailedGoalVerifyingFindingRouteFacts(scenario.Reviewer.Id, AgentRole.Reviewer,
            "attempt", developer.Id, false, AgentRole.Developer, false, round, 7, 4, false, null,
            [new FailedGoalFindingRouteTask(developer.Id, AgentRole.Developer)], LifetimeRound: 21, LifetimeBackstop: 21);
        Assert.Equal(FailedGoalVerifyingFindingRouteKind.LifetimeBackstopReached,
            FailedGoalRecoveryPolicy.SelectVerifyingFindingRoute(facts).Kind);
        Assert.Equal(FailedGoalVerifyingFindingRouteKind.Routed,
            FailedGoalRecoveryPolicy.SelectVerifyingFindingRoute(facts with { Round = 1, LifetimeRound = 20 }).Kind);
    }

    [Theory]
    [InlineData(FindingCategory.SpecDefect)]
    [InlineData(FindingCategory.OperatorOwned)]
    public void Closing_operator_owned_finding_is_not_a_shrink(FindingCategory category)
    {
        var scenario = new LedgerScenario();
        scenario.Verify([Finding("F-SAME"), Finding("S-1", category)]);
        scenario.AutoRetry();
        scenario.Verify([Finding("F-SAME"), Finding("S-1", category) with { State = ReviewFindingState.Resolved }]);
        Assert.Equal(2, ReviewRetryCapReceipt.Create(scenario.Goal, 7).Round);
        Assert.Null(ReviewRetryBudgetLedger.Evaluate(scenario.Goal).ResetMarker);
    }

    [Fact]
    public void Null_ledger_and_untyped_kernel_reviewer_retry_do_not_reset_budget()
    {
        var scenario = new LedgerScenario();
        scenario.Verify([Finding("F-SAME")]);
        scenario.AutoRetry();
        scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Reviewer.Id, "Human sounding prose without an intent");
        scenario.Verify(null);
        scenario.Verify([Finding("F-SAME")]);
        Assert.Equal(2, ReviewRetryCapReceipt.Create(scenario.Goal, 7).Round);
        Assert.Null(ReviewRetryBudgetLedger.Evaluate(scenario.Goal).ResetMarker);
    }

    [Fact]
    public void Latest_reset_wins_and_its_marker_is_consumed_by_first_automatic_retry()
    {
        var scenario = new LedgerScenario();
        scenario.Verify([Finding("F-A")]);
        scenario.AutoRetry();
        scenario.Intent();
        scenario.Verify([Finding("F-B")]);
        Assert.Equal("review_budget_reset=open-set-shrank closed=F-A", ReviewRetryBudgetLedger.Evaluate(scenario.Goal).ResetMarker);
        scenario.Intent();
        Assert.StartsWith("review_budget_reset=operator-retry intent=", ReviewRetryBudgetLedger.Evaluate(scenario.Goal).ResetMarker);
        scenario.AutoRetry();
        Assert.Null(ReviewRetryBudgetLedger.Evaluate(scenario.Goal).ResetMarker);
    }

    private static (int[] Rounds, int[] LifetimeRounds, int TimestampCount) ExerciseOrdering(bool advanceClock)
    {
        var scenario = new LedgerScenario(advanceClock);
        var rounds = new List<int>();
        var lifetimes = new List<int>();
        void Observe()
        {
            var receipt = ReviewRetryCapReceipt.Create(scenario.Goal, 7);
            rounds.Add(receipt.Round);
            lifetimes.Add(receipt.LifetimeRound!.Value);
        }
        scenario.Verify([Finding("F-A")]); Observe();
        scenario.AutoRetry(); Observe();
        scenario.Verify([Finding("F-B")]); Observe();
        scenario.AutoRetry(); Observe();
        scenario.Intent(); Observe();
        scenario.AutoRetry(); Observe();
        return (rounds.ToArray(), lifetimes.ToArray(), scenario.Goal.Timeline.Select(evt => evt.OccurredAt).Distinct().Count());
    }

    private static ReviewFinding Finding(string id, FindingCategory category = FindingCategory.Correctness) =>
        new(id, ReviewFindingState.Open, new ReviewFindingLocation($"src/{id}.cs", $"{id}.Run"),
            $"Defect {id} remains.", FindingSeverity.Blocking, category);

    private sealed class LedgerScenario
    {
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Reviewer { get; }
        private readonly TaskSpec _developer;
        private readonly TestClock _clock = new();
        private readonly bool _advanceClock;
        private int _round;

        public LedgerScenario(bool advanceClock = false)
        {
            _advanceClock = advanceClock;
            Kernel = new AgentOrchestratorKernel(_clock);
            _developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            Reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
            Goal = Kernel.CreateGoal("Review budget ordering", [_developer, Reviewer]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
        }

        public void Verify(IReadOnlyList<ReviewFinding>? ledger)
        {
            Tick();
            // These are ledger-reader fixtures; conductor facts exercise real merge and proof rules.
            Kernel.RecordTaskVerification(Goal.Id, Reviewer.Id, new TaskVerificationRecord(
                $"ledger-{++_round}", "C:\\tmp", 1, "ledger fixture", "", _clock.UtcNow, MergedReviewFindings: ledger));
        }

        public void AutoRetry()
        {
            Tick();
            Kernel.RetryTask(Goal.Id, _developer.Id, "auto-review-retry round fixture");
        }

        public void Intent(OperatorActorKind actor = OperatorActorKind.Human, RetryRoundKind? kind = RetryRoundKind.Standard,
            string outcome = "applied", bool reviewer = true)
        {
            Tick();
            Kernel.RecordOperatorIntentApplied(Goal.Id, Guid.NewGuid().ToString("N"), "retry",
                (reviewer ? Reviewer : _developer).Id.Value, "operator", "cli", "local-process", "Applied retry fixture",
                actorKind: actor, outcome: outcome, retryRoundKind: kind);
        }

        private void Tick() { if (_advanceClock) _clock.UtcNow = _clock.UtcNow.AddSeconds(1); }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    }
}
