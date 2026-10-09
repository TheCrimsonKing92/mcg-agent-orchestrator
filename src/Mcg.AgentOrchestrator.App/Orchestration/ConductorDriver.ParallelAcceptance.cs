using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal Func<Goal, AdditiveConflictProbeResult> PreLandingMergeConflictProbe { get; set; } =
        _ => new(null, null, AdditiveConflictProbeOutcome.Indeterminate, "not-configured");

    internal ConductorAdvanceResult? TryEscalatePreLandingMergeConflict(
        Goal goal, ConductorAutonomyPolicy policy, GateReadyCandidateProjectionResult.Excluded exclusion,
        Func<string, bool> alreadyEscalatedAt, out string? revisionFingerprint)
    {
        revisionFingerprint = null;
        if (exclusion.Reason != GateReadyCandidateExclusionReason.MergeConflict || exclusion.ConflictPaths.Count == 0)
            return null;
        try
        {
            if (GoalLifecycle.ResolveState(goal, GetFacts(goal)) != GoalLifecycleState.Verified) return null;
            var probe = PreLandingMergeConflictProbe(goal);
            if (probe.Outcome == AdditiveConflictProbeOutcome.Indeterminate)
                Console.WriteLine($"PRE_LANDING_CONFLICT_PROBE goal={goal.Id.Value[..8]} result=indeterminate reason={probe.Reason}");
            if (probe.Outcome != AdditiveConflictProbeOutcome.Unresolvable ||
                !ConductorGitRevisionReader.TryNormalize(probe.BranchRevision, out var branch) ||
                !ConductorGitRevisionReader.TryNormalize(probe.MainRevision, out var main))
                return null;
            var fingerprint = new GateReadyCandidateRevisionPair(branch, main).Fingerprint;
            if (alreadyEscalatedAt(fingerprint)) return null;
            var result = CreateLandingRebaseOutcome(goal, goal.Id.Value[..8], policy, "pre-landing", true,
                new GoalWorktreeRebaseResult(GoalWorktreeRebaseStatus.Conflict, GoalWorktrees.BranchName(goal.Id),
                    probe.Reason, exclusion.ConflictPaths.ToArray(), "workspace rebase"), out _);
            if (result is not null) revisionFingerprint = fingerprint;
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PRE_LANDING_CONFLICT_PROBE goal={goal.Id.Value[..8]} result=indeterminate reason={ex.Message}");
            return null;
        }
    }

    private sealed record VerifyingFindingTrigger(
        TaskSpec TriggeringTask,
        string Finding,
        IReadOnlyList<string> SuppressedFindings,
        TaskSpec? TargetTask,
        bool RequiresCommittedTarget = false,
        IReadOnlyList<ReviewFinding>? DeveloperOwnedFindings = null);

    internal const string CandidateDeclineCohortAttributionFailure = "cohort-attribution-failure";
    internal const string CandidateDeclineVerifiedTransitionRequiresOperator = "verified-transition-requires-operator";
    internal const string CandidateDeclineLifecycleNotVerified = "lifecycle-not-verified";
    internal const string CandidateDeclineVerificationIncomplete = "verification-incomplete";
    internal const string CandidateDeclineHoldActive = "hold-active";

    internal ConductorParallelAcceptanceCandidate? TryBuildParallelAcceptanceCandidate(
        Goal goal,
        ConductorAutonomyPolicy policy,
        int slotIndex,
        out string? reason)
    {
        if (HasRoutableRecordedCohortAttributionFailure(goal))
        {
            reason = CandidateDeclineCohortAttributionFailure;
            return null;
        }

        if (policy.GetTransitionDecision(GoalLifecycleState.Verified) == ConductorTransitionDecision.Escalate)
        {
            reason = CandidateDeclineVerifiedTransitionRequiresOperator;
            return null;
        }

        if (GoalLifecycle.ResolveState(goal, GetFacts(goal)) is not (GoalLifecycleState.Verified or GoalLifecycleState.Verifying))
        {
            reason = CandidateDeclineLifecycleNotVerified;
            return null;
        }

        if (!AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal))
        {
            reason = CandidateDeclineVerificationIncomplete;
            return null;
        }

        if (HasActiveApparatusHold(goal, out _) || HasActiveOwnerReviewHold(goal, out _, out _))
        {
            reason = CandidateDeclineHoldActive;
            return null;
        }

        reason = null;
        var acceptanceHeads = _resolveAcceptanceHeads(goal);

        if (!_parallelAcceptanceAttemptCoordinator.HasLiveAttempt(goal.Id.Value) &&
            TryGetActiveEvidenceMutationLease(goal) is { } lease)
        {
            throw new EvidenceMutationLeaseUnavailableException(FormatEvidenceMutationLeaseHeld(lease));
        }

        var slotCount = GetAcceptanceSlotCount(goal);
        if (slotIndex < 0 || slotIndex >= slotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotIndex),
                slotIndex,
                $"Acceptance slot index must be 0 through {slotCount - 1} for goal {goal.Id.Value[..8]}.");
        }

        return ConductorParallelAcceptanceCandidate.Create(
            goal,
            slotIndex,
            _getLandingFileScopes(goal),
            acceptanceHeads.BranchHeadSha,
            acceptanceHeads.MainHeadSha);
    }

    internal GateReadyCandidateProjectionResult ProjectGateReadyCandidate(
        Goal goal,
        ConductorAutonomyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(policy);

        if (HasActiveOwnerReviewHold(goal, out _, out _))
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.OwnerReviewHold);

        if (HasActiveApparatusHold(goal, out _))
        {
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.ApparatusHold);
        }

        GoalLifecycleState lifecycleState;
        try
        {
            lifecycleState = GoalLifecycle.ResolveState(goal, GetFacts(goal));
        }
        catch
        {
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.LifecycleNotReady);
        }

        bool gateSatisfied;
        try
        {
            gateSatisfied = _isVerificationGateSatisfied(goal);
        }
        catch
        {
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.GateNotReady);
        }

        ChangeRiskTier? changeRiskTier;
        ConductorTransitionDecision? autoPromotionDisposition;
        try
        {
            changeRiskTier = _classifyChangeRisk(goal);
            autoPromotionDisposition = changeRiskTier.HasValue
                ? policy.GetTransitionDecision(GoalLifecycleState.Merged, changeRiskTier.Value)
                : null;
        }
        catch
        {
            changeRiskTier = null;
            autoPromotionDisposition = null;
        }

        var input = new GateReadyCandidateInput(
            goal.Id,
            lifecycleState,
            gateSatisfied,
            changeRiskTier,
            autoPromotionDisposition);
        return _gateReadyCandidateProjector?.Project(input) ??
            ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.RevisionUnknown);
    }

    private static GateReadyCandidateProjectionResult.Excluded ExcludedGateReadyCandidate(
        GateReadyCandidateExclusionReason reason) => new(reason);

    private void RecoverCohortLandingEffects(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IGoalLifecycleEventWriter eventWriter,
        CohortAcceptanceStore store)
    {
        foreach (var recovery in store.RecoverPreparedLandings(workspace.ExecutionDirectory, workspace.IntegrationBranch))
        {
            var goalsById = kernel.Goals.ToDictionary(goal => goal.Id);
            var goals = recovery.Receipt.Identity.Members
                .Select(member => goalsById.TryGetValue(member.GoalId, out var goal) ? goal : null)
                .ToArray();
            if (goals.Any(goal => goal is null))
            {
                continue;
            }

            var resolvedGoals = goals.Cast<Goal>().ToArray(); var evidenceMutationLeases = new Stack<IDisposable>();
            try
            {
                foreach (var goal in resolvedGoals.OrderBy(goal => goal.Id.Value, StringComparer.Ordinal))
                {
                    var lease = _tryAcquireEvidenceMutationLease(goal, "conductor:cohort-recovery");
                    if (lease is null)
                    {
                        break;
                    }
                    evidenceMutationLeases.Push(lease);
                }
                if (evidenceMutationLeases.Count != resolvedGoals.Length)
                {
                    continue;
                }

                var changedFiles = recovery.Receipt.Identity.Members
                    .SelectMany(member => member.LandingPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(resolvedGoals, goalId => recovery.Receipt.Identity.Members.Single(member => member.GoalId == goalId).CandidateRevision, kernel, $"cohort-receipt:{recovery.Receipt.ReceiptId}", _integrationBranch, workspace.ExecutionDirectory) is { } evidenceDiagnostic) { Console.WriteLine($"COHORT_RECOVERY_HELD cohort={recovery.Receipt.Identity.Value} detail={evidenceDiagnostic}"); continue; }
                foreach (var goal in resolvedGoals)
                {
                    GoalOperationJournal.Completed(
                        workspace.ExecutionDirectory,
                        goal,
                        "conductor:land",
                        $"Recovered shared cohort receipt {recovery.Receipt.ReceiptId} after main advanced.");
                    eventWriter.AppendGoalLanded(
                        goal.Id,
                        $"cohort/{recovery.Receipt.Identity.Value}",
                        GoalWorktrees.BranchName(goal.Id), LandingAdmissionReceipt.Recovered(
                            recovery.Receipt.Identity.Members.Single(member => member.GoalId == goal.Id).CandidateRevision));
                    StateEffectProposalApplier.ApplyLandedProposals(
                        kernel,
                        goal,
                        workspace,
                        changedFiles,
                        Console.WriteLine);
                    var landingResult = new LandingResult(
                        goal.Id.Value,
                        goal.Id.Value[..8],
                        new LandingDecision.Promote(),
                        $"cohort/{recovery.Receipt.Identity.Value}",
                        MainAdvanced: true,
                        "Recovered exact tested cohort landing after main advanced.",
                        recovery.CombinedCommitRevision,
                        changedFiles);
                    _pendingRecoveredLandingReceipts.Add((
                        new ConductorLandingReceipt(
                            goal.Id.Value,
                            changedFiles,
                            recovery.CombinedCommitRevision),
                        recovery.Receipt.Identity.Value,
                        recovery.Receipt.ReceiptId));
                    _afterSuccessfulLanding(goal, landingResult);
                }
            }
            finally
            {
                while (evidenceMutationLeases.TryPop(out var lease))
                {
                    lease.Dispose();
                }
            }
        }
    }

    internal IReadOnlySet<string> ReadSuppressedCohortPairs() =>
        _cohortAcceptanceStore?.ReadSuppressedPairs() ?? new HashSet<string>(StringComparer.Ordinal);

    internal void ResetCohortFairness(GoalId goalId) =>
        _cohortAcceptanceStore?.ResetOvertake(goalId);

    internal bool TryGetCohortGateHold(GoalId goalId, out string detail)
    {
        SweepCompletedCohortGateRuns();
        foreach (var pair in _cohortGateRuns)
        {
            if (!pair.Value.MemberGoalIds.Contains(goalId.Value))
            {
                continue;
            }

            detail = FormatCohortGateInFlightDetail(pair.Value, _utcNow());
            return true;
        }

        detail = string.Empty;
        return false;
    }

    internal void RecordMergeTrainAdmissionFairness(ConductorMergeTrainSelection selection)
    {
        if (_cohortAcceptanceStore is null) return;
        foreach (var member in selection.Members)
        {
            _cohortAcceptanceStore.ResetOvertake(member.GoalId);
        }
    }

    internal ConductorParallelAcceptanceRunResult RunPreReviewFocusedEvidence(
        ConductorParallelAcceptanceCandidate candidate,
        string request,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken) =>
        ConductorParallelAcceptanceRunResult.Focused(
            candidate,
            _runFocusedEvidence(candidate.Goal, request, stableSlotLease, cancellationToken));

    internal ConductorParallelAcceptanceRunResult? RunParallelLandingAcceptancePreSlot(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy)
    {
        if (_executionDirectory is null)
        {
            return null;
        }

        var effectiveCandidate = RefreshParallelAcceptanceCandidate(candidate);
        var branchHeadSha = effectiveCandidate.BranchHeadSha;
        if (string.IsNullOrWhiteSpace(branchHeadSha) ||
            !IsCommitReachableFromMain(_executionDirectory, branchHeadSha, _integrationBranch))
        {
            return RunParallelLandingSourceSizePreflight(effectiveCandidate);
        }

        var skippedAt = DateTimeOffset.UtcNow;
        var mergeCommitSha = RecoverMainMergeCommitForBranchTip(_executionDirectory, branchHeadSha, _integrationBranch);
        var detail = $"Acceptance skipped:skip-already-merged goalId={effectiveCandidate.Goal.Id.Value}; branchRef={GoalWorktrees.BranchName(effectiveCandidate.Goal.Id)}; mergeCommitSha={mergeCommitSha}; skippedAtUtc={skippedAt:O}.";
        GoalOperationJournal.AcceptanceSkippedAlreadyMerged(
            _executionDirectory,
            effectiveCandidate.Goal,
            "conductor:acceptance",
            branchHeadSha,
            effectiveCandidate.MainHeadSha,
            detail,
            skippedAt);
        return ConductorParallelAcceptanceRunResult.Early(
            effectiveCandidate,
            new ConductorAdvanceResult(
                effectiveCandidate.Goal.Id.Value,
                effectiveCandidate.GoalPrefix,
                policy.Name,
                new ConductorAdvanceOutcome.Done(GoalLifecycleState.Verified)),
            new ConductorParallelAcceptanceEarlyOutcome(
                "skip-already-merged",
                GoalLifecycleState.Verified,
                detail));
    }

    private ConductorParallelAcceptanceCandidate RefreshParallelAcceptanceCandidate(
        ConductorParallelAcceptanceCandidate candidate) =>
        ConductorParallelAcceptanceCandidate.Create(
            candidate.Goal,
            candidate.SlotIndex,
            candidate.ScopePaths,
            TryResolveAcceptanceBranchHead(candidate.Goal),
            _executionDirectory is null ? null : TryResolveGitHead(_executionDirectory));

    internal static bool IsAcceptanceAttemptCancelled(
        OrchestratorWorkspace workspace,
        GoalId goalId,
        Func<GoalId, GoalStatus?>? loadGoalStatus = null,
        Func<bool>? attemptInvalidationRecorded = null) =>
        GetAcceptanceAttemptCancellationDecision(
            workspace,
            goalId,
            loadGoalStatus,
            attemptInvalidationRecorded).ShouldCancel;

    internal static AcceptanceAttemptCancellationDecision GetAcceptanceAttemptCancellationDecision(
        OrchestratorWorkspace workspace,
        GoalId goalId,
        Func<GoalId, GoalStatus?>? loadGoalStatus = null,
        Func<bool>? attemptInvalidationRecorded = null)
    {
        var invalidated = false;
        if (attemptInvalidationRecorded is not null)
        {
            try
            {
                invalidated = attemptInvalidationRecorded();
            }
            catch
            {
                // Attempt metadata is supplementary stop evidence. Every operator stop also updates the
                // fail-closed goal record, so transient metadata I/O must not recreate spurious cancellation.
            }
        }

        var loadGoalStatusRecord = loadGoalStatus ?? new Func<GoalId, GoalStatus?>(id =>
            SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                .LoadGoalAsync(id)
                .GetAwaiter()
                .GetResult()
                ?.Status);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var observedStatus = loadGoalStatusRecord(goalId);
                return AcceptanceAttemptCancellation.Decide(
                    observedStatus,
                    goalRecordReadable: true,
                    attemptInvalidationRecorded: invalidated);
            }
            catch
            {
                // Retry immediately: this callback runs at every gate-check boundary and must stay cheap.
            }
        }

        return AcceptanceAttemptCancellation.Decide(
            observedStatus: null,
            goalRecordReadable: false,
            attemptInvalidationRecorded: invalidated);
    }

    internal ConductorAdvanceResult CompleteParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance,
        out bool evidenceMutationLeaseHeld)
    {
        evidenceMutationLeaseHeld = false;
        using var evidenceMutationLease = _tryAcquireEvidenceMutationLease(
            candidate.Goal,
            "conductor:parallel-land");
        if (evidenceMutationLease is null)
        {
            var held = ReplacementEvidenceMutationHeld(candidate.Goal, candidate.GoalPrefix, policy);
            evidenceMutationLeaseHeld = true;
            return held;
        }

        acceptance = NormalizeNamedFailedChecksForRetry(acceptance);
        if (!acceptance.Passed || acceptance.UnmetCriteria.Count > 0)
        {
            return CompleteLandingAfterAcceptance(candidate.Goal, candidate.GoalPrefix, policy, acceptance);
        }

        return CompleteParallelLandingAfterPreMergeRebase(candidate, policy, acceptance);
    }

    internal ConductorAdvanceResult EscalateParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        string reason, ConductorEscalationKind? kind = null) =>
        Escalate(candidate.Goal, candidate.GoalPrefix, policy, GoalLifecycleState.Verified, reason, kind);

    internal ConductorAdvanceResult EscalateParallelLandingAcceptance(
        Goal goal,
        ConductorAutonomyPolicy policy,
        string reason, ConductorEscalationKind? kind = null) =>
        Escalate(goal, goal.Id.Value[..8], policy, GoalLifecycleState.Verified, reason, kind);

    internal static AcceptanceVerificationSummary NormalizeNamedFailedChecksForRetry(AcceptanceVerificationSummary acceptance)
    {
        if (acceptance.Passed ||
            acceptance.RequiredUnmetCriteria.Count > 0 ||
            acceptance.FailedChecks is not { Count: > 0 } failedChecks)
        {
            return acceptance;
        }

        var output = string.Join(Environment.NewLine, failedChecks);
        var summary = $"Named failing acceptance checks: {string.Join(", ", failedChecks)}";
        var attributions = acceptance.CheckAttributions;
        var allEnvironmentalApparatus = attributions is { Count: > 0 } &&
            failedChecks.All(name => attributions.Any(attribution =>
                attribution.CheckName.Equals(name, StringComparison.Ordinal) &&
                attribution.Cause == AcceptanceFailureCause.EnvironmentalApparatus));
        var allInherited = allEnvironmentalApparatus && failedChecks.All(name => attributions!.Any(attribution =>
            attribution.CheckName.Equals(name, StringComparison.Ordinal) &&
            attribution.Origin == AcceptanceFailureOrigin.Inherited));
        var check = new AcceptanceCheckResult(
            "acceptance failed checks",
            false,
            1,
            string.IsNullOrWhiteSpace(acceptance.FailureDetail) ? output : acceptance.FailureDetail,
            ResultSummary: summary,
            FailureClassification: allEnvironmentalApparatus
                ? allInherited
                    ? AcceptanceFailureClassifications.InheritedBaselineApparatus
                    : AcceptanceFailureClassifications.GateEnvironmentInterference
                : null);
        return new AcceptanceVerificationSummary(
            false,
            [check],
            acceptance.FailureDetail,
            acceptance.FailedChecks,
            acceptance.BranchHeadSha,
            acceptance.MainHeadSha,
            acceptance.TestResultPaths,
            acceptance.CheckAttributions,
            acceptance.BaselineAttestation);
    }

    internal static AcceptanceVerificationSummary ClassifyInheritedBaselineApparatus(
        AcceptanceVerificationSummary acceptance)
    {
        if (acceptance.Passed ||
            acceptance.FailedChecks is not { Count: > 0 } failedChecks ||
            acceptance.CheckAttributions is not { Count: > 0 } attributions ||
            !failedChecks.All(name => attributions.Any(attribution =>
                attribution.CheckName.Equals(name, StringComparison.Ordinal) &&
                attribution.Origin == AcceptanceFailureOrigin.Inherited &&
                attribution.Cause == AcceptanceFailureCause.EnvironmentalApparatus)))
        {
            return acceptance;
        }

        var inheritedChecks = failedChecks.ToHashSet(StringComparer.Ordinal);
        var classifiedChecks = acceptance.UnmetCriteria
            .Select(check => !check.Advisory && inheritedChecks.Contains(check.Name)
                ? check with
                {
                    FailureClassification = AcceptanceFailureClassifications.InheritedBaselineApparatus
                }
                : check)
            .ToArray();
        return new AcceptanceVerificationSummary(
            acceptance.Passed,
            classifiedChecks,
            acceptance.FailureDetail,
            acceptance.FailedChecks,
            acceptance.BranchHeadSha,
            acceptance.MainHeadSha,
            acceptance.TestResultPaths,
            attributions,
            acceptance.BaselineAttestation);
    }

    internal static bool IsEnvironmentalApparatusAcceptanceRun(AcceptanceVerificationSummary acceptance)
    {
        if (acceptance.RequiredUnmetCriteria is not { Count: > 0 } requiredUnmetCriteria)
        {
            return false;
        }

        return requiredUnmetCriteria.All(check =>
            check.FailureClassification is
                AcceptanceFailureClassifications.GateEnvironmentInterference or
                AcceptanceFailureClassifications.AssemblyCleanupFailure or
                AcceptanceFailureClassifications.InheritedBaselineApparatus or AcceptanceFailureClassifications.SharedGateApparatusInvalidated ||
            acceptance.CheckAttributions is { Count: > 0 } attributions &&
            attributions.Any(attribution =>
                attribution.CheckName.Equals(check.Name, StringComparison.Ordinal) &&
                attribution.Cause == AcceptanceFailureCause.EnvironmentalApparatus));
    }

    private static bool IsSameApparatusFailurePair(
        AcceptanceFailureSummary? failure,
        (string? BranchHeadSha, string? MainHeadSha) current)
    {
        if (failure is not { IsEnvironmentalApparatus: true })
        {
            return false;
        }

        return ShaIsUnchangedOrUnknown(failure.BranchHeadSha, current.BranchHeadSha) &&
            ShaIsUnchangedOrUnknown(failure.MainHeadSha, current.MainHeadSha);
    }

    private bool HasActiveApparatusHold(
        Goal goal,
        out (string? BranchHeadSha, string? MainHeadSha) current)
    {
        current = (null, null);
        if (goal.LatestAcceptanceFailure is not { IsEnvironmentalApparatus: true })
        {
            return false;
        }

        current = _resolveAcceptanceHeads(goal);
        return IsSameApparatusFailurePair(goal.LatestAcceptanceFailure, current);
    }

    private static bool ShaIsUnchangedOrUnknown(string? recorded, string? current) =>
        string.IsNullOrWhiteSpace(recorded) ||
        string.IsNullOrWhiteSpace(current) ||
        recorded.Equals(current, StringComparison.OrdinalIgnoreCase);

    internal ConductorAdvanceResult ReplayParallelLandingEarlyOutcome(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorAdvanceResult earlyResult,
        ConductorParallelAcceptanceEarlyOutcome? earlyOutcome)
    {
        if (earlyOutcome is null)
        {
            return earlyResult;
        }

        return earlyOutcome.Kind switch
        {
            ConductorParallelAcceptanceEarlyOutcome.MissingBranchRetiredKind =>
                ReplayMissingBranchRetirement(candidate, policy, earlyOutcome),
            ConductorParallelAcceptanceEarlyOutcome.PreLandingEscalatedKind =>
                Escalate(candidate.Goal, candidate.GoalPrefix, policy, earlyOutcome.State, earlyOutcome.Detail),
            _ => earlyResult
        };
    }

    private ConductorAdvanceResult ReplayMissingBranchRetirement(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceEarlyOutcome earlyOutcome)
    {
        _recordMissingBranchRetirement(candidate.Goal, earlyOutcome.Detail);
        return MakeResult(
            candidate.Goal.Id.Value,
            candidate.GoalPrefix,
            policy,
            new ConductorAdvanceOutcome.Done(earlyOutcome.State));
    }
}
