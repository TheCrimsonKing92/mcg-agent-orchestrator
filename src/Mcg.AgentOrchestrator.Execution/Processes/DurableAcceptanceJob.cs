using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A named, non-breakaway job belongs to one acceptance holder, independently of its conductor.
// The independent custodian keeps the name discoverable after conductor and holder death.
internal sealed class DurableAcceptanceJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    internal SpawnProcessIdentity CustodianIdentity { get; }
    private DurableAcceptanceJob(SafeFileHandle handle, SpawnProcessIdentity custodianIdentity)
    { _handle = handle; CustodianIdentity = custodianIdentity; }

    internal static DurableAcceptanceJob AttachCurrent(string name, string? settlementPath = null)
    {
        using var self = Process.GetCurrentProcess();
        return Attach(self, name, settlementPath);
    }

    internal static DurableAcceptanceJob Attach(Process holder, string name, string? settlementPath = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Durable acceptance containment requires Windows.");
        var handle = OwnedProcessGroup.WindowsJob.CreateKillOnCloseJob(allowBreakaway: false, name: name);
        Process? custodian = null;
        try
        {
            var identity = SpawnProcessIdentityReader.ReadForRegistration(holder).Identity
                ?? throw new InvalidOperationException("Acceptance holder identity is unavailable.");
            // Start before assignment: the custodian must not be a member of the job it owns.
            // Its final handle retains kill-on-close, so custody loss terminates that exact job.
            custodian = AcceptanceJobCustodian.Start(name, identity, settlementPath);
            var custodianIdentity = SpawnProcessIdentityReader.ReadForRegistration(custodian).Identity
                ?? throw new InvalidOperationException("Acceptance custodian identity is unavailable.");
            if (!OwnedProcessGroup.WindowsJob.AssignProcessToJobObject(handle, holder.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not contain acceptance holder.");
            custodian.StandardInput.WriteLine("attached");
            custodian.StandardInput.Close();
            custodian.Dispose();
            return new DurableAcceptanceJob(handle, custodianIdentity);
        }
        catch
        {
            if (custodian is not null)
            {
                try { if (!custodian.HasExited) { custodian.Kill(); custodian.WaitForExit(5000); } }
                finally { custodian.Dispose(); }
            }
            handle.Dispose();
            throw;
        }
    }

    internal static AcceptanceJobObservation Observe(
        string name, SpawnProcessIdentity holder, SpawnTrackedProcessStatus holderStatus, bool cancel,
        string? settlementPath = null)
    {
        if (!OperatingSystem.IsWindows()) return new(false, false, "platform-unavailable");
        using var handle = OpenJobObjectW(0x0004 | 0x0008, false, name); // QUERY | TERMINATE
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return error == 2 && holderStatus == SpawnTrackedProcessStatus.DeadOrRecycled &&
                HasSettlementReceipt(settlementPath, name, holder)
                ? new(false, true, "job-settlement-receipt")
                : new(false, false, $"job-open-error:{error}");
        }
        if (holderStatus == SpawnTrackedProcessStatus.Unknown)
            return new(false, false, "holder-identity-unavailable");
        if (holderStatus == SpawnTrackedProcessStatus.LiveMatch)
        {
            // Keep the exact process handle through membership validation and cancellation.
            var entry = new SpawnRegistryEntry(0, "acceptance-holder", holder.ProcessId,
                holder.StartedAt, holder.ImagePath, holder.StartedAt,
                null, null, null, null, null, SpawnRegistryLifecycle.Owned);
            var status = SpawnProcessIdentityReader.EvaluateTrackedProcess(entry, out var process, out _);
            using (process)
            {
                if (status != SpawnTrackedProcessStatus.LiveMatch || process is null ||
                    !IsProcessInJob(process.Handle, handle, out var inJob) || !inJob)
                    return new(false, false, "holder-job-mismatch");
                if (cancel && !OwnedProcessGroup.WindowsJob.TryTerminate(handle))
                    return new(true, false, "job-termination-failed");
            }
        }
        else if (cancel && !OwnedProcessGroup.WindowsJob.TryTerminate(handle))
            return new(false, false, "job-termination-failed");
        if (cancel && !OwnedProcessGroup.WaitForJobExit(handle, TimeSpan.FromSeconds(5)))
            return new(false, false, "job-settlement-unconfirmed");
        return OwnedProcessGroup.WindowsJob.TryGetActiveProcessCount(handle, out var active)
            ? new(holderStatus == SpawnTrackedProcessStatus.LiveMatch, active == 0, $"job-active:{active}")
            : new(false, false, "job-query-failed");
    }

    public void Dispose() => _handle.Dispose();

    private static bool HasSettlementReceipt(string? path, string name, SpawnProcessIdentity holder)
    {
        if (path is null) return false;
        try
        {
            var receipt = JsonSerializer.Deserialize<AcceptanceJobSettlement>(File.ReadAllText(path));
            return receipt is { Settled: true, Holder: not null } && receipt.JobName == name &&
                receipt.Holder.ProcessId == holder.ProcessId && receipt.Holder.StartedAt == holder.StartedAt &&
                string.Equals(receipt.Holder.ImagePath, holder.ImagePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle OpenJobObjectW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool inJob);
}

internal sealed record AcceptanceJobObservation(bool HolderMatches, bool Settled, string Detail);
internal sealed record AcceptanceJobSettlement(string JobName, SpawnProcessIdentity Holder, bool Settled);
