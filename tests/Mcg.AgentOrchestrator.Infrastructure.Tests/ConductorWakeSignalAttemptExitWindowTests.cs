using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorWakeSignalAttemptExitWindowTests
{
    [Xunit.Fact]
    public void MarkBeforeReleaseIsObservableAndConsumedOnce()
    {
        WithTrackedExit((signal, path) =>
        {
            var mark = typeof(FileSystemWatcherConductorWakeSignal).GetMethod(
                "TryMarkAttemptExitSignaled", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mark);
            Assert.True(Assert.IsType<bool>(mark.Invoke(signal, [path])));

            Assert.True(signal.Wait(TimeSpan.Zero));
            Assert.Equal(ConductorWakeReason.AttemptExit, signal.LastWakeReason);
            // test-design-discipline: allow-negative-wait - the sole exit path was already consumed.
            Assert.False(signal.Wait(TimeSpan.Zero));
        });
    }

    [Xunit.Fact]
    public void CallbackReleaseIsConsumedWithItsMark()
    {
        WithTrackedExit((signal, path) =>
        {
            var callback = typeof(FileSystemWatcherConductorWakeSignal).GetMethod(
                "SignalIfTrackedAttemptExit", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(callback);
            callback.Invoke(signal, [path]);

            Assert.True(signal.Wait(TimeSpan.Zero));
            Assert.Equal(ConductorWakeReason.AttemptExit, signal.LastWakeReason);
            // test-design-discipline: allow-negative-wait - callback and file represent one exit.
            Assert.False(signal.Wait(TimeSpan.Zero));
        });
    }

    [Xunit.Fact]
    public void PendingMarkSurvivesDispatchSignalDrain()
    {
        WithTrackedExit((signal, path) =>
        {
            var callback = typeof(FileSystemWatcherConductorWakeSignal).GetMethod(
                "SignalIfTrackedAttemptExit", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(callback);
            callback.Invoke(signal, [path]);
            signal.UpdateTrackedExitArtifacts([]);

            Assert.True(signal.Wait(TimeSpan.Zero));
            Assert.Equal(ConductorWakeReason.AttemptExit, signal.LastWakeReason);
            // test-design-discipline: allow-negative-wait - the drained release and file are one exit.
            Assert.False(signal.Wait(TimeSpan.Zero));
        });
    }

    [Xunit.Fact]
    public void ExistingFileWithoutCallbackIsConsumedOnce()
    {
        WithTrackedExit((signal, _) =>
        {
            Assert.True(signal.Wait(TimeSpan.Zero));
            Assert.Equal(ConductorWakeReason.AttemptExit, signal.LastWakeReason);
            // test-design-discipline: allow-negative-wait - the file is unchanged after consumption.
            Assert.False(signal.Wait(TimeSpan.Zero));
        });
    }

    private static void WithTrackedExit(Action<FileSystemWatcherConductorWakeSignal, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-attempt-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var signal = new FileSystemWatcherConductorWakeSignal(Path.Combine(root, "logs"), _ => { });
            var path = Path.Combine(root, "attempt", "tracked.exit.txt");
            signal.UpdateTrackedAttemptExitArtifacts([path]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "0");
            assert(signal, path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
