using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    private readonly bool _usesDurableHolderLease;
    private readonly SpawnProcessIdentity _generationIdentity;
    private readonly Func<int, bool> _isHolderProcessAbsent;
    private readonly Func<DateTimeOffset?> _hostBootTime;
    private readonly AcceptanceHolderLease? _holderLease;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _holderHolds = new();
    private AcceptanceHolderLease HolderLease => _holderLease ?? new(utcNow: _utcNow,
        eventSink: line => _conductEventLogWriter?.Append("acceptance-holder", null, line));

    private bool ReservesHolderLease(ConductorParallelAcceptanceAttempt attempt)
    {
        var binding = AcceptanceHolderBinding.For(attempt);
        AcceptanceHolderObservation observation;
        try
        {
            if (attempt.OwnerProcessStartedAt is { } started && attempt.OwnerExecutablePath is { } image)
            {
                HolderLease.ValidateRecordedHolder(binding, new(attempt.OwnerProcessId, started, image));
                _ = HolderLease.ReconcileUnclaimed(binding, new(attempt.OwnerProcessId, started, image));
            }
            observation = HolderLease.Observe(binding);
            if (observation.CanAdopt) _ = HolderLease.Adopt(binding, _generationIdentity);
        }
        catch (Exception ex) when (AcceptanceHolderLeaseFault.From(ex, binding) is { } fault)
        {
            RecordHolderHold(attempt, fault);
            return true;
        }
        if (observation.ReservesCapacity)
        {
            if (observation.Fault is { } fault) RecordHolderHold(attempt, fault);
            else if (!observation.CanAdopt) RecordHolderHold(attempt, observation.Reason);
            return true;
        }
        if (attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running) return false;
        // A terminal holder artifact is insufficient until the attempt itself is terminalized.
        using var writer = _tryAcquireAttemptWriterLease(Path.GetDirectoryName(attempt.MetadataPath)!, TimeSpan.Zero);
        if (writer is null)
        {
            RecordHolderHold(attempt, "attempt-writer-lease-busy");
            return true;
        }
        lock (MetadataWriteGate)
        {
            var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
            if (current.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running &&
                !File.Exists(current.ResultPath))
                WriteAttemptFile(current with
                {
                    Outcome = ConductorParallelAcceptanceAttemptOutcome.ProcessDied,
                    TransientFailureCount = CountConsecutiveTransientFailures(current) + 1,
                    CompletedAt = _utcNow(), Detail = $"holder-tree-settled:{observation.Reason}"
                });
        }
        return false;
    }

    private bool ReservesLegacyHolder(ConductorParallelAcceptanceAttempt attempt)
    {
        if (!_usesDurableHolderLease)
        {
            // Injected holders obey the same identity fence: publication and reconciliation
            // cannot settle a live or unreadable holder. Fixtures signal holder exit explicitly.
            return _classifyHolder(attempt) != SpawnTrackedProcessStatus.DeadOrRecycled;
        }
        if (LegacyHolderSettlementReason(attempt) is null)
        {
            RecordHolderHold(attempt, "legacy-tree-settlement-unavailable");
            return true;
        }
        if (attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running &&
            LegacyAcceptanceHolderSettlement.HasTerminalReceipt(AcceptanceHolderBinding.For(attempt),
                attempt.OwnerProcessId, attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath, attempt.StartedAt))
            return false;
        // Holder settlement is independent of result publication. Terminalize under the
        // existing writer boundary before exposing capacity, preserving any published verdict.
        using var writer = _tryAcquireAttemptWriterLease(Path.GetDirectoryName(attempt.MetadataPath)!, TimeSpan.Zero);
        if (writer is null)
        {
            RecordHolderHold(attempt, "attempt-writer-lease-busy");
            return true;
        }
        lock (MetadataWriteGate)
        {
            var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
            var reason = LegacyHolderSettlementReason(current);
            if (reason is null) return true;
            if (current.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                if (File.Exists(current.ResultPath))
                {
                    // The regular result mapper owns the verdict. Admission waits for its
                    // terminal publication rather than replacing a valid result with failure.
                    return true;
                }
                WriteAttemptFile(current with
                {
                    Outcome = ConductorParallelAcceptanceAttemptOutcome.ProcessDied,
                    TransientFailureCount = CountConsecutiveTransientFailures(current) + 1,
                    CompletedAt = _utcNow(), Detail = "legacy-holder-settled"
                });
            }
            var binding = AcceptanceHolderBinding.For(current);
            if (LegacyAcceptanceHolderSettlement.RecordTerminal(binding, current.OwnerProcessId,
                current.OwnerProcessStartedAt, current.OwnerExecutablePath, current.StartedAt,
                _utcNow(), reason))
                _conductEventLogWriter?.Append("acceptance-holder", null,
                    $"ACCEPTANCE_HOLDER_RELEASE attempt={current.AttemptId} slot={binding.Slot} " +
                    $"holder={current.OwnerProcessId} holderStartedAt={current.OwnerProcessStartedAt:O} " +
                    $"holderImage={current.OwnerExecutablePath} receipt={LegacyAcceptanceHolderSettlement.TerminalReceiptPath(binding)} " +
                    $"reason={reason}");
        }
        return false;
    }

    private string? LegacyHolderSettlementReason(ConductorParallelAcceptanceAttempt attempt)
    {
        if (_hostBootTime() is { } boot && LegacyAcceptanceHolderSettlement.PredatesHostBoot(attempt, boot))
            return LegacyAcceptanceHolderSettlement.PredatesHostBootEvidence;

        if (LegacyAcceptanceHolderSettlement.HasPidAbsentTerminalReceipt(attempt))
            return LegacyAcceptanceHolderSettlement.IncompleteIdentityPidAbsentEvidence;

        if (LegacyAcceptanceHolderSettlement.IsAbsent(attempt.OwnerProcessId,
            attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath, () => _classifyHolder(attempt)))
            return "legacy-holder-absent-or-settled";

        // Owner ruling 2a386d93: only terminal, reconciled v0 parallel attempts can
        // settle an incomplete identity by PID absence. Any process at that PID holds.
        if (attempt.HolderContractVersion == 0 && attempt.ExecutionProtocol == "out-of-process" &&
            attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running && IsReconciled(attempt) &&
            (attempt.OwnerProcessStartedAt is null || string.IsNullOrWhiteSpace(attempt.OwnerExecutablePath)) &&
            attempt.OwnerProcessId > 0 && _isHolderProcessAbsent(attempt.OwnerProcessId))
            return LegacyAcceptanceHolderSettlement.IncompleteIdentityPidAbsentEvidence;

        return !(attempt.OwnerProcessId <= 0 && attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running) &&
            LegacyAcceptanceHolderSettlement.IsSettled(AcceptanceHolderBinding.For(attempt), attempt.OwnerProcessId,
                attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath, attempt.StartedAt,
                () => _classifyHolder(attempt), attempt.LastHeartbeatAt)
            ? "legacy-holder-absent-or-settled" : null;
    }

    private void RecordHolderHold(ConductorParallelAcceptanceAttempt attempt, AcceptanceHolderLeaseFault fault) =>
        RecordHolderHold(attempt, fault.Format());

    private void RecordHolderHold(ConductorParallelAcceptanceAttempt attempt, string reason)
    {
        if (_holderHolds.TryGetValue(attempt.AttemptId, out var previous) && previous == reason) return;
        _holderHolds[attempt.AttemptId] = reason;
        _conductEventLogWriter?.Append("acceptance-holder", null,
            $"ACCEPTANCE_HOLDER_HOLD attempt={attempt.AttemptId} slot=slot-{attempt.SlotIndex} " +
            $"holder={attempt.OwnerProcessId} holderStartedAt={attempt.OwnerProcessStartedAt:O} " +
            $"holderImage={attempt.OwnerExecutablePath} generation={_generationIdentity.ProcessId} reason={reason}");
    }

    internal void PrepareHolderDetach()
    {
        if (!_usesDurableHolderLease || !Directory.Exists(_rootDirectory)) return;
        foreach (var attempt in GetUnreconciledAttempts(Directory.EnumerateDirectories(_rootDirectory)
            .Select(Path.GetFileName).OfType<string>()))
        {
            if (!IsCapacityReservingAttempt(attempt)) continue;
            if (attempt.HolderContractVersion != 1)
            {
                // Legacy metadata and the global census retain the fence across conductor exit.
                // There is no safe containment transfer/cancel proof to fabricate here.
                RecordHolderHold(attempt, "legacy-detach-capacity-fenced");
                continue;
            }
            var binding = AcceptanceHolderBinding.For(attempt);
            try
            {
                if (attempt.OwnerProcessStartedAt is { } started && attempt.OwnerExecutablePath is { } image)
                    HolderLease.ValidateRecordedHolder(binding, new(attempt.OwnerProcessId, started, image));
                if (HolderLease.Read(binding).Generation != _generationIdentity)
                {
                    // Another live conductor still owns this holder. Its durable lease already fences admission.
                    if (!HolderLease.Adopt(binding, _generationIdentity)) continue;
                }
                if (HolderLease.Detach(binding, _generationIdentity)) continue;
                var unclaimedIdentity = attempt.OwnerProcessStartedAt is { } holderStarted && attempt.OwnerExecutablePath is { } holderImage
                    ? new SpawnProcessIdentity(attempt.OwnerProcessId, holderStarted, holderImage) : null;
                if (!HolderLease.Cancel(binding, _generationIdentity, "detach-not-provable", unclaimedIdentity))
                    throw new InvalidOperationException($"Acceptance detach/cancel settlement unproven: {attempt.AttemptId}.");
            }
            catch (Exception ex) when (AcceptanceHolderLeaseFault.From(ex, binding) is { } fault)
            {
                RecordHolderHold(attempt, fault);
                continue;
            }
            using var writer = _tryAcquireAttemptWriterLease(Path.GetDirectoryName(attempt.MetadataPath)!, TimeSpan.Zero);
            if (writer is null)
                throw new InvalidOperationException($"Acceptance cancel terminal publication blocked: {attempt.AttemptId}.");
            lock (MetadataWriteGate)
            {
                var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
                WriteAttemptFile(current with
                {
                    Outcome = ConductorParallelAcceptanceAttemptOutcome.Cancelled,
                    CompletedAt = _utcNow(), Detail = "holder-tree-cancelled-before-detach"
                });
                File.WriteAllText(current.ExitCodePath, "1");
            }
        }
    }
}
