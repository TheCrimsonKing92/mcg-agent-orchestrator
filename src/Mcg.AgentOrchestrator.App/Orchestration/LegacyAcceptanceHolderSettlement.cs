using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Operator whole-tree attestations and automatic holder-terminal observations use separate
// artifacts and transitions. A terminal observation does not claim legacy tree containment.
internal sealed record LegacyAcceptanceHolderSettlementReceipt(
    AcceptanceHolderBinding Binding, int HolderProcessId, DateTimeOffset? HolderStartedAt,
    string? HolderImage, DateTimeOffset AttemptStartedAt, DateTimeOffset ConfirmedAt,
    string Evidence, string Transition = "operator-confirmed-tree-exit",
    DateTimeOffset? HolderObservedAt = null);

internal static class LegacyAcceptanceHolderSettlement
{
    internal const string IncompleteIdentityPidAbsentEvidence =
        "legacy-terminal-reconciled-incomplete-identity-pid-absent";

    // Upgrade admission follows the recorded full identity. Incomplete or uninspectable
    // identities still require the existing operator proof; a matching live PID never settles.
    internal static bool IsAbsent(int processId, DateTimeOffset? startedAt, string? image,
        Func<SpawnTrackedProcessStatus> classify) =>
        processId > 0 && startedAt is not null && !string.IsNullOrWhiteSpace(image) &&
        classify() == SpawnTrackedProcessStatus.DeadOrRecycled;

    internal static string TerminalReceiptPath(AcceptanceHolderBinding binding) =>
        binding.MetadataPath + ".legacy-holder-terminal.json";

    // Called under the attempt writer boundary, after terminal metadata publication and
    // before admission can reuse the slot. This is an identity observation, not tree proof.
    internal static bool RecordTerminal(AcceptanceHolderBinding binding, int processId,
        DateTimeOffset? startedAt, string? image, DateTimeOffset attemptStartedAt,
        DateTimeOffset now, string reason)
    {
        if (HasTerminalReceipt(binding, processId, startedAt, image, attemptStartedAt)) return false;
        var path = TerminalReceiptPath(binding);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new LegacyAcceptanceHolderSettlementReceipt(
                binding, processId, startedAt, image, attemptStartedAt, now, reason, "holder-terminal")));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

    // A release observed under the writer boundary stays settled if Windows later reuses
    // the PID. Only the exact owner-ruled evidence and bound attempt qualify.
    internal static bool HasPidAbsentTerminalReceipt(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.HolderContractVersion == 0 && attempt.ExecutionProtocol == "out-of-process" &&
        attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running &&
        (attempt.ReconciledAt.HasValue || attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Reconciled) &&
        (attempt.OwnerProcessStartedAt is null || string.IsNullOrWhiteSpace(attempt.OwnerExecutablePath)) &&
        attempt.OwnerProcessId > 0 &&
        HasTerminalReceipt(AcceptanceHolderBinding.For(attempt), attempt.OwnerProcessId,
            attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath, attempt.StartedAt,
            IncompleteIdentityPidAbsentEvidence);

    internal static bool HasTerminalReceipt(AcceptanceHolderBinding binding, int processId,
        DateTimeOffset? startedAt, string? image, DateTimeOffset attemptStartedAt,
        string? requiredEvidence = null)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<LegacyAcceptanceHolderSettlementReceipt>(
                File.ReadAllText(TerminalReceiptPath(binding)));
            return receipt is not null && receipt.Binding == binding && receipt.HolderProcessId == processId &&
                receipt.HolderStartedAt == startedAt && receipt.HolderImage == image &&
                receipt.AttemptStartedAt == attemptStartedAt && receipt.ConfirmedAt >= attemptStartedAt &&
                receipt.Transition == "holder-terminal" && !string.IsNullOrWhiteSpace(receipt.Evidence) &&
                (requiredEvidence is null || receipt.Evidence == requiredEvidence);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return false; }
    }

    internal static string ReceiptPath(AcceptanceHolderBinding binding) =>
        binding.MetadataPath + ".legacy-holder-settled.json";

    internal static bool IsSettled(AcceptanceHolderBinding binding, int processId,
        DateTimeOffset? startedAt, string? image, DateTimeOffset attemptStartedAt,
        Func<SpawnTrackedProcessStatus> classify, DateTimeOffset? holderObservedAt = null,
        Func<int, DateTimeOffset?>? readProcessStartedAt = null)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<LegacyAcceptanceHolderSettlementReceipt>(
                File.ReadAllText(ReceiptPath(binding)));
            if (receipt is null || receipt.Binding != binding || receipt.HolderProcessId != processId ||
                receipt.HolderStartedAt != startedAt || receipt.HolderImage != image ||
                receipt.AttemptStartedAt != attemptStartedAt || receipt.ConfirmedAt < attemptStartedAt ||
                receipt.Transition != "operator-confirmed-tree-exit" || string.IsNullOrWhiteSpace(receipt.Evidence))
                return false;
            var status = classify();
            if (status == SpawnTrackedProcessStatus.DeadOrRecycled) return true;
            // Callers allow this only for a terminal, unresolved pre-upgrade launch. The
            // operator's whole-tree proof also attests that no launcher remains pending.
            if (processId == 0 && status == SpawnTrackedProcessStatus.Unknown &&
                startedAt is null && string.IsNullOrWhiteSpace(image)) return true;
            // Some pre-upgrade metadata lacks start/image. Keep requiring whole-tree proof,
            // but independently distinguish the original root from a recycled PID. Attempt
            // creation predates launch and cannot prove reuse; a post-claim heartbeat can.
            if (status != SpawnTrackedProcessStatus.Unknown || processId <= 0 ||
                startedAt is not null && !string.IsNullOrWhiteSpace(image)) return false;
            var currentStart = (readProcessStartedAt ?? ReadProcessStartedAt)(processId);
            if (currentStart is null) return true;
            if (startedAt is not null) return currentStart != startedAt;
            // Freeze the observation in the attestation: verdict/reconciliation writes may
            // advance metadata timestamps after settlement. Without an earlier observation,
            // confirmation itself proves root absence then (the operator rechecks it).
            var observed = receipt.HolderObservedAt ?? receipt.ConfirmedAt;
            return observed >= attemptStartedAt && observed <= receipt.ConfirmedAt &&
                (receipt.HolderObservedAt is null || holderObservedAt is { } metadataObservation &&
                    observed <= metadataObservation) && currentStart > observed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static DateTimeOffset? ReadProcessStartedAt(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (ArgumentException) { return null; } // GetProcessById: no such PID
    }
}
