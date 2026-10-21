using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns identity and cleanup while a dispatch host is still behind its start gate.
internal static class DispatchHostLaunchOwnership
{
    internal static SpawnProcessIdentity? ReadIdentity(Process process, bool enrolled, Func<int, bool> tryKillOwnedProcess)
    {
        // A registry-free runner retains live job ownership; enrolled builds additionally require
        // durable identity and cannot use that legacy degradation.
        _ = WorkerProcessJobs.TryGetRegisteredIdentity(process.Id, out var identity);
        if (!enrolled || identity is not null)
            return identity;

        try
        {
            // Read from the held process handle, never by reopening an unbound PID.
            var observed = WorkerBuildRequestChannel.ReadIdentity(process);
            return new SpawnProcessIdentity(observed.Pid, observed.StartedAt, observed.ImagePath);
        }
        catch
        {
            TerminateUnreleased(process, tryKillOwnedProcess);
            throw;
        }
    }

    internal static void TerminateUnreleased(Process process, Func<int, bool> tryKillOwnedProcess)
    {
        try
        {
            if (!tryKillOwnedProcess(process.Id) && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The checkpoint failure is the actionable fault; process cleanup is best-effort.
        }
    }

    internal static void ReleaseStartGate(string startGatePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(startGatePath)!);
        File.WriteAllText(startGatePath, "go");
    }
}
