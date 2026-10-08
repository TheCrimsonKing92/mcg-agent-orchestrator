using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using Scenario = ConductorDriverTestsTimedOutSelectionRerun.Scenario;

// Scenario owns a unique artifact root and clock; process and runner seams are injected.
public sealed class ConductorDriverTestsTimedOutRerunDecision
{
    private const string CandidateSha = "abc1234";
    private const string Selection = "reviewer-focused-evidence-infrastructure-tests-fullyqualifiedname-mtptestrunnerscripttests";
    private const string Reason = "verification-inconclusive-timed-out-selection-rerun";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleContextHold_CarriesNamedDecisionAndOriginalReason(bool resumeReceipt)
    {
        using var scenario = new Scenario();
        if (resumeReceipt)
        {
            var interruption = Assert.Throws<InvalidOperationException>(() => scenario.Driver(interruptAfterReceipt: true)
                .AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive));
            Assert.Equal("fixture interruption after durable receipt", interruption.Message);
            scenario.Reload();
        }
        var contextReads = 0;
        var runs = scenario.RunRequests.Count;
        string? actualVersion = null;
        var driver = MakeDriver(
            getFacts: goal =>
            {
                // Recovery snapshots this version before task-fact enumeration reads context and appends another note.
                actualVersion = ContextVersion(goal);
                return GoalLifecycleFacts.None;
            },
            getPreReviewEvidenceContext: _ =>
            {
                contextReads++;
                scenario.Kernel.RecordTaskNote(scenario.Goal.Id, scenario.Owner.Id, "fixture changed recovery context");
                return NoPreReviewContext(CandidateSha);
            },
            focusedEvidenceAttemptCoordinator: new(scenario.AttemptsRoot,
                runInline: true, acquireStableSlotLease: (_, _) => null));

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);

        var record = AssertDecision(held.Decision, "Hold", 101, "stale-recovery-facts", held.Reason, scenario);
        var expectedVersion = Assert.Single(record.Facts, fact => fact.Name == "contextVersion").Value;
        Assert.NotNull(actualVersion);
        Assert.NotEqual(expectedVersion, actualVersion);
        // Reason templates transcribed from main 147a5f08d, including the timed-out policy reason.
        var expectedReason = $"{Reason}: rerun {Selection} once for {Scenario.Candidate.Canonical}. " +
            "Recovery effects were held because the goal/task/attempt authority changed before application " +
            $"(stale-recovery-facts; expected={expectedVersion}; actual={actualVersion}; state=Failed).";
        Assert.Equal(expectedReason, held.Reason);
        Assert.True(contextReads > 0);
        Assert.Equal(runs, scenario.RunRequests.Count);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Dispatches);
    }

    [Fact]
    [Trait("Category", "CrossTick")]
    public void BackgroundPendingThenObservationEscalation_CarryNamedDecisionsAndOriginalReasons()
    {
        using var scenario = new Scenario();
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(scenario.AttemptsRoot,
            isProcessAlive: pid => pid == 7103,
            launchOwnedProcess: _ => { launches++; return new(7103); },
            acquireStableSlotLease: (_, _) => null);
        var driver = scenario.Driver(coordinator: coordinator);

        var started = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        var attempt = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        var pendingReason = $"Background {Reason} focused evidence is running in attempt {attempt.AttemptId}.";
        Assert.Equal(pendingReason, started.Reason);
        AssertDecision(started.Decision, "Hold", 116, "timed-out-rerun-evidence-pending", pendingReason, scenario);
        Assert.Equal(ConductorHoldOwner.None, started.Owner);

        var running = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Equal(pendingReason, running.Reason);
        AssertDecision(running.Decision, "Hold", 116, "timed-out-rerun-evidence-pending", pendingReason, scenario);
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, running.Owner);
        Assert.Equal(attempt.AttemptId, Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value])).AttemptId);

        var candidate = ConductorParallelAcceptanceCandidate.Create(scenario.Goal, 0, [], CandidateSha, null);
        coordinator.RunAttemptForTests(attempt, candidate, ConductorAutonomyPolicy.Permissive,
            (current, _, _, _, _) => ConductorParallelAcceptanceRunResult.Fault(current,
                new InvalidOperationException("fixture focused evidence failed")));
        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(
            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);

        var expectedReason = $"BACKGROUND_FOCUSED_EVIDENCE_FAILED: {Reason} focused evidence run failed. " +
            $"attempt={attempt.AttemptId}: fixture focused evidence failed";
        Assert.Equal(expectedReason, escalated.Reason);
        AssertDecision(escalated.Decision, "Escalate", 113, "timed-out-rerun-observation-escalated", expectedReason, scenario);
        Assert.Equal(expectedReason, Assert.Single(scenario.Escalations));
        Assert.Equal(1, launches);
        Assert.Empty(scenario.RunRequests);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Dispatches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedRoundEscalation_CarriesNamedDecisionAndOriginalReason(bool renewedTimeout)
    {
        using var scenario = new Scenario(missingSelectionMapping: !renewedTimeout);
        var rounds = TesterInconclusiveRoundInputsReader.Read(scenario.Goal, scenario.Tester);
        Assert.NotNull(rounds);
        Assert.True(rounds.InputsUnchanged);
        var tester = scenario.Tester;
        // Exact interpolation from main 147a5f08d; the round and classifier inputs precede this tick.
        var expectedReason = $"Tester task {tester.Id.Value[..Math.Min(8, tester.Id.Value.Length)]} stayed verification-inconclusive on unchanged inputs; operator action required. {rounds.DescribeUnchanged(DispatchFailureClassifier.Classify(tester, tester.LastVerification!).EvidenceSummary)}";

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Driver(timedOut: renewedTimeout)
            .AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);

        Assert.Equal(expectedReason, escalated.Reason);
        AssertDecision(escalated.Decision, "Escalate", 115, "timed-out-round-unchanged", expectedReason, scenario);
        Assert.Equal(expectedReason, Assert.Single(scenario.Escalations));
        Assert.Equal(renewedTimeout ? 1 : 0, scenario.RunRequests.Count);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Dispatches);
    }

    [Fact]
    public void RetryAuthorityChangedHold_CarriesNamedDecisionAndOriginalReason()
    {
        using var scenario = new Scenario();
        var interruption = Assert.Throws<InvalidOperationException>(() => scenario.Driver(interruptAfterReceipt: true)
            .AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive));
        Assert.Equal("fixture interruption after durable receipt", interruption.Message);
        scenario.Reload();
        var retries = 0;
        var dispatches = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
            focusedEvidenceAttemptCoordinator: new(scenario.AttemptsRoot,
                runInline: true, acquireStableSlotLease: (_, _) => null),
            retryTaskWithCause: (gid, tid, message, round, cause) =>
            {
                retries++;
                scenario.Kernel.RetryTask(gid, tid, message, cause, retryRoundKind: round);
                scenario.Kernel.RecordTaskDispatch(gid, tid,
                    new("fixture", "verify", scenario.AttemptsRoot, DateTimeOffset.Parse("2026-10-05T01:00:00Z")));
                return scenario.Tester;
            },
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); });

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);

        const string expectedReason = "Timeout rerun retry changed dispatch authority; re-observe on next tick.";
        Assert.Equal(expectedReason, held.Reason);
        AssertDecision(held.Decision, "Hold", 114, "timed-out-rerun-retry-authority-changed", expectedReason, scenario);
        Assert.Equal(1, retries);
        Assert.Equal(0, dispatches);
        Assert.Single(scenario.RunRequests);
        Assert.Equal(WorkTaskStatus.Running, scenario.Tester.Status);
    }

    private static PolicyDecisionRecord AssertDecision(PolicyDecisionRecord? record, string action, int rung,
        string evidence, string reason, Scenario scenario)
    {
        Assert.NotNull(record);
        Assert.Equal("failed-goal-recovery", record.Stage);
        Assert.Equal(action, record.Action);
        Assert.Equal(rung, record.Rung);
        Assert.Equal(evidence, record.DiscriminatingEvidence);
        Assert.Equal(reason, record.Reason);
        Assert.Equal(scenario.Goal.Id.Value, Assert.Single(record.Facts, fact => fact.Name == "goalId").Value);
        Assert.Equal(scenario.Tester.Id.Value, Assert.Single(record.Facts, fact => fact.Name == "targetTaskId").Value);
        return record;
    }

    // Context identity from main 147a5f08d; these timestamps identify facts, never elapsed time.
    private static string ContextVersion(Goal goal)
    {
        var taskState = string.Join(";", goal.Tasks.Select(task => string.Join(":",
            task.Id.Value, task.Status, task.EmptyOutputRetryCount, task.CriterionRetryCount,
            task.LastProcess is { } process
                ? $"process:{process.ProcessId}:{process.StartedAt.UtcTicks}"
                : task.LastVerification is { } verification
                    ? $"verification:{verification.CompletedAt.UtcTicks}:{verification.ChildProcessId?.ToString() ?? "none"}"
                    : "unobserved-attempt",
            task.LastVerification?.CompletedAt.UtcTicks.ToString() ?? "none")));
        var lastEvent = goal.Timeline.LastOrDefault();
        return string.Join("|", goal.Id.Value, goal.Status, GoalLifecycleState.Failed,
            goal.AuthoritativeBrief.Version, goal.AutomaticAcceptanceRetryCount, goal.Timeline.Count,
            lastEvent?.Kind.ToString() ?? "none", lastEvent?.OccurredAt.UtcTicks.ToString() ?? "none", taskState);
    }
}
