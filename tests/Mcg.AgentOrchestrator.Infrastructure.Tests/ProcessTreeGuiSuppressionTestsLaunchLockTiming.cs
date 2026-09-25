using System.Collections.Concurrent;
using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsLaunchLockTiming
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Fact(Skip = "Requires Windows suspended-process handles.", SkipUnless = nameof(IsWindows))]
    public void ParallelOwnedProcessLaunchesRecordEverySample()
    {
        const int threadCount = 6;
        const int launchesPerThread = 4;
        const int expectedLaunches = threadCount * launchesPerThread;
        var processBefore = LaunchLockTelemetry.Process.Snapshot().Launches;
        using var capture = LaunchLockTelemetry.BeginCapture();
        using var startGate = new Barrier(threadCount);
        var failures = new ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, threadCount).Select(_ => new Thread(() =>
        {
            try
            {
                if (!startGate.SignalAndWait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("Launch threads did not reach the start gate.");
                }

                for (var i = 0; i < launchesPerThread; i++)
                {
                    using var launch = OwnedProcessGroup.StartSuspended(new ProcessStartInfo("cmd.exe", "/d /c exit 0")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    launch.Resume();
                    Xunit.Assert.True(launch.WaitForOwnedExit(TimeSpan.FromSeconds(30)), "Owned child did not exit.");
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        }) { IsBackground = true }).ToArray();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads)
        {
            Xunit.Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "Launch thread did not finish.");
        }
        Xunit.Assert.Empty(failures);

        var snapshot = capture.Snapshot();
        Xunit.Assert.Equal(expectedLaunches, snapshot.Launches);
        Xunit.Assert.Equal(expectedLaunches, snapshot.ByEntry[(int)LaunchLockEntryPoint.AcquireSuppressedChildSpawn]);
        Xunit.Assert.Equal(0, snapshot.ByEntry[(int)LaunchLockEntryPoint.AcquireConsoleForChildSpawn]);
        Xunit.Assert.Equal(0, snapshot.ByEntry[(int)LaunchLockEntryPoint.AcquireErrorModeForChildSpawn]);
        Xunit.Assert.Equal(0, snapshot.ByEntry[(int)LaunchLockEntryPoint.Start]);
        Xunit.Assert.True(snapshot.WaitTicksTotal >= 0);
        Xunit.Assert.True(snapshot.HoldTicksTotal >= 0);
        Xunit.Assert.Equal(expectedLaunches, capture.Samples.Count);
        foreach (var sample in capture.Samples)
        {
            Xunit.Assert.True(sample.WaitTicks >= 0);
            Xunit.Assert.True(sample.HoldTicks >= sample.CreateTicks);
            Xunit.Assert.True(sample.CreateTicks > 0);
        }
        Xunit.Assert.True(LaunchLockTelemetry.Process.Snapshot().Launches - processBefore >= expectedLaunches);
    }

    [Xunit.Fact(Skip = "Requires Windows suspended-process handles.", SkipUnless = nameof(IsWindows))]
    public void FailedOwnedProcessLaunchRecordsCreateDuration()
    {
        using var capture = LaunchLockTelemetry.BeginCapture();
        var missingExecutable = Path.Combine(Path.GetTempPath(), $"mcg-missing-{Guid.NewGuid():N}.exe");

        Xunit.Assert.Throws<OwnedProcessLaunchException>(() => OwnedProcessGroup.StartSuspended(
            new ProcessStartInfo(missingExecutable) { UseShellExecute = false }));

        var snapshot = capture.Snapshot();
        Xunit.Assert.Equal(1, snapshot.Launches);
        Xunit.Assert.Equal(1, snapshot.ByEntry[(int)LaunchLockEntryPoint.AcquireSuppressedChildSpawn]);
        var sample = Xunit.Assert.Single(capture.Samples);
        Xunit.Assert.True(sample.CreateTicks > 0);
        Xunit.Assert.True(sample.HoldTicks >= sample.CreateTicks);
    }
}
