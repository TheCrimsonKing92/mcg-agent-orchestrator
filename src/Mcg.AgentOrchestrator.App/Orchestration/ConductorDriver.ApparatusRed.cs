using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly ApparatusRedGate? _apparatusRedGate;

    private sealed record AcceptanceRetryDisposition(
        IReadOnlyList<AcceptanceCheckResult> ActionableCriteria,
        IReadOnlyList<ExcludedAcceptanceFailure> ExcludedFailures);

    private sealed record ExcludedAcceptanceFailure(
        string Identity,
        AcceptanceRetryExclusionKind Kind);

    private enum AcceptanceRetryExclusionKind
    {
        Inherited,
        UnconfirmedIntroduced
    }

    private static string FormatExcludedAcceptanceFailure(ExcludedAcceptanceFailure failure) =>
        failure.Kind == AcceptanceRetryExclusionKind.UnconfirmedIntroduced
            ? $"{failure.Identity} (candidate rerun passed; unconfirmed introduction)"
            : $"{failure.Identity} (pre-existing/main-red)";

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
        _clearAcceptanceFailure(goal);
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
