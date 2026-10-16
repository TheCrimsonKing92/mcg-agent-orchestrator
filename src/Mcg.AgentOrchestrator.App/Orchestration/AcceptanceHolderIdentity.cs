using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A PID is a lookup key. Only the recorded start time and image identify the holder.
internal static class AcceptanceHolderIdentity
{
    internal static bool IsProcessAbsent(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return false; // Any process at this PID, including a replacement, retains capacity.
        }
        catch (ArgumentException) { return true; } // GetProcessById: no such PID
        catch (Exception ex) when (ex is InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        { return false; }
    }

    internal static SpawnTrackedProcessStatus Classify(
        int processId, DateTimeOffset? startedAt, string? imagePath)
    {
        if (processId <= 0 || startedAt is null || string.IsNullOrWhiteSpace(imagePath))
            return SpawnTrackedProcessStatus.Unknown;

        var entry = new SpawnRegistryEntry(0, "acceptance-holder", processId, startedAt.Value,
            imagePath, startedAt.Value, null, null, null, null, null, SpawnRegistryLifecycle.Owned);
        var status = SpawnProcessIdentityReader.EvaluateTrackedProcess(entry, out var process, out _);
        process?.Dispose();
        return status;
    }

    internal static SpawnProcessIdentity? Capture(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return SpawnProcessIdentityReader.ReadForRegistration(process).Identity;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    internal static bool StopBeforeClaim(SpawnProcessIdentity identity)
    {
        var entry = new SpawnRegistryEntry(0, "acceptance-unclaimed-holder", identity.ProcessId,
            identity.StartedAt, identity.ImagePath, identity.StartedAt,
            null, null, null, null, null, SpawnRegistryLifecycle.Owned);
        var status = SpawnProcessIdentityReader.EvaluateTrackedProcess(entry, out var process, out _);
        using (process)
        {
            if (status == SpawnTrackedProcessStatus.DeadOrRecycled) return true;
            if (status != SpawnTrackedProcessStatus.LiveMatch || process is null) return false;
            try
            {
                // Caller holds the claim lock: no acceptance execution or descendants can start.
                process.Kill();
                return process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { return false; }
        }
    }
}
