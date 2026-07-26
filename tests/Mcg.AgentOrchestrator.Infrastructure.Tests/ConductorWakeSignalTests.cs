using System.Diagnostics;
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

            File.WriteAllText(untracked, "0");

            Assert.False(wakeSignal.Wait(TimeSpan.FromMilliseconds(250)));
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
        }
        finally
        {
            TryDeleteDirectory(root);
        }
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
