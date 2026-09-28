using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    internal IReadOnlyList<ConductorParallelAcceptanceAttempt> GetSupersedableGateAttempts(
        IEnumerable<string> goalIds) =>
        GetUnreconciledAttempts(goalIds)
            .Where(attempt => attempt.Kind == GateDispatchKind &&
                !attempt.ReconciledAt.HasValue &&
                attempt.SupersedingMainHeadSha is null)
            .ToArray();

    internal bool RequestSupersededMainStop(
        ConductorParallelAcceptanceAttempt attempt,
        string supersedingMainHeadSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supersedingMainHeadSha);
        lock (MetadataWriteGate)
        {
            var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
            if (current.AttemptId != attempt.AttemptId ||
                current.Kind != GateDispatchKind ||
                current.ReconciledAt.HasValue ||
                current.SupersedingMainHeadSha is not null)
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
        lock (MetadataWriteGate)
        {
            var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
            if (current.AttemptId != attempt.AttemptId || current.SupersedingMainHeadSha is null)
                throw new InvalidDataException("Superseded acceptance attempt changed before exit reconciliation.");

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
}
