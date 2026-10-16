internal static class BoundedThreadPoolSaturationApparatus
{
    internal static readonly TimeSpan PhaseWaitTimeout = TimeSpan.FromSeconds(30);

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

            if (!entered.Wait(PhaseWaitTimeout))
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
            if (completed.CurrentCount != 0 && !completed.Wait(PhaseWaitTimeout))
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

    // Grows this process's worker pool to the legacy saturation scale before saturation is bounded, so the
    // warm-history arm reproduces prior pool growth inside the isolated child instead of depending on
    // whatever history the shared test runner happens to carry.
    internal static WarmPoolHistoryEvidence EstablishWarmPoolHistory(
        int? maxWorkerCap = null,
        TimeSpan? growthTimeout = null)
    {
        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            throw new InvalidOperationException("failureKind=warm-history-on-pool-thread");
        }

        var warmWorkerCount = WarmWorkerCount();
        ThreadPool.GetMinThreads(out var originalMinWorkers, out var originalMinIo);
        ThreadPool.GetMaxThreads(out var originalMaxWorkers, out var originalMaxIo);
        var threadCountBeforeWarm = ThreadPool.ThreadCount;
        var minSet = false;
        var maxSet = false;
        int warmThreadCountPeak;
        using var release = new ManualResetEventSlim(false);
        using var entered = new CountdownEvent(warmWorkerCount);
        using var completed = new CountdownEvent(0);

        try
        {
            // Set the minimum first so lowering the maximum to the optional cap cannot be refused.
            minSet = ThreadPool.SetMinThreads(
                maxWorkerCap is int cap ? Math.Min(warmWorkerCount, cap) : warmWorkerCount, originalMinIo);
            if (!minSet)
            {
                throw new InvalidOperationException(
                    $"failureKind=warm-set-min-failed warmWorkerCount={warmWorkerCount}");
            }

            if (maxWorkerCap is int workerCap)
            {
                maxSet = ThreadPool.SetMaxThreads(workerCap, originalMaxIo);
                if (!maxSet)
                {
                    throw new InvalidOperationException(
                        $"failureKind=warm-set-max-failed maxWorkerCap={workerCap}");
                }
            }

            completed.Reset(warmWorkerCount);
            for (var index = 0; index < warmWorkerCount; index++)
            {
                QueueBlocker(entered, release, completed, signalsEntry: true);
            }

            var growthStarted = System.Diagnostics.Stopwatch.StartNew();
            while ((warmThreadCountPeak = ThreadPool.ThreadCount) < warmWorkerCount)
            {
                if (growthStarted.Elapsed >= (growthTimeout ?? PhaseWaitTimeout))
                {
                    throw new TimeoutException(
                        $"failureKind=warm-growth-not-observed warmWorkerCount={warmWorkerCount} " +
                        $"threadCount={warmThreadCountPeak} enteredBlockers={warmWorkerCount - entered.CurrentCount} " +
                        $"pendingWorkItems={ThreadPool.PendingWorkItemCount}");
                }

                Thread.Sleep(50);
            }
        }
        finally
        {
            release.Set();
            try
            {
                if (completed.CurrentCount != 0 && !completed.Wait(PhaseWaitTimeout))
                {
                    throw new TimeoutException(
                        $"failureKind=warm-cleanup-timeout remainingCallbacks={completed.CurrentCount} " +
                        $"pendingWorkItems={ThreadPool.PendingWorkItemCount}");
                }
            }
            finally
            {
                ThreadPool.GetMinThreads(out var currentMinWorkers, out _);
                if (originalMaxWorkers >= currentMinWorkers)
                {
                    RestoreMaximum();
                    RestoreMinimum();
                }
                else
                {
                    RestoreMinimum();
                    RestoreMaximum();
                }
            }
        }

        return new WarmPoolHistoryEvidence(warmWorkerCount, threadCountBeforeWarm, warmThreadCountPeak);

        void RestoreMinimum()
        {
            if (minSet && !ThreadPool.SetMinThreads(originalMinWorkers, originalMinIo))
                throw new InvalidOperationException("failureKind=warm-restore-min-failed");
        }

        void RestoreMaximum()
        {
            if (maxSet && !ThreadPool.SetMaxThreads(originalMaxWorkers, originalMaxIo))
                throw new InvalidOperationException("failureKind=warm-restore-max-failed");
        }
    }

    internal static int WarmWorkerCount()
    {
        ThreadPool.GetMinThreads(out var originalMinWorkers, out _);
        // Same scale as the shared saturation helper's blocker formula, so the grown history matches the
        // magnitude that produced the original warm-pool failure rather than an arbitrary count.
        return Math.Max(originalMinWorkers, Environment.ProcessorCount) * 2 + 32;
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

internal sealed record WarmPoolHistoryEvidence(
    int WarmWorkerCount,
    int ThreadCountBeforeWarm,
    int WarmThreadCountPeak);

internal sealed record BoundedSaturationEvidence(
    int WorkerBound,
    int BarrierCount,
    int ThreadCountBefore,
    int ThreadCountAfterBarrier,
    int AvailableWorkersAfterBarrier);
