using System.Diagnostics;
using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorWakeSignalTests
{
    [Xunit.Fact(DisplayName = "ConductorWakeSignal_wakes_for_tracked_exit_artifact_within_short_window")]
    public void ConductorWakeSignalWakesForTrackedExitArtifactWithinShortWindow()
    {
        var root = CreateTempDirectory("mcg-conductor-wake-tracked");
        try
        {
            var tracked = Path.Combine(root, "tracked.exit.txt");
            using var wakeSignal = new FileSystemWatcherConductorWakeSignal(root, _ => { });
            wakeSignal.UpdateTrackedExitArtifacts([tracked]);

            var clock = Stopwatch.StartNew();
            var writeTask = Task.Run(async () =>
            {
                await Task.Delay(100);
                File.WriteAllText(tracked, "0");
            });

            Assert.True(wakeSignal.Wait(TimeSpan.FromSeconds(2)));
            writeTask.GetAwaiter().GetResult();
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"tracked exit wake took {clock.Elapsed}.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorWakeSignal_ignores_untracked_exit_artifact")]
    public void ConductorWakeSignalIgnoresUntrackedExitArtifact()
    {
        var root = CreateTempDirectory("mcg-conductor-wake-untracked");
        try
        {
            var tracked = Path.Combine(root, "tracked.exit.txt");
            var untracked = Path.Combine(root, "untracked.exit.txt");
            using var wakeSignal = new FileSystemWatcherConductorWakeSignal(root, _ => { });
            wakeSignal.UpdateTrackedExitArtifacts([tracked]);

            var signalIfTracked = typeof(FileSystemWatcherConductorWakeSignal).GetMethod(
                "SignalIfTracked",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(signalIfTracked);

            signalIfTracked.Invoke(wakeSignal, [untracked]);
            AssertSignalDrained(wakeSignal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorWakeSignal_existing_tracked_exit_artifact_wakes_without_waiting_for_poll")]
    public void ConductorWakeSignalExistingTrackedExitArtifactWakesWithoutWaitingForPoll()
    {
        var root = CreateTempDirectory("mcg-conductor-wake-existing");
        try
        {
            var tracked = Path.Combine(root, "tracked.exit.txt");
            File.WriteAllText(tracked, "0");
            using var wakeSignal = new FileSystemWatcherConductorWakeSignal(root, _ => { });
            wakeSignal.UpdateTrackedExitArtifacts([tracked]);

            var clock = Stopwatch.StartNew();
            Assert.True(wakeSignal.Wait(TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds)));
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(250), $"existing exit wake took {clock.Elapsed}.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorWakeSignal_existing_operator_intent_wake_file_wakes_without_exit_tracking")]
    public void ConductorWakeSignalExistingOperatorIntentWakeFileWakesWithoutExitTracking()
    {
        var root = CreateTempDirectory("mcg-conductor-wake-intent");
        try
        {
            var wakePath = Path.Combine(root, $"queued{Mcg.AgentOrchestrator.Infrastructure.SqliteOperatorIntentStore.WakeFileSuffix}");
            File.WriteAllText(wakePath, "queued");
            using var wakeSignal = new FileSystemWatcherConductorWakeSignal(root, _ => { });
            wakeSignal.UpdateTrackedExitArtifacts([]);

            Assert.True(wakeSignal.Wait(TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds)));
            Assert.False(File.Exists(wakePath));
            AssertSignalDrained(wakeSignal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorWakeSignal_unavailable_watch_directory_degrades_to_polling")]
    public void ConductorWakeSignalUnavailableWatchDirectoryDegradesToPolling()
    {
        var root = CreateTempDirectory("mcg-conductor-wake-unavailable");
        try
        {
            var fileInsteadOfDirectory = Path.Combine(root, "not-a-directory");
            File.WriteAllText(fileInsteadOfDirectory, "occupied");
            var warnings = new List<string>();

            using var wakeSignal = new FileSystemWatcherConductorWakeSignal(
                fileInsteadOfDirectory,
                warnings.Add);

            wakeSignal.UpdateTrackedExitArtifacts([]);
            Assert.Contains(warnings, warning =>
                warning.Contains("continuing with timed polling", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorWakeSignal_late_watcher_callback_after_dispose_is_ignored")]
    public void ConductorWakeSignalLateWatcherCallbackAfterDisposeIsIgnored()
    {
        var root = CreateTempDirectory("mcg-conductor-wake-disposed");
        try
        {
            var wakeSignal = new FileSystemWatcherConductorWakeSignal(root, _ => { });
            var signal = typeof(FileSystemWatcherConductorWakeSignal).GetMethod(
                "Signal",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(signal);

            wakeSignal.Dispose();

            var exception = Record.Exception(() => signal.Invoke(wakeSignal, null));
            Assert.Null(exception);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void AssertSignalDrained(FileSystemWatcherConductorWakeSignal wakeSignal)
    {
        var signaled = typeof(FileSystemWatcherConductorWakeSignal).GetField(
            "_signaled",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var signal = typeof(FileSystemWatcherConductorWakeSignal).GetField(
            "_signal",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(signaled);
        Assert.NotNull(signal);
        Assert.Equal(0, Assert.IsType<int>(signaled.GetValue(wakeSignal)));
        Assert.Equal(0, Assert.IsType<SemaphoreSlim>(signal.GetValue(wakeSignal)).CurrentCount);
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
