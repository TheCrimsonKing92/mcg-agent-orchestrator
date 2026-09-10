internal static class BoundedThreadPoolSaturationApparatus
{
    internal static void RunWithBoundedSaturatedThreadPool(Action<BoundedSaturationEvidence> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            throw new InvalidOperationException("failureKind=apparatus-on-pool-thread");
        }

        ThreadPool.GetMinThreads(out var originalMinWorkers, out var originalMinIo);
        ThreadPool.GetMaxThreads(out var originalMaxWorkers, out var originalMaxIo);
        var threadCountBefore = ThreadPool.ThreadCount;
        var workerBound = Math.Max(Environment.ProcessorCount, 1);
        var minSet = false;
        var maxSet = false;
        using var release = new ManualResetEventSlim(false);
        using var entered = new CountdownEvent(1);
        using var completed = new CountdownEvent(1);

        try
        {
            minSet = ThreadPool.SetMinThreads(workerBound, originalMinIo);
            if (!minSet)
            {
                throw new InvalidOperationException($"failureKind=set-min-failed workerBound={workerBound}");
            }

            maxSet = ThreadPool.SetMaxThreads(workerBound, originalMaxIo);
            if (!maxSet)
            {
                throw new InvalidOperationException($"failureKind=set-max-failed workerBound={workerBound}");
            }

            ThreadPool.GetAvailableThreads(out var barrierCount, out _);
            entered.Reset(barrierCount);
            completed.Reset(barrierCount + workerBound);

            for (var index = 0; index < barrierCount; index++)
            {
                QueueBlocker(entered, release, completed, signalsEntry: true);
            }

            for (var index = 0; index < workerBound; index++)
            {
                QueueBlocker(entered, release, completed, signalsEntry: false);
            }

            if (!entered.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException(
                    $"failureKind=barrier-timeout barrierCount={barrierCount} " +
                    $"threadCount={ThreadPool.ThreadCount} pendingWorkItems={ThreadPool.PendingWorkItemCount}");
            }

            ThreadPool.GetAvailableThreads(out var remainingWorkers, out _);
            if (remainingWorkers != 0)
            {
                throw new InvalidOperationException(
                    $"failureKind=not-saturated remainingWorkers={remainingWorkers} workerBound={workerBound}");
            }

            body(new BoundedSaturationEvidence(
                workerBound,
                barrierCount,
                threadCountBefore,
                ThreadPool.ThreadCount,
                remainingWorkers));
        }
        finally
        {
            release.Set();
            if (completed.CurrentCount != 0 && !completed.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException(
                    $"failureKind=cleanup-timeout remainingCallbacks={completed.CurrentCount} " +
                    $"pendingWorkItems={ThreadPool.PendingWorkItemCount}");
            }

            if (maxSet && !ThreadPool.SetMaxThreads(originalMaxWorkers, originalMaxIo))
            {
                throw new InvalidOperationException("failureKind=restore-max-failed");
            }

            if (minSet && !ThreadPool.SetMinThreads(originalMinWorkers, originalMinIo))
            {
                throw new InvalidOperationException("failureKind=restore-min-failed");
            }
        }
    }

    private static void QueueBlocker(
        CountdownEvent entered,
        ManualResetEventSlim release,
        CountdownEvent completed,
        bool signalsEntry)
    {
        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                var (entered, release, completed, signalsEntry) =
                    ((CountdownEvent, ManualResetEventSlim, CountdownEvent, bool))state!;
                try
                {
                    if (signalsEntry)
                    {
                        entered.Signal();
                    }

                    release.Wait();
                }
                finally
                {
                    completed.Signal();
                }
            },
            (entered, release, completed, signalsEntry));
    }
}

internal sealed record BoundedSaturationEvidence(
    int WorkerBound,
    int BarrierCount,
    int ThreadCountBefore,
    int ThreadCountAfterBarrier,
    int AvailableWorkersAfterBarrier);
