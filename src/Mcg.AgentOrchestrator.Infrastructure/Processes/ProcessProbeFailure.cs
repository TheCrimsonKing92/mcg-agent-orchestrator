using System.ComponentModel;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ProcessProbeFailure
{
    internal static bool IsNotLive(Exception exception) => exception is
        Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException;
}
