namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DetachedLaunchBreakawayFallback
{
    internal const uint CreateBreakawayFromJob = 0x01000000;

    internal static bool TryGetRetryFlags(uint failedFlags, int nativeError, out uint retryFlags)
    {
        retryFlags = failedFlags & ~CreateBreakawayFromJob;
        return nativeError == 5 && (failedFlags & CreateBreakawayFromJob) != 0;
    }
}

internal readonly record struct DetachedProcessCreationOutcome(
    bool Created, IntPtr ProcessHandle, IntPtr ThreadHandle, int ProcessId, int NativeError);
