using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum OperatorGateStopResult { Stopped, NotCancellable, Unproven }

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    internal OperatorGateStopResult CancelForOperator(ConductorParallelAcceptanceAttempt attempt, string intentId)
    {
        if (attempt.ExecutionProtocol == "in-process" || attempt.HolderContractVersion != 1)
            return OperatorGateStopResult.NotCancellable;
        var binding = AcceptanceHolderBinding.For(attempt);
        try
        {
            var identity = attempt.OwnerProcessStartedAt is { } started && attempt.OwnerExecutablePath is { } image
                ? new SpawnProcessIdentity(attempt.OwnerProcessId, started, image) : null;
            if (identity is not null) HolderLease.ValidateRecordedHolder(binding, identity);
            if (!HolderLease.Cancel(binding, _generationIdentity, $"operator-gate-cancel:{intentId}", identity))
                return OperatorGateStopResult.Unproven;
        }
        catch (Exception)
        {
            return OperatorGateStopResult.Unproven;
        }
        using var writer = _tryAcquireAttemptWriterLease(Path.GetDirectoryName(attempt.MetadataPath)!, TimeSpan.Zero);
        if (writer is null) return OperatorGateStopResult.Unproven;
        lock (MetadataWriteGate)
        {
            var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
            WriteAttemptFile(current with
            {
                Outcome = current.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running
                    ? ConductorParallelAcceptanceAttemptOutcome.Cancelled : current.Outcome,
                CompletedAt = current.CompletedAt ?? _utcNow(),
                ReconciledAt = current.ReconciledAt ?? _utcNow(),
                Detail = $"operator-cancelled intent={intentId}"
            });
            if (!File.Exists(current.ExitCodePath)) File.WriteAllText(current.ExitCodePath, "1");
        }
        return OperatorGateStopResult.Stopped;
    }

    // Settled attempts disappear from the capacity view, but retain the crash-replay marker.
    internal bool HasOperatorCancelMarker(string goalId, string intentId)
    {
        var directory = Path.Combine(_rootDirectory, goalId);
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.attempt.json")
            .Any(path => ReadCanonicalAttempt(path, goalId).Detail?.EndsWith($"intent={intentId}", StringComparison.Ordinal) == true);
    }
}
