using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceDotnetTestBatchGroup<TCheck>(int Index, TCheck[] Checks, bool IsInfrastructurePartition);

internal static class AcceptanceDotnetTestBatchScheduler
{
    internal static bool CanOverlap<TCheck>(
        IReadOnlyList<TCheck> checks,
        bool slotLeaseAndBuildPhasePresent,
        int budget,
        Func<TCheck, bool> isMtpDotnetTest,
        Func<TCheck, bool> isInfrastructurePartition,
        Func<TCheck, bool> hasExclusiveResourceKeys)
    {
        if (!slotLeaseAndBuildPhasePresent || budget <= 1 || checks.Any(check => !isMtpDotnetTest(check)))
            return false;

        var partitions = checks.Select((check, index) => (check, index))
            .Where(item => isInfrastructurePartition(item.check)).ToArray();
        return partitions.Length > 0 && partitions.Length < checks.Count &&
               partitions[^1].index - partitions[0].index + 1 == partitions.Length &&
               checks.Where(check => !isInfrastructurePartition(check))
                   .All(check => !hasExclusiveResourceKeys(check));
    }

    internal static IReadOnlyList<AcceptanceDotnetTestBatchGroup<TCheck>> Group<TCheck>(
        IReadOnlyList<TCheck> checks,
        Func<TCheck, bool> isInfrastructurePartition)
    {
        var groups = new List<AcceptanceDotnetTestBatchGroup<TCheck>>();
        for (var index = 0; index < checks.Count;)
        {
            var isPartition = isInfrastructurePartition(checks[index]);
            var count = isPartition
                ? checks.Skip(index).TakeWhile(isInfrastructurePartition).Count()
                : 1;
            groups.Add(new(index, checks.Skip(index).Take(count).ToArray(), isPartition));
            index += count;
        }
        return groups;
    }

    internal static async Task<IReadOnlyList<TPart>> RunAsync<TCheck, TPart>(
        IReadOnlyList<AcceptanceDotnetTestBatchGroup<TCheck>> groups,
        int budget,
        Task buildCompleted,
        Func<bool> prebuildPassed,
        Func<bool> sharedApparatusInvalidated,
        Func<AcceptanceDotnetTestBatchGroup<TCheck>, SemaphoreSlim, CancellationToken, Task<TPart>> runPartitionGroup,
        Func<TCheck, CancellationToken, Task<(TPart Part, bool StopBatch)>> runSingleCheck,
        CancellationToken cancellationToken)
        where TPart : class
    {
        using var slots = new SemaphoreSlim(budget);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var failures = new ConcurrentQueue<Exception>();
        var parts = new TPart?[groups.Sum(group => group.Checks.Length)];

        async Task RunGroupAsync(AcceptanceDotnetTestBatchGroup<TCheck> group)
        {
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                if (sharedApparatusInvalidated())
                    return;
                if (group.IsInfrastructurePartition)
                {
                    parts[group.Index] = await runPartitionGroup(group, slots, stop.Token).ConfigureAwait(false);
                    return;
                }

                await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                try
                {
                    if (sharedApparatusInvalidated())
                        return;
                    var run = await runSingleCheck(group.Checks[0], stop.Token).ConfigureAwait(false);
                    parts[group.Index] = run.Part;
                    if (run.StopBatch)
                        await stop.CancelAsync().ConfigureAwait(false);
                }
                finally
                {
                    slots.Release();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped invocations have no verdict. Gate cancellation is propagated after draining.
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }

        // Preserve the first plan entry's build attribution and prevent a second consumer
        // from building in the same artifacts while the shared prebuild is still running.
        var owner = RunGroupAsync(groups[0]);
        await Task.WhenAny(buildCompleted, owner).ConfigureAwait(false);
        if (prebuildPassed() && !stop.IsCancellationRequested)
        {
            var started = new List<Task> { owner };
            foreach (var group in groups.Skip(1))
                started.Add(RunGroupAsync(group));
            await Task.WhenAll(started).ConfigureAwait(false);
        }
        else
        {
            // A failed or absent prebuild retains the serial failure path and its receipts.
            await owner.ConfigureAwait(false);
            foreach (var group in groups.Skip(1))
            {
                if (stop.IsCancellationRequested || sharedApparatusInvalidated())
                    break;
                await RunGroupAsync(group).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failures.TryPeek(out var failure))
            ExceptionDispatchInfo.Capture(failure).Throw();
        return parts.Where(part => part is not null).Select(part => part!).ToArray();
    }
}
