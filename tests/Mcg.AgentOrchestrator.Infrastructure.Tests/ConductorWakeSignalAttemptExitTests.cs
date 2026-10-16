using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorWakeSignalAttemptExitTests
{
    [Xunit.Theory]
    [Xunit.InlineData(ConductorParallelAcceptanceAttemptCoordinator.PreReviewEvidenceDispatchKind)]
    [Xunit.InlineData(ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind)]
    public async Task RunningAttemptExitWakesWatchSleep(string kind)
    {
        var root = CreateTempDirectory();
        var stopPath = Path.Combine(root, "stop");
        try
        {
            var attempt = WriteAttempt(root, kind);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, isProcessAlive: _ => true);
            var paths = coordinator.GetRunningAttemptExitCodePaths([attempt.GoalId]);
            Assert.Equal(attempt.ExitCodePath, Assert.Single(paths));

            using var signal = new FileSystemWatcherConductorWakeSignal(Path.Combine(root, "logs"), _ => { });
            using var updateObserved = new ManualResetEventSlim();
            using var gatedSignal = new GateWakeSignal(signal, updateObserved);
            var sleeper = Task.Run(() => ConductorBatchLoop.SleepUntilNextTick(
                TimeSpan.FromSeconds(120), stopPath, gatedSignal, [], paths));
            try
            {
                Assert.True(updateObserved.Wait(TimeSpan.FromSeconds(10)));
                File.WriteAllText(attempt.ExitCodePath, "0");

                Assert.Equal(WatchSleepResult.WakeSignaled, await sleeper.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(ConductorWakeReason.AttemptExit, gatedSignal.LastWakeReason);
            }
            finally
            {
                File.WriteAllText(stopPath, "stop");
                await sleeper.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FinishedAttemptExitDoesNotWakeAgain()
    {
        var root = CreateTempDirectory();
        try
        {
            var attempt = WriteAttempt(root, ConductorParallelAcceptanceAttemptCoordinator.PreReviewEvidenceDispatchKind);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, isProcessAlive: _ => true);
            var paths = coordinator.GetRunningAttemptExitCodePaths([attempt.GoalId]);

            using var signal = new FileSystemWatcherConductorWakeSignal(Path.Combine(root, "logs"), _ => { });
            signal.UpdateTrackedExitArtifacts([]);
            signal.UpdateTrackedAttemptExitArtifacts(paths);
            File.WriteAllText(attempt.ExitCodePath, "0");
            Assert.True(signal.Wait(TimeSpan.Zero));

            Assert.Empty(coordinator.GetRunningAttemptExitCodePaths([attempt.GoalId]));
            signal.UpdateTrackedExitArtifacts([]);
            signal.UpdateTrackedAttemptExitArtifacts(paths);
            // test-design-discipline: allow-negative-wait - the only producer has already completed.
            Assert.False(signal.Wait(TimeSpan.Zero));

            signal.UpdateTrackedExitArtifacts([]);
            signal.UpdateTrackedAttemptExitArtifacts([]);
            // test-design-discipline: allow-negative-wait - stale attempts are not tracked.
            Assert.False(signal.Wait(TimeSpan.Zero));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ExitWrittenBetweenPathSnapshotAndSleepWakesAtEntry()
    {
        var root = CreateTempDirectory();
        try
        {
            var attempt = WriteAttempt(root, ConductorParallelAcceptanceAttemptCoordinator.PreReviewEvidenceDispatchKind);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, isProcessAlive: _ => true);
            var paths = coordinator.GetRunningAttemptExitCodePaths([attempt.GoalId]);
            File.WriteAllText(attempt.ExitCodePath, "0");

            using var signal = new FileSystemWatcherConductorWakeSignal(Path.Combine(root, "logs"), _ => { });
            Assert.Equal(WatchSleepResult.WakeSignaled, ConductorBatchLoop.SleepUntilNextTick(
                TimeSpan.FromSeconds(120), Path.Combine(root, "stop"), signal, [], paths));
            Assert.Equal(ConductorWakeReason.AttemptExit, signal.LastWakeReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void WakeReasonIdentifiesAttemptExit()
    {
        var root = CreateTempDirectory();
        try
        {
            using var signal = new FileSystemWatcherConductorWakeSignal(root, _ => { });
            var path = Path.Combine(root, "attempt.exit.txt");
            signal.UpdateTrackedExitArtifacts([]);
            signal.UpdateTrackedAttemptExitArtifacts([path]);
            File.WriteAllText(path, "0");
            Assert.True(signal.Wait(TimeSpan.Zero));

            Assert.Equal("WATCH_WAKE tick=7 reason=attempt-exit",
                ConductorBatchLoop.FormatWatchWake(7, Path.Combine(root, "stop"), signal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ConductorParallelAcceptanceAttempt WriteAttempt(string root, string kind)
    {
        const string goalId = "goal-1";
        const string attemptId = "attempt-1";
        var directory = Path.Combine(root, "attempts", goalId);
        Directory.CreateDirectory(directory);
        var prefix = Path.Combine(directory, attemptId);
        var now = DateTimeOffset.UtcNow;
        var attempt = new ConductorParallelAcceptanceAttempt(
            attemptId, goalId, "goal", 0, "branch", "main", now, now, 1234,
            ConductorParallelAcceptanceAttemptOutcome.Running,
            prefix + ".out.log", prefix + ".err.log", prefix + ".exit.txt",
            prefix + ".heartbeat.json", prefix + ".result.json", prefix + ".attempt.json",
            Kind: kind);
        File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt));
        return attempt;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-attempt-wake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class GateWakeSignal(
        FileSystemWatcherConductorWakeSignal inner,
        ManualResetEventSlim updateObserved) : IConductorWakeSignal, IConductorAttemptExitWakeSignal
    {
        public ConductorWakeReason LastWakeReason => inner.LastWakeReason;

        public void UpdateTrackedExitArtifacts(IReadOnlyCollection<string> paths) =>
            inner.UpdateTrackedExitArtifacts(paths);

        public void UpdateTrackedAttemptExitArtifacts(IReadOnlyCollection<string> paths)
        {
            inner.UpdateTrackedAttemptExitArtifacts(paths);
            updateObserved.Set();
        }

        public bool Wait(TimeSpan timeout) => inner.Wait(timeout);

        public void Dispose() { }
    }
}
