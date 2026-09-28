using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    internal IReadOnlyList<ConductorParallelAcceptanceAttempt> GetSupersedableGateAttempts(
        IEnumerable<string> goalIds) =>
        GetUnreconciledAttempts(goalIds)
            .Where(IsStoppableSupersededGateAttempt)
            .ToArray();

    private bool IsStoppableSupersededGateAttempt(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.Kind == GateDispatchKind &&
        attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running &&
        !attempt.ReconciledAt.HasValue &&
        !File.Exists(attempt.ResultPath) &&
        !File.Exists(attempt.ExitCodePath) &&
        _isProcessAlive(attempt.OwnerProcessId) &&
        attempt.SupersedingMainHeadSha is null;

    internal bool RequestSupersededMainStop(
        ConductorParallelAcceptanceAttempt attempt,
        string supersedingMainHeadSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supersedingMainHeadSha);
        lock (MetadataWriteGate)
        {
            var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
            if (current.AttemptId != attempt.AttemptId ||
                !IsStoppableSupersededGateAttempt(current))
                return false;

            WriteAttemptFile(current with { SupersedingMainHeadSha = supersedingMainHeadSha });
            return true;
        }
    }

    private ConductorParallelAcceptanceAttemptDecision? TryCompleteSupersededAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate)
    {
        if (!File.Exists(attempt.ExitCodePath) && _isProcessAlive(attempt.OwnerProcessId))
            return null;

        var run = ConductorParallelAcceptanceRunResult.Fault(
            candidate,
            new AcceptanceExecutionIdentityChangedException(
                "main advanced during acceptance", isChangedIdentity: true));
        ConductorParallelAcceptanceAttempt? unmarked = null;
        lock (MetadataWriteGate)
        {
            var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
            if (current.AttemptId != attempt.AttemptId)
                throw new InvalidDataException("Superseded acceptance attempt changed before exit reconciliation.");

            if (current.SupersedingMainHeadSha is null)
            {
                // The child may have published terminal metadata after the stop request.
                // Observe its real artifacts rather than inventing a stale outcome.
                unmarked = current;
            }
            else
            {
                var completed = current with
                {
                    Outcome = ConductorParallelAcceptanceAttemptOutcome.Faulted,
                    CompletedAt = current.CompletedAt ?? _utcNow(),
                    Detail = "identity-stale: main advanced during acceptance"
                };
                WriteAttemptFile(completed);
                return ConductorParallelAcceptanceAttemptDecision.Completed(completed, run);
            }
        }

        return TryCompleteRunningAttempt(unmarked, candidate);
    }
}
