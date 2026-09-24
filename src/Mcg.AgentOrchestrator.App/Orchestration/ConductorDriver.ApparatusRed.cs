using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly ApparatusRedGate? _apparatusRedGate;

    private sealed record AcceptanceRetryDisposition(
        IReadOnlyList<AcceptanceCheckResult> ActionableCriteria,
        IReadOnlyList<ExcludedAcceptanceFailure> ExcludedFailures);

    private sealed record ExcludedAcceptanceFailure(
        string Identity,
        AcceptanceRetryExclusionKind Kind,
        string? ReceiptPointer = null);

    private enum AcceptanceRetryExclusionKind
    {
        Inherited,
        UnconfirmedIntroduced
    }

    private static string FormatExcludedAcceptanceFailure(ExcludedAcceptanceFailure failure) =>
        failure.Kind == AcceptanceRetryExclusionKind.UnconfirmedIntroduced
            ? $"{failure.Identity} (candidate rerun passed; unconfirmed introduction; receipt: {failure.ReceiptPointer})"
            : $"{failure.Identity} (pre-existing/main-red)";

    private ConductorAdvanceResult? TryDisposeExcludedAcceptanceFailures(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance,
        ApparatusRedGateReading? apparatusRedReading,
        ref AcceptanceRetryDisposition retryDisposition,
        out bool attemptedAllFlakyDisposition)
    {
        var allUnconfirmed = retryDisposition.ExcludedFailures.Count > 0 &&
            retryDisposition.ExcludedFailures.All(failure =>
                failure.Kind == AcceptanceRetryExclusionKind.UnconfirmedIntroduced);
        attemptedAllFlakyDisposition = allUnconfirmed;
        if (allUnconfirmed &&
            TryDisposeApparatusRed(goal, goalPrefix, policy, acceptance, apparatusRedReading)
                is { } candidateRerunRegate)
        {
            return candidateRerunRegate;
        }

        if (allUnconfirmed)
        {
            // Without an apparatus disposition, fail closed through today's retry path.
            retryDisposition = new AcceptanceRetryDisposition(
                acceptance.RequiredUnmetCriteria, []);
            return null;
        }

        var observedHeads = _resolveAcceptanceHeads(goal);
        var branchHeadSha = acceptance.BranchHeadSha ?? observedHeads.BranchHeadSha;
        var mainHeadSha = acceptance.MainHeadSha ?? observedHeads.MainHeadSha;
        var failedChecks = acceptance.FailedChecks is { Count: > 0 }
            ? acceptance.FailedChecks
            : acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray();
        if (goal.Status == GoalStatus.AcceptanceFailed)
        {
            RestoreVerifiedAfterAcceptanceClassification(
                goal,
                "All acceptance failures classified as Inherited; restored Verified for existing hold routing.");
        }
        _recordAcceptanceFailure(
            goal,
            failedChecks,
            branchHeadSha,
            mainHeadSha,
            acceptance.CheckAttributions,
            acceptance.BaselineAttestation);
        var excludedSummary = string.Join(
            ", ",
            retryDisposition.ExcludedFailures.Select(FormatExcludedAcceptanceFailure));
        var reason =
            $"Acceptance gate failures are all outside this goal's attributable scope: {excludedSummary}. " +
            "The candidate remains held at Verified for operator/main-red routing; no worker was reopened.";
        RecordEscalation(goal, GoalLifecycleState.Verified, reason);
        return MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                GoalLifecycleState.Verified,
                reason,
                StableIdentity: BuildUnattributableAcceptanceIdentity(
                    branchHeadSha,
                    mainHeadSha,
                    retryDisposition.ExcludedFailures)));
    }

    /// <summary>
    /// Returns a disposition for an apparatus RED, or null when the RED is genuine and must take
    /// today's Developer-reopen path unchanged. A driver without a configured gate always returns
    /// null, so every pre-existing acceptance path is byte-identical.
    /// </summary>
    private ConductorAdvanceResult? TryDisposeApparatusRed(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance,
        ApparatusRedGateReading? reading)
    {
        if (_apparatusRedGate is null || reading is null)
        {
            return null;
        }

        return _apparatusRedGate.Classify(goal, reading) switch
        {
            ApparatusRedDisposition.Regate regate =>
                HoldApparatusRedRegate(goal, goalPrefix, policy, acceptance, reading, regate),
            ApparatusRedDisposition.BoundExhausted bound =>
                EscalateApparatusRedBound(goal, goalPrefix, policy, bound),
            ApparatusRedDisposition.Genuine genuine =>
                RecordApparatusRedGenuine(goal, acceptance, genuine)
        };
    }

    private ConductorAdvanceResult? RecordApparatusRedGenuine(
        Goal goal,
        AcceptanceVerificationSummary acceptance,
        ApparatusRedDisposition.Genuine genuine)
    {
        if (string.IsNullOrWhiteSpace(_executionDirectory))
        {
            return null;
        }

        var observedHeads = _resolveAcceptanceHeads(goal);
        GoalOperationJournal.AcceptanceApparatusGenuine(
            _executionDirectory,
            goal,
            acceptance.BranchHeadSha ?? observedHeads.BranchHeadSha,
            acceptance.MainHeadSha ?? observedHeads.MainHeadSha,
            genuine.Reason);

        return null;
    }

    /// <summary>
    /// Restores Verified for a re-gate on the next tick, exactly as the blocked-build-slot fault path
    /// does. The recorded acceptance failure is cleared first: leaving a stale failure for this
    /// candidate would suppress the next gate and park the goal forever, which is strictly worse than
    /// the paid retry this replaces.
    /// </summary>
    private ConductorAdvanceResult HoldApparatusRedRegate(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance,
        ApparatusRedGateReading reading,
        ApparatusRedDisposition.Regate regate)
    {
        RestoreVerifiedAfterAcceptanceClassification(
            goal,
            $"Acceptance RED classified as apparatus ({regate.EvidenceKind}); restored Verified for re-gate {regate.RegateOrdinal}/{regate.RegateCap}.");
        _apparatusRedGate!.RecordRegate(goal, regate);
        var observedHeads = _resolveAcceptanceHeads(goal);
        var branchHeadSha = acceptance.BranchHeadSha ?? observedHeads.BranchHeadSha;
        var mainHeadSha = acceptance.MainHeadSha ?? observedHeads.MainHeadSha;
        WriteApparatusRedRegateJournal(goal, branchHeadSha, mainHeadSha, regate, reading.FailedCheckNames);
        var reason =
            $"Acceptance RED classified as apparatus ({regate.EvidenceKind}) for candidate " +
            $"{FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)}: every failing test lies outside the " +
            $"candidate's changed paths ({string.Join(", ", regate.TestIdentities)}). Re-gating on the next " +
            $"conduct tick ({regate.RegateOrdinal}/{regate.RegateCap}); no worker was reopened.";
        return MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                GoalLifecycleState.Verified,
                reason,
                StableIdentity:
                    $"acceptance-apparatus-red:{branchHeadSha ?? "unknown"}:{mainHeadSha ?? "unknown"}:" +
                    regate.EvidenceKind));
    }

    private void RestoreVerifiedAfterAcceptanceClassification(Goal goal, string reason)
    {
        var kernel = _cohortKernel ?? _conductorTickKernel;
        if (kernel is not null && goal.Status == GoalStatus.AcceptanceFailed)
        {
            kernel.RestoreVerifiedForClassifiedAcceptanceFailure(goal.Id, reason);
            return;
        }

        _clearAcceptanceFailure(goal);
    }

    /// <summary>
    /// The bound exists so a permanently broken apparatus cannot re-gate forever. Exhausting it routes
    /// to the operator with a distinct reason — never back into a paid Developer round, which would
    /// reintroduce the defect this goal removes.
    /// </summary>
    private ConductorAdvanceResult EscalateApparatusRedBound(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        ApparatusRedDisposition.BoundExhausted bound) =>
        Escalate(
            goal,
            goalPrefix,
            policy,
            GoalLifecycleState.Verified,
            $"{ApparatusRedClassifier.BoundExhaustedToken}: this goal already re-gated " +
            $"{bound.RegateCount}/{bound.RegateCap} apparatus REDs without a green gate. The current RED is " +
            $"apparatus again ({bound.EvidenceKind}) on {string.Join(", ", bound.TestIdentities)}. " +
            "Repair the apparatus or confirm acceptance-retry; no worker was reopened.");

    private void WriteApparatusRedRegateJournal(
        Goal goal,
        string? branchHeadSha,
        string? mainHeadSha,
        ApparatusRedDisposition.Regate regate,
        IReadOnlyList<string> failedCheckNames)
    {
        if (string.IsNullOrWhiteSpace(_executionDirectory))
        {
            return;
        }

        try
        {
            GoalOperationJournal.AcceptanceApparatusRegated(
                _executionDirectory,
                goal,
                branchHeadSha,
                mainHeadSha,
                regate.EvidenceKind,
                regate.TestIdentities,
                failedCheckNames,
                regate.RegateOrdinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The classification record is observability; losing it cannot change the gate outcome.
        }
    }
}
