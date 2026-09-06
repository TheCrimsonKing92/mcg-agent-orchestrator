using Mcg.AgentOrchestrator.Infrastructure;

// Serialized: this test deliberately occupies the thread pool while the dedicated reader runs.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class PipeDrainThreadPoolSaturationTests
{
    [Xunit.Fact]
    public void PipeDrainReadsOnDedicatedThreadWhileAsyncReadIsHeld()
    {
        const string expected = "complete-pipe-output";
        using var asyncReadRelease = new ManualResetEventSlim(false);
        var asyncReader = new HeldAsyncTextReader(expected, asyncReadRelease);
        var drainReader = new HeldAsyncTextReader(expected, asyncReadRelease);
        var poolDependentRead = asyncReader.ReadToEndAsync();
        try
        {
            var drain = PipeDrain.Start(drainReader, "pipe-drain-dedicated-thread-test");

            Assert.True(
                drain.Join(Environment.TickCount64 + 2_000),
                PipeDrain.DescribeTimeout("test", 2_000, drain, null));
            Assert.Equal(expected, drain.Text);
            Assert.False(
                drainReader.ReadOccurredOnThreadPool,
                "PipeDrain read on a thread-pool worker instead of its dedicated thread.");
            Assert.False(poolDependentRead.IsCompleted, "Negative control completed before its explicit release.");
        }
        finally
        {
            asyncReadRelease.Set();
            Assert.Equal(expected, poolDependentRead.GetAwaiter().GetResult());
        }
    }

    internal static void RunWithSaturatedThreadPool(Action action)
    {
        var release = new ManualResetEventSlim(false);
        ThreadPool.GetMinThreads(out var minWorkerThreads, out _);
        var blockedItems = Math.Max(minWorkerThreads, Environment.ProcessorCount) * 2 + 32;
        var callbacksToObserve = Math.Max(minWorkerThreads, Environment.ProcessorCount);
        var callbacksStarted = new SemaphoreSlim(0, blockedItems);
        var callbacksCompleted = new CountdownEvent(blockedItems);
        try
        {
            for (var i = 0; i < blockedItems; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static state =>
                    {
                        var (releaseSignal, startedSignal, completionSignal) =
                            ((ManualResetEventSlim, SemaphoreSlim, CountdownEvent))state!;
                        try
                        {
                            startedSignal.Release();
                            releaseSignal.Wait();
                        }
                        finally
                        {
                            completionSignal.Signal();
                        }
                    },
                    (release, callbacksStarted, callbacksCompleted));
            }

            for (var i = 0; i < callbacksToObserve; i++)
            {
                if (!callbacksStarted.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException(
                        $"Thread-pool saturation callbacks did not start; " +
                        $"observedStarts={i}; expectedStarts={callbacksToObserve}; " +
                        $"poolPendingWorkItems={ThreadPool.PendingWorkItemCount}.");
                }
            }

            action();
        }
        finally
        {
            release.Set();
            if (!callbacksCompleted.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    $"Thread-pool saturation callbacks did not complete after release; " +
                    $"remainingCallbacks={callbacksCompleted.CurrentCount}; " +
                    $"poolPendingWorkItems={ThreadPool.PendingWorkItemCount}.");
            }

            callbacksStarted.Dispose();
            callbacksCompleted.Dispose();
            release.Dispose();
        }
    }

    private sealed class HeldAsyncTextReader(
        string text,
        ManualResetEventSlim release) : SynchronousOnlyTextReader(text)
    {
        public override Task<string> ReadToEndAsync() => Task.Run(
            () =>
            {
                release.Wait();
                return ReadToEnd();
            });
    }

    internal class SynchronousOnlyTextReader(string text) : TextReader
    {
        private int _position;

        internal bool ReadOccurredOnThreadPool { get; private set; }

        public override int Read(char[] buffer, int index, int count)
        {
            ReadOccurredOnThreadPool |= Thread.CurrentThread.IsThreadPoolThread;
            var remaining = text.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var copied = Math.Min(count, remaining);
            text.CopyTo(_position, buffer, index, copied);
            _position += copied;
            return copied;
        }
    }
}
