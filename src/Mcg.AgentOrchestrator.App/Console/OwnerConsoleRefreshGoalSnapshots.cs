using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleRefreshGoalSnapshots(IOrchestratorStateQueries state)
{
    private readonly object _sync = new();
    private Generation? _current;

    internal IDisposable BeginRefresh()
    {
        var generation = new Generation();
        lock (_sync) _current = generation;
        return new RefreshHandle(() =>
        {
            lock (_sync)
            {
                if (ReferenceEquals(_current, generation)) _current = null;
            }
        });
    }

    internal async Task<OwnerConsoleLoadedGoals> LoadAsync(IReadOnlyCollection<GoalId> ids,
        CancellationToken cancellationToken)
    {
        var requested = ids.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        if (requested.Count == 0) return new([], []);
        var ordered = requested.Order(StringComparer.Ordinal).Select(id => new GoalId(id)).ToArray();
        Batch? reserved = null;
        Batch[]? batches = null;
        lock (_sync)
        {
            if (_current is { } generation)
            {
                var missing = ordered.Where(id => !generation.Requested.Contains(id.Value)).ToArray();
                if (missing.Length > 0)
                {
                    reserved = new(missing);
                    generation.Batches.Add(reserved);
                    generation.Requested.UnionWith(reserved.Ids);
                }
                batches = generation.Batches.Where(batch => batch.Ids.Overlaps(requested)).ToArray();
            }
        }
        if (batches is null)
        {
            var kernel = await state.LoadGoalsAsync(ordered, cancellationToken).ConfigureAwait(false);
            return Select(kernel.Goals, kernel.HumanInputRequests, requested);
        }

        // Reserve before leaving the lock. Joiners share success, failure or cancellation,
        // and completion belongs to these batches even if a newer generation has begun.
        if (reserved is not null) _ = PopulateAsync(reserved, cancellationToken);
        var loaded = await Task.WhenAll(batches.Select(batch => batch.Completion.Task))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return Select(loaded.SelectMany(batch => batch.Goals), loaded.SelectMany(batch => batch.Requests), requested);
    }

    private async Task PopulateAsync(Batch batch, CancellationToken cancellationToken)
    {
        try
        {
            var kernel = await state.LoadGoalsAsync(batch.Query, cancellationToken).ConfigureAwait(false);
            batch.Completion.SetResult(Select(kernel.Goals, kernel.HumanInputRequests, batch.Ids));
        }
        catch (OperationCanceledException ex) { batch.Completion.SetCanceled(ex.CancellationToken); }
        catch (Exception ex) { batch.Completion.SetException(ex); }
    }

    private static OwnerConsoleLoadedGoals Select(IEnumerable<Goal> goals,
        IEnumerable<HumanInputRequest> requests, HashSet<string> ids) => new(
        goals.Where(goal => ids.Contains(goal.Id.Value)).DistinctBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal).ToArray(),
        requests.Where(request => ids.Contains(request.GoalId.Value)).ToArray());

    private sealed class Generation
    {
        internal readonly HashSet<string> Requested = new(StringComparer.Ordinal);
        internal readonly List<Batch> Batches = [];
    }

    private sealed class Batch(GoalId[] query)
    {
        internal readonly GoalId[] Query = query;
        internal readonly HashSet<string> Ids = query.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        internal readonly TaskCompletionSource<OwnerConsoleLoadedGoals> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RefreshHandle(Action close) : IDisposable
    {
        private Action? _close = close;
        public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke();
    }
}
