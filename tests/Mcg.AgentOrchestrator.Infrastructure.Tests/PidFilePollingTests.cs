using System.Collections.Concurrent;
using Xunit;

// Parallel-safe: each test owns its files, handles and cancellation sources.
public sealed class PidFilePollingTests
{
    [Fact]
    public async Task HelperKeepsPollingThroughSharingViolationAndReturnsPidAfterRelease()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "descendant.pid");
        using var cancellation = new CancellationTokenSource();
        var ioException = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FileStream? holder = null;
        Task<int>? wait = null;
        try
        {
            File.WriteAllText(path, "4242");
            holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            wait = PidFilePolling.WaitForPidAsync(path, cancellation.Token, outcome =>
            {
                if (outcome == PidFileReadOutcome.IoException)
                    ioException.TrySetResult();
            });

            await TestHangGuard.WaitAsync(ioException.Task,
                "io-exception attempt while the pid file is held with FileShare.None");
            Assert.False(wait.IsCompleted);

            holder.Dispose();
            holder = null;
            Assert.Equal(4242, await TestHangGuard.WaitAsync(wait, "pid read after exclusive handle release"));
        }
        finally
        {
            holder?.Dispose();
            await StopPollingAsync(wait, cancellation);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HelperPollsThroughMissingAndUnparsableValuesAndHonoursCancellation()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "descendant.pid");
        using var cancellation = new CancellationTokenSource();
        var outcomes = new ConcurrentQueue<PidFileReadOutcome>();
        var missing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unparsable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledMissing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? wait = null;
        Task<int>? cancelledWait = null;
        try
        {
            wait = PidFilePolling.WaitForPidAsync(path, cancellation.Token, outcome =>
            {
                outcomes.Enqueue(outcome);
                if (outcome == PidFileReadOutcome.Missing)
                    missing.TrySetResult();
                if (outcome == PidFileReadOutcome.Unparsable)
                    unparsable.TrySetResult();
            });

            await TestHangGuard.WaitAsync(missing.Task, "missing pid file attempt");
            Assert.False(wait.IsCompleted);
            PublishPid(path, "not-a-pid");
            await TestHangGuard.WaitAsync(unparsable.Task, "unparsable pid file attempt");
            Assert.False(wait.IsCompleted);
            PublishPid(path, "4242");
            Assert.Equal(4242, await TestHangGuard.WaitAsync(wait, "pid read after positive value publication"));

            var observed = outcomes.ToArray();
            Assert.True(Array.IndexOf(observed, PidFileReadOutcome.Missing)
                < Array.IndexOf(observed, PidFileReadOutcome.Unparsable));
            Assert.Equal(PidFileReadOutcome.Ready, observed[^1]);
            Assert.Equal(1, observed.Count(outcome => outcome == PidFileReadOutcome.Ready));

            cancelledWait = PidFilePolling.WaitForPidAsync(
                Path.Combine(directory, "cancelled.pid"), cancellation.Token, outcome =>
                {
                    if (outcome == PidFileReadOutcome.Missing)
                        cancelledMissing.TrySetResult();
                });
            await TestHangGuard.WaitAsync(cancelledMissing.Task, "missing pid file attempt before cancellation");
            Assert.False(cancelledWait.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                TestHangGuard.WaitAsync(cancelledWait, "pid poll cancellation"));
        }
        finally
        {
            await StopPollingAsync(wait, cancellation);
            await StopPollingAsync(cancelledWait, cancellation);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void PublishPid(string path, string value)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, value);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async Task StopPollingAsync(Task<int>? wait, CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        if (wait is null)
            return;

        try
        {
            await TestHangGuard.WaitAsync(wait, "pid poll cleanup after cancellation");
        }
        catch (OperationCanceledException)
        {
        }
    }
}
