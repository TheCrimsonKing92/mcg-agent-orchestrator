using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorWatchProgressReporter
{
    internal static bool IsProcessAlive(int processId) => IsProcessAlive(processId, ReadHasExited);

    internal static bool IsProcessAlive(int processId, Func<int, bool> readHasExited)
    {
        try
        {
            return !readHasExited(processId);
        }
        catch (Exception exception) when (ProcessProbeFailure.IsNotLive(exception))
        {
            return false;
        }
    }

    private static bool ReadHasExited(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.HasExited;
    }
}
