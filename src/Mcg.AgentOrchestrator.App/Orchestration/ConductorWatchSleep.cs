namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns interruptible waiting between conductor ticks and exit-artifact wake registration.
internal static class ConductorWatchSleep
{
    private static bool IsStopRequested(string stopFilePath) =>
        !string.IsNullOrEmpty(stopFilePath) && File.Exists(stopFilePath);

    internal static WatchSleepResult Wait(
        TimeSpan interval,
        string stopFilePath,
        IConductorWakeSignal? wakeSignal,
        IReadOnlyList<string> trackedExitCodePaths,
        IReadOnlyList<string>? runningAttemptExitCodePaths = null)
    {
        wakeSignal?.UpdateTrackedExitArtifacts(trackedExitCodePaths);
        if (wakeSignal is IConductorAttemptExitWakeSignal attemptWake) attemptWake.UpdateTrackedAttemptExitArtifacts(runningAttemptExitCodePaths ?? []);
        var remaining = interval;
        var poll = TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds);
        while (remaining > TimeSpan.Zero)
        {
            if (IsStopRequested(stopFilePath))
                return WatchSleepResult.StopRequested;
            var slice = remaining < poll ? remaining : poll;
            if (wakeSignal is not null)
            {
                if (wakeSignal.Wait(slice))
                    return WatchSleepResult.WakeSignaled;
            }
            else
            {
                Thread.Sleep(slice);
            }
            remaining -= slice;
        }

        return IsStopRequested(stopFilePath)
            ? WatchSleepResult.StopRequested
            : WatchSleepResult.FallbackElapsed;
    }
}
