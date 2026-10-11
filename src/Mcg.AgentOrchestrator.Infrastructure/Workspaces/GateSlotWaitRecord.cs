using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record GateSlotWaitRecord(
    string GateIdentity,
    IReadOnlyList<string> BusyLeaseIds,
    TimeSpan WaitDuration,
    string Outcome,
    int ReservedSlotCount,
    int? AcquiredSlot = null,
    string? ErrorType = null)
{
    internal const string EventName = "ACCEPTANCE_SLOT_PRIORITY_WAIT";
    internal const string Acquired = "acquired";
    internal const string Expired = "expired";
    internal const string Cancelled = "cancelled";
    internal const string GaveUp = "gave-up";

    internal string FormatDetails() =>
        $"busyLeases={(BusyLeaseIds.Count == 0 ? "none" : string.Join('|', BusyLeaseIds))} " +
        $"waitMs={WaitDuration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} " +
        $"reservedSlots={ReservedSlotCount} slot={AcquiredSlot?.ToString(CultureInfo.InvariantCulture) ?? "none"}" +
        (ErrorType is null ? "" : $" error={ErrorType}");

    internal void Emit(DotnetBuildStorageRoot? storageRoot)
    {
        Console.WriteLine($"{EventName} gate={GateIdentity} outcome={Outcome} {FormatDetails()} pid={Environment.ProcessId}");
        if (storageRoot is not null)
            DotnetBuildEnvironmentManager.TryAppendGateSlotWaitJournal(storageRoot, this);
    }
}
