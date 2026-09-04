using Mcg.AgentOrchestrator.Infrastructure;

// Serialized: this test deliberately occupies the thread pool while the dedicated reader runs.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class PipeDrainThreadPoolSaturationTests
{
    [Xunit.Fact(DisplayName = "PipeDrain_reads_to_end_while_pool_dependent_async_read_is_starved")]
    public void PipeDrainReadsToEndWhilePoolDependentAsyncReadIsStarved()
    {
        const string expected = "complete-pipe-output";
        Task<string>? poolDependentRead = null;
        try
        {
            RunWithSaturatedThreadPool(() =>
            {
                poolDependentRead = new SynchronousOnlyTextReader(expected).ReadToEndAsync();
                var drain = PipeDrain.Start(new SynchronousOnlyTextReader(expected), "pipe-drain-saturation-test");

                Assert.True(
                    drain.Join(Environment.TickCount64 + 2_000),
                    PipeDrain.DescribeTimeout("test", 2_000, drain, null));
                Assert.Equal(expected, drain.Text);
                Assert.False(poolDependentRead.IsCompleted, "Negative control unexpectedly found a free thread-pool worker.");
            });
        }
        finally
        {
            poolDependentRead?.GetAwaiter().GetResult();
        }
    }

    internal static void RunWithSaturatedThreadPool(Action action)
    {
        var release = new ManualResetEventSlim(false);
        ThreadPool.GetMinThreads(out var minWorkerThreads, out _);
        var blockedItems = Math.Max(minWorkerThreads, Environment.ProcessorCount) * 2 + 32;
        var callbacksCompleted = new CountdownEvent(blockedItems);
        try
        {
            for (var i = 0; i < blockedItems; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static state =>
                    {
                        var (releaseSignal, completionSignal) =
                            ((ManualResetEventSlim, CountdownEvent))state!;
                        try
                        {
                            releaseSignal.Wait();
                        }
                        finally
                        {
                            completionSignal.Signal();
                        }
                    },
                    (release, callbacksCompleted));
            }

            Thread.Sleep(250);
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

            callbacksCompleted.Dispose();
            release.Dispose();
        }
    }

    internal sealed class SynchronousOnlyTextReader(string text) : TextReader
    {
        private int _position;

        public override int Read(char[] buffer, int index, int count)
        {
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
