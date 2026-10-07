using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Process liveness and build slots are seamed; the completion gate and fixed clock control each attempt.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreReviewEvidenceHoldDecision
{
    [Fact]
    public void InFlightAttempt_HoldsWithOriginalIdentityOwnerAndRecordedDecision()
    {
        using var fixture = new EvidenceFixture();
        var started = fixture.Advance();
        AssertRunningHold(fixture, started);

        var running = fixture.Advance();
        AssertRunningHold(fixture, running);
        Assert.Equal(started.StableIdentity, running.StableIdentity);
        Assert.Equal(0, fixture.Runs);
        Assert.Equal(0, fixture.Dispatches);
    }

    [Fact]
    public void BuildSlotsBusy_HoldsWithOriginalReasonNoOwnerAndRecordedDecision()
    {
        using var fixture = new EvidenceFixture(buildSlotsBusy: true);
        AssertRunningHold(fixture, fixture.Advance());
        fixture.Handle.CompleteForTests();
        var attempt = fixture.UnreconciledAttempt();

        var held = fixture.Advance();
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, attempt.Outcome);
        Assert.Equal($"Background pre-review evidence did not run (BlockedBuildSlot); " +
            $"retry on next conduct tick. attempt={attempt.AttemptId}: " +
            (attempt.Detail ?? "no result artifact was produced"), held.Reason);
        Assert.Null(held.StableIdentity);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        AssertDecision(fixture.Goal, attempt, held, "did-not-run", 3, "attempt-did-not-run");
        Assert.Null(fixture.Reviewer.PreReviewEvidenceReceipt);
        Assert.Equal(0, fixture.Runs);
        Assert.Equal(0, fixture.Dispatches);
    }

    private static void AssertRunningHold(EvidenceFixture fixture, ConductorAdvanceOutcome.Held held)
    {
        var attempt = fixture.UnreconciledAttempt();
        Assert.Equal($"PRE_REVIEW_FOCUSED_EVIDENCE_RUNNING: attempt {attempt.Ordinal}, " +
            $"0m0s elapsed (attempt={attempt.AttemptId})", held.Reason);
        Assert.Equal($"pre-review-evidence:{attempt.AttemptId}", held.StableIdentity);
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, held.Owner);
        AssertDecision(fixture.Goal, attempt, held, "attempt-running", 2, "attempt-running");
    }

    private static void AssertDecision(
        Goal goal, ConductorParallelAcceptanceAttempt attempt,
        ConductorAdvanceOutcome.Held held, string condition, int rung, string evidence)
    {
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("pre-review-evidence", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(rung, decision.Rung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(held.Reason, decision.Reason);
        Assert.Equal(new PolicyDecisionFact[]
        {
            new("goalId", goal.Id.Value),
            new("condition", condition),
            new("attemptId", attempt.AttemptId),
            new("attemptOutcome", attempt.Outcome.ToString()),
            new("holdReason", held.Reason)
        }, decision.Facts.ToArray());

        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(held);
        Assert.Equal("Held", payload.OutcomeKind);
        Assert.Equal("WorkspaceReady", payload.LifecycleState);
        Assert.Null(payload.EscalationKind);
        var recorded = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal(decision.Stage, recorded.Stage);
        Assert.Equal(decision.Action, recorded.Action);
        Assert.Equal(decision.Rung, recorded.Rung);
        Assert.Equal(decision.DiscriminatingEvidence, recorded.DiscriminatingEvidence);
        Assert.Equal(decision.Reason, recorded.Reason);
        Assert.Equal(decision.Facts.ToArray(), recorded.Facts.ToArray());
    }

    private sealed class EvidenceFixture : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly ConductorDriver _driver;
        private readonly ConductorParallelAcceptanceAttemptCoordinator _coordinator;
        private readonly ConductorParallelAcceptanceAttemptCompletionGateForTests _gate = new();
        internal Goal Goal { get; }
        internal TaskSpec Reviewer { get; }
        internal int Runs;
        internal int Dispatches;
        internal ConductorParallelAcceptanceAttemptCompletionGateForTests.Handle Handle =>
            _gate.RequiredHandleForTests(Goal.Id.Value);

        internal EvidenceFixture(bool buildSlotsBusy = false)
        {
            var (kernel, goal) = SoftwareGoal();
            Goal = goal;
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Reviewer.Id))
                PassVerification(kernel, Goal, task);

            _coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(_root, "pre-review-evidence-attempts"),
                executionDirectory: _root,
                isProcessAlive: _ => true,
                attemptCompletionGateForTests: _gate,
                timeProvider: new FixedTimeProvider(),
                acquireStableSlotLease: (_, _) => buildSlotsBusy
                    ? throw new DotnetBuildSlotsBusyException(
                        new DotnetBuildLeaseAcquisition.SlotsBusy("pre-review-evidence", []))
                    : null);
            _driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("hold-decision-sha"),
                runFocusedEvidence: (_, request) =>
                {
                    Runs++;
                    return PassingPreReviewEvidence(request);
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    Dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: _coordinator,
                executionDirectory: _root);
        }

        internal ConductorParallelAcceptanceAttempt UnreconciledAttempt() =>
            Assert.Single(_coordinator.GetUnreconciledAttempts([Goal.Id.Value]));

        internal ConductorAdvanceOutcome.Held Advance()
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
                _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive).Outcome);
            Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
            return held;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        private sealed class FixedTimeProvider : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => 0;
        }
    }
}
