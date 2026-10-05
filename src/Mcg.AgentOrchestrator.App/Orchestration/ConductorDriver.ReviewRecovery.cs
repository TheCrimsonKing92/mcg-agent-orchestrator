using System.Collections.Immutable;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private bool TryObserveReviewContractRecovery(
        Goal goal,
        out FailedGoalFindingObservation observation)
    {
        observation = FailedGoalFindingObservation.None;
        var candidate = FailedGoalRecoveryPolicy.SelectReviewContractCandidate(
            goal.Tasks.Select(task => new FailedGoalReviewContractCandidate(
                task.Id,
                task.RequiredRole,
                task.Status,
                task.LastVerification?.ReviewFindingContractViolation?.Code,
                task.LastVerification?.MergedReviewFindings is not null)));
        if (candidate is null)
            return false;

        var reviewerTask = goal.Tasks.Single(task => task.Id == candidate.TaskId);
        var violation = reviewerTask.LastVerification?.ReviewFindingContractViolation ??
            throw new InvalidOperationException("Selected review-contract candidate lost its violation evidence.");

        IReadOnlyList<ReviewFinding> canonicalLedger;
        try
        {
            canonicalLedger = AutoReviewRetryConvergenceBriefBuilder
                .ReadStructuredReviewFindingState(goal, reviewerTask);
        }
        catch (Exception ex) when (
            ex is ReviewFindingConvergenceException or InvalidOperationException or ArgumentException)
        {
            observation = FailedGoalRecoveryPolicy.SelectReviewContractObservation(
                new FailedGoalReviewContractSelectionFacts(
                    candidate,
                    $"{reviewerTask.RequiredRole} contract-repair could not reconstruct the canonical finding ledger for task {reviewerTask.Id.Value[..8]}; " +
                        $"violation={violation.Code}; diagnostic={TrimForConductorMessage(ex.Message)}; operator action required.",
                    null,
                    null,
                    0,
                    MaxReviewFindingContractRepairsPerRound,
                    string.Empty,
                    string.Empty,
                    BuildFailedGoalAttemptIdentity(reviewerTask)));
            return true;
        }
        var retryCapEvidence = violation.Code is ReviewFindingConvergence.UnprovenResolutionAtCapViolationCode or
                ReviewFindingConvergence.MissingReviewRetryCapReceiptViolationCode
            ? BuildReviewCapDecisionMessage(
                 goal,
                 reviewerTask,
                 reviewerTask.LastDispatch?.ReviewRetryCap,
                 violation.Message,
                 FormatVerifyingRoleOutputArtifact(reviewerTask),
                 canonicalLedger)
            : null;
        var touchProofDiagnostic = reviewerTask.LastVerification?.ReviewFindingTouchProofDiagnostic;
        var touchProofUnavailableEvidence =
            (violation.Code is ReviewFindingConvergence.IdentityMovedViolationCode or
                ReviewFindingConvergence.UntouchedReopenViolationCode) &&
            touchProofDiagnostic is { Length: > 0 }
            ? $"{reviewerTask.RequiredRole} review-finding contract cannot classify touch-dependent violation {violation.Code} " +
                $"for task {reviewerTask.Id.Value[..8]} because system-derived round-diff proof is unavailable; " +
                $"suppression=missing-system-derived-round-diff-proof; operator adjudication is required and the mechanical repair budget was not consumed. " +
                $"Diagnostic: {TrimForConductorMessage(touchProofDiagnostic)}"
            : null;
        var priorRepairs = CountReviewerContractRepairsInCurrentRound(goal, reviewerTask);
        var repairLimitEvidence =
            $"{reviewerTask.RequiredRole} exhausted the contract-repair limit ({MaxReviewFindingContractRepairsPerRound}) in the same review round for task {reviewerTask.Id.Value[..8]}; " +
                $"violation_code={violation.Code}; prior_stable_id={violation.PriorStableId ?? "none"}; " +
                $"submitted_stable_id={violation.SubmittedStableId ?? "none"}; " +
                $"prior_location={violation.PriorLocation?.ToString() ?? "none"}; " +
                $"submitted_location={violation.SubmittedLocation?.ToString() ?? "none"}; " +
                $"canonical_open_count={canonicalLedger.Count(finding => finding.State == ReviewFindingState.Open)}. " +
                BuildAdjudicateRemedy(goal.Status);
        var selectionFacts = new FailedGoalReviewContractSelectionFacts(
                candidate,
                null,
                retryCapEvidence,
                touchProofUnavailableEvidence,
                priorRepairs,
                MaxReviewFindingContractRepairsPerRound,
                repairLimitEvidence,
                string.Empty,
                BuildFailedGoalAttemptIdentity(reviewerTask));
        observation = FailedGoalRecoveryPolicy.SelectReviewContractObservation(selectionFacts);
        if (observation.Kind != FailedGoalFindingObservationKind.ReviewContractRepairEnvelopeAvailable)
            return true;
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildContractRepairBrief(
            goal,
            reviewerTask,
            violation,
            priorRepairs + 1,
            MaxReviewFindingContractRepairsPerRound,
            FormatVerifyingRoleOutputArtifact(reviewerTask));
        observation = FailedGoalRecoveryPolicy.SelectReviewContractObservation(selectionFacts with { RepairEnvelope = brief });
        return true;
    }

    private bool TryObserveVerifyingFindingRecovery(
        Goal goal,
        ConductorAutonomyPolicy policy,
        IReadOnlyList<string> landingFileScopes,
        out FailedGoalFindingObservation observation)
    {
        observation = FailedGoalFindingObservation.None;
        var pendingNotes = new List<FailedGoalPendingNote>();
        var trigger = goal.Tasks
            .Select(task => BuildTesterDeveloperOwnedFindingTrigger(goal, task))
            .FirstOrDefault(candidate =>
                candidate is not null &&
                VerifyingFindingCurrency.IsCurrent(
                    goal,
                    candidate.TriggeringTask,
                    candidate.TriggeringTask.LastVerification!));
        if (trigger is not null &&
            (TryRouteTesterFindingToPendingEvidence(goal, policy, trigger, out observation) ||
             TryDeliverGreenTesterFindingEvidence(goal, policy, trigger, out observation)))
        {
            return true;
        }
        if (trigger is null)
        {
            foreach (var requestingTask in goal.Tasks.Where(task => task.LastVerification is not null))
            {
                if (TryBuildFindingEvidenceRequest(goal, requestingTask, policy, out observation))
                {
                    return true;
                }
            }
            trigger = goal.Tasks
                .Select(task => BuildVerifyingFindingTrigger(goal, task))
                .FirstOrDefault(candidate => candidate is not null);
        }

        if (trigger is null)
        {
            return false;
        }

        var triggeringTask = trigger.TriggeringTask;
        var outputArtifact = FormatVerifyingRoleOutputArtifact(triggeringTask);
        if (triggeringTask.RequiredRole == AgentRole.Reviewer)
        {
            pendingNotes.AddRange(BuildSuppressedAutoReviewRetryFindingNotes(triggeringTask, trigger.SuppressedFindings));
        }

        ReviewRetryRoute? reviewerRoute = null;
        if (triggeringTask.RequiredRole is AgentRole.Reviewer or AgentRole.Tester)
        {
            if (triggeringTask.RequiredRole == AgentRole.Reviewer && goal.RefinedSpec is { AcceptanceCriteria.Count: > 0 } &&
                !WorkerResultBlockers.TryFindCriteriaVerdicts(
                    triggeringTask.LastVerification,
                    out _,
                    out var criteriaDiagnostic))
            {
                pendingNotes.Add(new FailedGoalPendingNote(
                    triggeringTask.Id,
                    $"CRITERIA_ATTESTATION missing: {TrimForConductorMessage(criteriaDiagnostic)}"));
            }

            reviewerRoute = ResolveReviewerRetryRoute(goal, triggeringTask, trigger.Finding);
        }

        var receipt = ReviewRetryCapReceipt.Create(goal, policy.ReviewAutoRetryStopRound, policy.ReviewAutoRetryLifetimeMultiplier);
        var round = receipt.Round;
        var route = FailedGoalRecoveryPolicy.SelectVerifyingFindingRoute(
            new FailedGoalVerifyingFindingRouteFacts(
                triggeringTask.Id,
                triggeringTask.RequiredRole,
                BuildFailedGoalAttemptIdentity(triggeringTask),
                trigger.TargetTask?.Id,
                trigger.RequiresCommittedTarget,
                reviewerRoute?.TargetRole,
                reviewerRoute?.EscalateToOperator == true,
                round,
                policy.ReviewAutoRetryStopRound,
                policy.ReviewAutoRetryWarningRound,
                IsMissingFindingResultRetryEligible(goal, triggeringTask, trigger.Finding),
                AutomaticWorkerRetryCause.Resolve(triggeringTask),
                goal.Tasks
                    .TakeWhile(task => task.Id != triggeringTask.Id)
                    .Select(task => new FailedGoalFindingRouteTask(task.Id, task.RequiredRole))
                    .ToImmutableArray(),
                SummarizeRepeatedFailingSet(goal), LifetimeRound: receipt.LifetimeRound ?? 0, LifetimeBackstop: receipt.LifetimeBackstop ?? 0));

        if (route.Kind == FailedGoalVerifyingFindingRouteKind.OperatorEvidenceRequired)
        {
            observation = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
                $"{triggeringTask.RequiredRole} blocker requires operator-owned evidence; auto-retry skipped for task {triggeringTask.Id.Value[..8]}. " +
                $"Route: {reviewerRoute?.Reason}. Findings: {TrimForConductorMessage(trigger.Finding)}. Full {triggeringTask.RequiredRole} output: {outputArtifact}");
            observation = observation with { PendingNotes = pendingNotes.ToImmutableArray() };
            return true;
        }

        if (route.Kind == FailedGoalVerifyingFindingRouteKind.TargetUnavailable)
        {
            observation = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingRouteUnavailable,
                $"{triggeringTask.RequiredRole} blocker could not be routed to an upstream {route.TargetRole} task; operator action required. " +
                $"Findings: {TrimForConductorMessage(trigger.Finding)}. Full {triggeringTask.RequiredRole.ToString().ToLowerInvariant()} output: {outputArtifact}");
            observation = observation with { PendingNotes = pendingNotes.ToImmutableArray() };
            return true;
        }

        if (route.Kind == FailedGoalVerifyingFindingRouteKind.RepeatedFailingTestSet)
        {
            var repeated = SummarizeRepeatedFailingSet(goal);
            var hold = new PreReviewRepeatedFailureHold(repeated.RepeatedTests, repeated.ConsecutiveRounds);
            observation = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingRepeatedFailingTestSet, hold.Reason);
            observation = observation with { PendingNotes = pendingNotes.ToImmutableArray() };
            return true;
        }

        var targetTask = goal.Tasks.Single(task => task.Id == route.TargetTaskId);
        if (route.Kind is FailedGoalVerifyingFindingRouteKind.RetryCapReached or FailedGoalVerifyingFindingRouteKind.LifetimeBackstopReached)
        {
            observation = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingRetryCapReached,
                triggeringTask.RequiredRole == AgentRole.Reviewer
                    ? BuildReviewCapDecisionMessage(
                        goal,
                        triggeringTask,
                        receipt,
                        trigger.Finding,
                        outputArtifact,
                        triggeringTask.LastVerification?.MergedReviewFindings ?? [])
                    : BuildTesterReviewCapDecisionMessage(goal, triggeringTask, targetTask, receipt, trigger.Finding, outputArtifact));
            observation = observation with { PendingNotes = pendingNotes.ToImmutableArray() };
            return true;
        }

        if (route.Kind == FailedGoalVerifyingFindingRouteKind.MissingFindingResult)
        {
            observation = FailedGoalFindingObservation.Routed(
                FailedGoalFindingObservationKind.FindingResultMissing,
                triggeringTask.Id,
                route.AttemptIdentity,
                $"auto-review-retry round {route.Round}: {triggeringTask.RequiredRole} task {triggeringTask.Id.Value[..8]} " +
                "reported needs-work, but its structured finding result was missing or unparseable and no current open blocking finding exists. " +
                "Re-run the verifying role against the current Developer output; do not reopen the Developer from superseded finding history.",
                warningMessage: null,
                route.RoundKind,
                route.RetryCause);
            observation = observation with { PendingNotes = pendingNotes.ToImmutableArray() };
            return true;
        }

        var triggerLabel = triggeringTask.RequiredRole == AgentRole.Reviewer
            ? "verdict=needs-work"
            : "WORKER_RESULT blocker";
        string message;
        try
        {
            message = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
                goal,
                targetTask,
                triggeringTask,
                trigger.Finding,
                triggerLabel,
                route.TargetRole,
                round,
                outputArtifact,
                landingFileScopes);
        }
        catch (ReviewFindingConvergenceException ex)
        {
            observation = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingConvergenceViolation,
                $"review finding convergence violation code={ex.Code} previous_open={ex.PreviousOpenCount} next_open={ex.NextOpenCount}; " +
                $"{ex.Message} Loop stopped before another retry brief was issued. Full reviewer output: {outputArtifact}");
            observation = observation with { PendingNotes = pendingNotes.ToImmutableArray() };
            return true;
        }

        var warning = route.EmitWarning
            ? $"auto-review-retry escalation-warning round {round}/{policy.ReviewAutoRetryStopRound - 1}: " +
                $"continuing automatic retry for task {targetTask.Id.Value[..8]}; operator review will be required at round {policy.ReviewAutoRetryStopRound}."
            : null;
        observation = FailedGoalFindingObservation.Routed(
            FailedGoalFindingObservationKind.FindingRouteObserved,
            targetTask.Id,
            BuildFailedGoalAttemptIdentity(targetTask),
            InsertReviewBudgetResetMarker(message, ReviewRetryBudgetLedger.Evaluate(goal).ResetMarker),
            warning,
            route.RoundKind,
            route.RetryCause);
        observation = observation with { PendingNotes = pendingNotes.ToImmutableArray() };
        return true;
    }
    private VerifyingFindingTrigger? BuildVerifyingFindingTrigger(Goal goal, TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Failed)
        {
            return null;
        }

        if (task.LastVerification is { } latestVerification &&
            !VerifyingFindingCurrency.IsCurrent(goal, task, latestVerification))
        {
            return null;
        }

        string blocker;
        if (task.RequiredRole == AgentRole.Reviewer)
        {
            var hasNeedsWork = WorkerResultBlockers.TryFindUnsuppressedNeedsWorkVerdict(
                task.LastVerification,
                goal.EffectiveAcceptanceCriteriaCorrections,
                out blocker,
                out var suppressedFindings);
            var hasBlockedAtCap = WorkerResultBlockers.TryFindUnsuppressedBlockedAtCapVerdict(
                task.LastVerification,
                goal.EffectiveAcceptanceCriteriaCorrections,
                out var capBlocker,
                out var capSuppressedFindings);
            if (hasBlockedAtCap && task.LastDispatch?.ReviewRetryCap is not { IsAtCap: true })
            {
                return null;
            }

            if (hasNeedsWork || hasBlockedAtCap)
            {
                return new VerifyingFindingTrigger(
                    task,
                    hasBlockedAtCap ? capBlocker : blocker,
                    hasBlockedAtCap ? capSuppressedFindings : suppressedFindings,
                    null);
            }
        }

        if (task.RequiredRole != AgentRole.Tester ||
            !WorkerResultBlockers.TryGetTestsStatus(task.LastVerification, out var testsStatus) ||
            testsStatus != WorkerResultBlockers.TestsStatus.Fail ||
            !WorkerResultBlockers.TryFindHardFailureBlocker(task.LastVerification, out blocker))
        {
            return null;
        }

        var upstreamDeveloper = goal.Tasks
            .TakeWhile(t => t.Id != task.Id)
            .LastOrDefault(t => t.RequiredRole == AgentRole.Developer && HasCommittedOutput(t));
        if (upstreamDeveloper is null)
        {
            return null;
        }

        return new VerifyingFindingTrigger(task, blocker, [], upstreamDeveloper);
    }

    private static IReadOnlyList<FailedGoalPendingNote> BuildSuppressedAutoReviewRetryFindingNotes(
        TaskSpec reviewerTask,
        IReadOnlyList<string> suppressedFindings)
    {
        return suppressedFindings
            .Select(finding => new FailedGoalPendingNote(
                reviewerTask.Id,
                $"Suppressed auto-review-retry finding matching operator criteria correction: {TrimForConductorMessage(finding)}"))
            .ToArray();
    }

    private static ReviewRetryRoute? ResolveReviewerRetryRoute(
        Goal goal,
        TaskSpec reviewerTask,
        string blockerProse)
    {
        try
        {
            var findings = AutoReviewRetryConvergenceBriefBuilder
                .ReadStructuredReviewFindingState(goal, reviewerTask)
                .Where(finding => finding.State == ReviewFindingState.Open &&
                    finding.Severity == FindingSeverity.Blocking)
                .ToArray();
            return reviewerTask.RequiredRole == AgentRole.Tester &&
                (findings.Length == 0 || findings.Any(finding => finding.Category == FindingCategory.Unspecified))
                ? null : ReviewFindingRouting.Resolve(findings, blockerProse);
        }
        catch (Exception ex) when (
            ex is ReviewFindingConvergenceException or InvalidOperationException or ArgumentException)
        {
            return reviewerTask.RequiredRole == AgentRole.Tester ? null : ReviewFindingRouting.Resolve([], blockerProse);
        }
    }
    private static bool HasCommittedOutput(TaskSpec task) =>
        VerifyingFindingCurrency.HasCommittedOutput(task);

    private static int CountReviewerContractRepairsInCurrentRound(Goal goal, TaskSpec reviewerTask)
    {
        var currentRoundStartedAt = GetCurrentReviewerRoundStart(goal, reviewerTask);
        return goal.Timeline.Count(evt =>
            evt.TaskId == reviewerTask.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.StartsWith(ReviewContractRepairRetryMessagePrefix, StringComparison.Ordinal) &&
            evt.OccurredAt >= currentRoundStartedAt);
    }

    private static DateTimeOffset GetCurrentReviewerRoundStart(Goal goal, TaskSpec reviewerTask)
    {
        // A fresh review starts at any non-reviewer retry or any reviewer retry that is not one of
        // the bounded mechanical receipt/contract repairs. Mechanical retries remain in the same
        // round so neither budget can be reset by alternating the two repair paths.
        return goal.Timeline
            .Where(evt =>
                evt.Kind == ProgressKind.TaskRetried &&
                evt.TaskId is not null &&
                (evt.TaskId != reviewerTask.Id ||
                    !MechanicalReviewerRetryMessagePrefixes.Any(prefix =>
                        evt.Message.StartsWith(prefix, StringComparison.Ordinal))))
            .Select(evt => evt.OccurredAt)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
    }

    private static string FormatVerifyingRoleOutputArtifact(TaskSpec task)
    {
        var verification = task.LastVerification;
        if (!string.IsNullOrWhiteSpace(verification?.StandardOutputPath))
        {
            return verification.StandardOutputPath!;
        }

        return $"{task.RequiredRole.ToString().ToLowerInvariant()} task {task.Id.Value[..8]} verification output";
    }
}
