using System.ComponentModel;
using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class ProcessTreeGuiSuppression
{
    internal static bool TryLowerCurrentProcessToBelowNormal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var current = Process.GetCurrentProcess();
            if (current.PriorityClass is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal)
            {
                return true;
            }

            current.PriorityClass = ProcessPriorityClass.BelowNormal;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or
                                   PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
