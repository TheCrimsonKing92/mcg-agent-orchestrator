using System.Runtime.CompilerServices;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// One offer owner per gate coordinator. The batch consumes results only after its host prebuild.
internal sealed class RemoteLaneEarlyOffer(RemoteLaneCoordinator coordinator) : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<RemoteLaneCoordinator, RemoteLaneEarlyOffer> Owners = new();
    private readonly HashSet<string> _consulted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Offered> _adoptions = new(StringComparer.Ordinal);
    private readonly List<Task<RemoteLaneOutcome>> _startedTasks = [];
    private readonly object _sync = new();
    private bool _hasStarted;
    private CancellationTokenSource? _cancellation;

    internal sealed record Context(Func<string, bool> Offer, Action<Check, RemoteLaneExecutorEntry> Started,
        Func<Check, TimeSpan> Timeout, Func<Check, TimeSpan> Deadline, AcceptancePartitionVerdictCache? Cache, TimeProvider Time);
    internal sealed record Offered(Task<RemoteLaneOutcome>? Remote, AcceptanceCheckResult? Reused, TimeSpan ReuseElapsed);

    internal static RemoteLaneEarlyOffer For(RemoteLaneCoordinator coordinator) => Owners.GetValue(coordinator, value => new(value));
    internal Task OfferPassCompleted { get; private set; } = Task.CompletedTask;
    internal static IEnumerable<(Check Check, int Index)> Order(IReadOnlyList<Check> checks) => checks
        .Select((check, index) => (Check: check, Index: index))
        .OrderByDescending(shard => AcceptanceLaneDurationStore.ResolveSortSeconds(shard.Check)).ThenBy(shard => shard.Index);

    internal RemoteLaneEarlyOffer? Start(bool disabled, IReadOnlyList<Check> checks, Context context, CancellationToken token)
    {
        if (disabled || checks.Count == 0) return null;
        lock (_sync)
        {
            if (_hasStarted) return null;
            _hasStarted = true;
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            _cancellation = lifetime;
            OfferPassCompleted = Task.Run(async () =>
            {
                foreach (var shard in Order(checks))
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (await TryOfferAsync(shard.Check, context, lifetime.Token).ConfigureAwait(false) is not { } offered) continue;
                    _adoptions.Add(shard.Check.Name, offered);
                    if (offered.Remote is { } remote) _startedTasks.Add(remote);
                }
            }, CancellationToken.None);
            // A build failure can bypass adoption; keep faults observed while preserving them for the batch await.
            _ = OfferPassCompleted.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return this;
        }
    }

    // Called only after OfferPassCompleted: the background writer and batch reader never overlap.
    internal bool TryAdopt(string checkName, out Offered adopted) => _adoptions.Remove(checkName, out adopted!);

    internal async Task<Offered?> TryOfferAsync(Check check, Context context, CancellationToken token)
    {
        if (_consulted.Contains(check.Name) || !coordinator.IsEligible(check) || !context.Offer(check.Name)) return null;
        token.ThrowIfCancellationRequested();
        var entry = await coordinator.ClaimForGateAsync(check, token).ConfigureAwait(false);
        if (entry is null) return null;
        _consulted.Add(check.Name);
        var reuseStarted = context.Time.GetTimestamp();
        if (context.Cache?.TryReuse(check) is { } reused)
        {
            coordinator.Release(entry.Id, check.Name);
            return new(null, reused, context.Time.GetElapsedTime(reuseStarted));
        }
        context.Started(check, entry);
        return new(coordinator.RunRemoteAsync(check, entry, context.Timeout(check), context.Deadline(check), token), null, TimeSpan.Zero);
    }

    internal void Cancel() => _cancellation?.Cancel();

    public async ValueTask DisposeAsync()
    {
        Cancel();
        try { await OfferPassCompleted.ConfigureAwait(false); } catch (Exception) { /* The batch observes pass failures. */ }
        try { await Task.WhenAll(_startedTasks).ConfigureAwait(false); } catch (Exception) { /* Drain before coordinator disposal. */ }
        _adoptions.Clear();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
