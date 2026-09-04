using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Serialized: the fast-exit test deliberately starves the thread pool while repeatedly launching
// real child processes through the Windows owned-process path.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class OwnedProcessExitObservationTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Fact(Skip = "Requires Windows suspended-process handles.", SkipUnless = nameof(IsWindows))]
    public void WaitAndRead_FastExitUnderPoolSaturation_ReturnsKnownCode()
    {
        var release = new ManualResetEventSlim(false);
        ThreadPool.GetMinThreads(out var minWorkerThreads, out _);
        var blockedItems = Math.Max(minWorkerThreads, Environment.ProcessorCount) * 2 + 32;
        var blockedItemsCompleted = new CountdownEvent(blockedItems);
        var blockerState = (Release: release, Completed: blockedItemsCompleted);
        try
        {
            for (var i = 0; i < blockedItems; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static state =>
                    {
                        var (release, completed) = ((ManualResetEventSlim Release, CountdownEvent Completed))state!;
                        try
                        {
                            release.Wait();
                        }
                        finally
                        {
                            completed.Signal();
                        }
                    },
                    blockerState);
            }

            // Match the established GitCli saturation fixture so queued blockers occupy the
            // currently available workers before native-handle observation begins.
            Thread.Sleep(250);

            for (var iteration = 0; iteration < 200; iteration++)
            {
                using var launch = OwnedProcessGroup.StartSuspended(CreateFastExitStartInfo());

                Xunit.Assert.True(launch.HasOwnedExitObservation);
                Xunit.Assert.Null(launch.TryReadOwnedExitCode());
                launch.Resume();
                Xunit.Assert.True(
                    launch.WaitForOwnedExit(TimeSpan.FromSeconds(30)),
                    $"Owned process did not exit in iteration {iteration}.");
                Xunit.Assert.Equal(7, launch.TryReadOwnedExitCode());
            }
        }
        finally
        {
            release.Set();
            var blockersCompleted = blockedItemsCompleted.Wait(TimeSpan.FromSeconds(30));
            if (blockersCompleted)
            {
                blockedItemsCompleted.Dispose();
                release.Dispose();
            }

            Xunit.Assert.True(blockersCompleted, "Thread-pool saturation blockers did not complete after release.");
        }
    }

    [Xunit.Fact(Skip = "Requires Windows suspended-process handles.", SkipUnless = nameof(IsWindows))]
    public async Task WaitAsync_CanceledToken_LeavesOwnedHandleAvailable()
    {
        using var launch = OwnedProcessGroup.StartSuspended(CreateFastExitStartInfo());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => launch.WaitForOwnedExitAsync(cancellation.Token));

        Xunit.Assert.True(launch.HasOwnedExitObservation);
        Xunit.Assert.Null(launch.TryReadOwnedExitCode());
        launch.Resume();
        Xunit.Assert.True(launch.WaitForOwnedExit(TimeSpan.FromSeconds(30)));
        Xunit.Assert.Equal(7, launch.TryReadOwnedExitCode());
    }

    private static ProcessStartInfo CreateFastExitStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /c exit 7",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return startInfo;
    }
}
