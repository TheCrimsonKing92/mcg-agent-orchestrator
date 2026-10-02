using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static bool CanOverlapNonPartitionDotnetTests(
        IReadOnlyList<AcceptanceManifestCheck> checks,
        int? slotIndex,
        DotnetBuildEnvironmentLease? lease,
        DotnetTestBuildPhase? buildPhase,
        int budget)
    {
        if (slotIndex is null || lease is null || buildPhase is null || budget <= 1 ||
            checks.Any(check => !check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) ||
                                !UsesMicrosoftTestingPlatform(check)))
            return false;

        var partitions = checks.Select((check, index) => (check, index))
            .Where(item => TryGetInfrastructurePartitionId(item.check, out _, out _)).ToArray();
        return partitions.Length > 0 && partitions.Length < checks.Count &&
               partitions[^1].index - partitions[0].index + 1 == partitions.Length &&
               checks.Where(check => !TryGetInfrastructurePartitionId(check, out _, out _))
                   .All(check => check.ExclusiveResourceKeys.Count == 0);
    }

    private async Task<CheckBatchResult> RunOverlappedDotnetTestBatchAsync(
        IReadOnlyList<AcceptanceManifestCheck> checks,
        AcceptancePartitionVerdictCache? cacheContext,
        string worktreePath,
        GoalId? goalId,
        int slotIndex,
        DotnetBuildEnvironmentLease lease,
        DotnetTestBuildPhase buildPhase,
        int budget,
        CancellationToken cancellationToken,
        bool continueAfterFailure)
    {
        using var slots = new SemaphoreSlim(budget);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var failures = new ConcurrentQueue<Exception>();
        var parts = new CheckBatchResult?[checks.Count];
        var groups = new List<(int Index, AcceptanceManifestCheck[] Checks)>();
        for (var index = 0; index < checks.Count;)
        {
            var count = TryGetInfrastructurePartitionId(checks[index], out _, out _)
                ? checks.Skip(index).TakeWhile(check => TryGetInfrastructurePartitionId(check, out _, out _)).Count()
                : 1;
            groups.Add((index, checks.Skip(index).Take(count).ToArray()));
            index += count;
        }

        async Task RunGroupAsync((int Index, AcceptanceManifestCheck[] Checks) group)
        {
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                if (cacheContext?.SharedApparatusInvalidation is not null)
                    return;
                if (TryGetInfrastructurePartitionId(group.Checks[0], out _, out _))
                {
                    parts[group.Index] = await RunInfrastructureShardBatchAsync(group.Checks,
                        cacheContext, worktreePath, goalId, slotIndex, lease, buildPhase, budget,
                        cancellationToken, slots, stop.Token).ConfigureAwait(false);
                    return;
                }

                await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                try
                {
                    if (cacheContext?.SharedApparatusInvalidation is not null)
                        return;
                    var run = await RunCheckWithPartitionVerdictCacheAsync(group.Checks[0],
                        cacheContext, worktreePath, goalId, slotIndex, lease, buildPhase,
                        stop.Token).ConfigureAwait(false);
                    parts[group.Index] = new CheckBatchResult([run.Result], run.Retried);
                    if (!run.Result.Passed && !continueAfterFailure &&
                        ShouldStopAfterFailedCheck(cacheContext, group.Checks[0]))
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
        await Task.WhenAny(buildPhase.BuildCompleted.Task, owner).ConfigureAwait(false);
        if (buildPhase.Run?.Result.Passed == true && !stop.IsCancellationRequested)
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
                if (stop.IsCancellationRequested || cacheContext?.SharedApparatusInvalidation is not null)
                    break;
                await RunGroupAsync(group).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failures.TryPeek(out var failure))
            ExceptionDispatchInfo.Capture(failure).Throw();
        var results = parts.Where(part => part is not null).SelectMany(part => part!.Results).ToArray();
        return new CheckBatchResult(cacheContext?.ApplySharedApparatusInvalidation(results) ?? results,
            parts.Any(part => part?.Retried == true));
    }
}
