using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    private static ConductorParallelAcceptanceAttemptDecision? TryReusePassedReconciledAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        string dispatchKind,
        string? focusedEvidenceRequest,
        ConductorFocusedEvidenceRequestContext? requestContext)
    {
        if (!string.Equals(dispatchKind, GateDispatchKind, StringComparison.Ordinal) ||
            focusedEvidenceRequest is not null || requestContext is not null ||
            !string.Equals(
                string.IsNullOrWhiteSpace(attempt.Kind) ? GateDispatchKind : attempt.Kind,
                GateDispatchKind,
                StringComparison.Ordinal) ||
            attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed ||
            attempt.ReconciledAt is null || attempt.SupersededBy is not null ||
            string.IsNullOrWhiteSpace(attempt.BranchHeadSha) ||
            string.IsNullOrWhiteSpace(attempt.MainHeadSha) ||
            string.IsNullOrWhiteSpace(candidate.BranchHeadSha) ||
            string.IsNullOrWhiteSpace(candidate.MainHeadSha) ||
            !MatchesCandidate(attempt, candidate, dispatchKind, focusedEvidenceRequest, requestContext) ||
            candidate.Goal.GetOutstandingCriterionEvidenceObligations(attempt.BranchHeadSha)
                .Any(obligation =>
                    obligation.Owner is not (CriterionEvidenceOwner.Operator or CriterionEvidenceOwner.Worker) ||
                    string.Equals(obligation.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal)))
        {
            return null;
        }

        var effectiveCandidate = ConductorParallelAcceptanceCandidate.Create(
            candidate.Goal,
            attempt.SlotIndex,
            attempt.ScopePaths ?? candidate.ScopePaths,
            attempt.BranchHeadSha,
            attempt.MainHeadSha);
        var run = ConductorParallelAcceptanceRunResult.Accepted(
            effectiveCandidate,
            new AcceptanceVerificationSummary(
                true,
                [],
                BranchHeadSha: attempt.BranchHeadSha,
                MainHeadSha: attempt.MainHeadSha,
                TestResultPaths: attempt.TestResultPaths));
        return ConductorParallelAcceptanceAttemptDecision.Completed(attempt, run);
    }
}
