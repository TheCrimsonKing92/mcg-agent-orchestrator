using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed class DashboardContinuationService : IDisposable
{
    internal const string StoreFileName = OrchestratorWorkspace.ContinuationStoreFileName;

    private static readonly JsonSerializerOptions StoreJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, ContinuationWatch> _watches = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _pollInterval;
    private readonly int _maxIterations;

    public DashboardContinuationService()
        : this(TimeSpan.FromSeconds(5), 120)
    {
    }

    public DashboardContinuationService(TimeSpan pollInterval, int maxIterations)
    {
        _pollInterval = pollInterval;
        _maxIterations = maxIterations;
    }

    public IReadOnlyList<DashboardContinuationStatusDto> GetStatuses()
    {
        lock (_gate)
        {
            return _watches.Values
                .OrderByDescending(watch => watch.StartedAt)
                .Select(watch => watch.ToDto())
                .ToList();
        }
    }

    public DashboardContinuationSummaryDto GetSummary()
    {
        var statuses = GetStatuses();
        DateTimeOffset? nextCheckAt = null;
        foreach (var status in statuses)
        {
            if (status.NextCheckAt is { } candidate &&
                (nextCheckAt is null || candidate < nextCheckAt.Value))
            {
                nextCheckAt = candidate;
            }
        }

        return new DashboardContinuationSummaryDto(
            statuses.Count,
            statuses.Count(status => status.IsRunning),
            statuses.Count(status => !status.IsRunning && status.LastError is null),
            statuses.Count(status => status.LastError is not null),
            statuses.Count(status => status.RestoredFromStore),
            statuses.Count(status => !status.RestoredFromStore),
            statuses.Count(status => status.NextCheckAt is not null),
            statuses.Count(status => ContainsStopReason(status, "Background work is still running")),
            statuses.Count(status => ContainsStopReason(status, "Monitoring is read-only")),
            nextCheckAt,
            statuses
                .Where(status => status.IsRunning)
                .Select(status => ShortGoalPrefix(status.GoalId))
                .ToList());
    }

    public IReadOnlyList<DashboardContinuationStatusDto> RestoreSubscriptionWatches(DashboardEndpointServices services)
    {
        var entries = LoadPersistedWatches(services);
        if (entries.Count == 0)
        {
            return GetStatuses();
        }

        var watchesToStart = new List<ContinuationWatch>();
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.GoalId))
                {
                    continue;
                }

                if (_watches.TryGetValue(entry.GoalId, out var existing) && existing.IsRunning)
                {
                    continue;
                }

                var watch = ContinuationWatch.Restore(entry);
                _watches[entry.GoalId] = watch;
                watchesToStart.Add(watch);
            }
        }

        foreach (var watch in watchesToStart)
        {
            watch.Start(RunSubscriptionWatchAsync(services, watch));
        }

        PersistActiveWatches(services);
        return GetStatuses();
    }

    public DashboardContinuationStatusDto StartSubscriptionWatch(DashboardEndpointServices services, string goalId)
    {
        ContinuationWatch watch;
        lock (_gate)
        {
            if (_watches.TryGetValue(goalId, out var existing) && existing.IsRunning)
            {
                return existing.ToDto();
            }

            watch = new ContinuationWatch(goalId, DateTimeOffset.UtcNow);
            _watches[goalId] = watch;
        }

        PersistActiveWatches(services);
        watch.Start(RunSubscriptionWatchAsync(services, watch));
        return watch.ToDto();
    }

    private async Task RunSubscriptionWatchAsync(DashboardEndpointServices services, ContinuationWatch watch)
    {
        try
        {
            for (var iteration = 0; iteration < _maxIterations; iteration++)
            {
                await Task.Delay(watch.GetDelay(_pollInterval), watch.Cancellation.Token);
                var agents = services.LoadAgentCatalog().Agents;
                var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
                var result = await services.State.MutateValueIfChangedAsync(
                    current =>
                    {
                        var goal = OrchestratorEntityResolver.ResolveGoal(
                            current,
                            OrchestratorEntityResolver.GetLatestGoal(current),
                            watch.GoalId);
                        var supervisor = GoalSupervisor.ApplySafe(
                            current,
                            goal,
                            agents,
                            services.Workspace,
                            AutonomyPolicy.SafeAuto);
                        if (supervisor.AppliedActions.Count == 0)
                        {
                            var advance = GoalManagementCommandService.AdvanceGoalWithSubscriptionsUntilBlocked(
                                current,
                                agents,
                                profiles,
                                services.Workspace,
                                goal,
                                providers: services.Providers);
                            return Task.FromResult((advance.Executed, advance));
                        }

                        var step = new AdvanceResultDto(
                            goal.Id.Value,
                            true,
                            null,
                            NextActionAutomationKind.None,
                            TimelineMessage($"Supervisor applied {supervisor.AppliedActions.Count} safe recovery action(s)."),
                            supervisor);
                        var supervised = new AdvanceLoopResultDto(
                            goal.Id.Value,
                            true,
                            1,
                            TimelineMessage("Supervisor applied safe recovery action."),
                            null,
                            [step]);
                        return Task.FromResult((true, supervised));
                    },
                    watch.Cancellation.Token);

                watch.RecordIteration(result);
                if (!ShouldContinueWatching(result))
                {
                    watch.Complete(result.StopReason);
                    PersistActiveWatches(services);
                    RemoveRestoredTerminalWatch(watch);
                    return;
                }

                PersistActiveWatches(services);
            }

            watch.Complete($"Stopped after {_maxIterations} server-side continuation poll(s); run continuation again if more work remains.");
            PersistActiveWatches(services);
            RemoveRestoredTerminalWatch(watch);
        }
        catch (OperationCanceledException)
        {
            watch.PauseForShutdown("Dashboard stopped before continuation finished; it will resume after restart.");
        }
        catch (Exception ex)
        {
            watch.Fail(ex.Message);
            PersistActiveWatches(services);
            RemoveRestoredTerminalWatch(watch);
        }
    }

    public static bool ShouldContinueWatching(AdvanceLoopResultDto result)
    {
        return result.StopReason.Contains("Background work is still running", StringComparison.OrdinalIgnoreCase) ||
            result.ContinueAfter is { } continueAfter && continueAfter > DateTimeOffset.UtcNow;
    }

    private static bool ContainsStopReason(DashboardContinuationStatusDto status, string value) =>
        status.StopReason.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string ShortGoalPrefix(string goalId) =>
        goalId.Length <= 8 ? goalId : goalId[..8];

    private static string TimelineMessage(string value) =>
        OutputTextPreview.CreateTimeline(value).Text;

    private static string? TimelineMessageOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : TimelineMessage(value);

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var watch in _watches.Values)
            {
                watch.Cancellation.Cancel();
                watch.Cancellation.Dispose();
            }

            _watches.Clear();
        }
    }

    private static string GetStorePath(DashboardEndpointServices services) =>
        services.Workspace.ContinuationStorePath;

    private static IReadOnlyList<ContinuationStoreEntry> LoadPersistedWatches(DashboardEndpointServices services)
    {
        var path = GetStorePath(services);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<ContinuationStoreSnapshot>(File.ReadAllText(path), StoreJsonOptions);
            return snapshot?.Watches ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void PersistActiveWatches(DashboardEndpointServices services)
    {
        List<ContinuationStoreEntry> entries;
        lock (_gate)
        {
            entries = _watches.Values
                .Where(watch => watch.IsRunning)
                .OrderBy(watch => watch.StartedAt)
                .Select(watch => watch.ToStoreEntry())
                .ToList();
        }

        var path = GetStorePath(services);
        try
        {
            if (entries.Count == 0)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ContinuationStoreSnapshot(entries), StoreJsonOptions));
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Could not persist dashboard continuation watches to {path}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Could not persist dashboard continuation watches to {path}: {ex.Message}");
        }
    }

    private void RemoveRestoredTerminalWatch(ContinuationWatch watch)
    {
        if (!watch.RestoredFromStore)
        {
            return;
        }

        lock (_gate)
        {
            if (_watches.TryGetValue(watch.GoalId, out var current) && ReferenceEquals(current, watch) && !current.IsRunning)
            {
                _watches.Remove(watch.GoalId);
            }
        }
    }

    private sealed record ContinuationStoreSnapshot(IReadOnlyList<ContinuationStoreEntry> Watches);

    internal sealed record ContinuationStoreEntry(
        string GoalId,
        DateTimeOffset StartedAt,
        DateTimeOffset? LastCheckedAt,
        int IterationCount,
        string StopReason,
        string? LastError,
        DateTimeOffset? NextCheckAt);

    private sealed class ContinuationWatch
    {
        private readonly object _gate = new();

        public ContinuationWatch(string goalId, DateTimeOffset startedAt)
        {
            GoalId = goalId;
            StartedAt = startedAt;
            StopReason = "Waiting for running background work or retry window to complete.";
        }

        private ContinuationWatch(ContinuationStoreEntry entry)
        {
            GoalId = entry.GoalId;
            StartedAt = entry.StartedAt;
            LastCheckedAt = entry.LastCheckedAt;
            IterationCount = entry.IterationCount;
            StopReason = string.IsNullOrWhiteSpace(entry.StopReason)
                ? "Waiting for running background work or retry window to complete."
                : TimelineMessage(entry.StopReason);
            LastError = TimelineMessageOrNull(entry.LastError);
            NextCheckAt = entry.NextCheckAt;
            RestoredFromStore = true;
        }

        public string GoalId { get; }

        public DateTimeOffset StartedAt { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public bool IsRunning { get; private set; } = true;

        public DateTimeOffset? LastCheckedAt { get; private set; }

        public int IterationCount { get; private set; }

        public string StopReason { get; private set; }

        public string? LastError { get; private set; }

        public DateTimeOffset? NextCheckAt { get; private set; }

        public bool RestoredFromStore { get; }

        public static ContinuationWatch Restore(ContinuationStoreEntry entry) => new(entry);

        public void Start(Task task)
        {
            _ = task;
        }

        public void RecordIteration(AdvanceLoopResultDto result)
        {
            lock (_gate)
            {
                LastCheckedAt = DateTimeOffset.UtcNow;
                IterationCount++;
                StopReason = TimelineMessage(result.StopReason);
                NextCheckAt = result.ContinueAfter is { } continueAfter && continueAfter > DateTimeOffset.UtcNow
                    ? continueAfter
                    : null;
            }
        }

        public TimeSpan GetDelay(TimeSpan fallback)
        {
            lock (_gate)
            {
                if (NextCheckAt is not { } nextCheckAt)
                {
                    return fallback;
                }

                var delay = nextCheckAt - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }
        }

        public void Complete(string stopReason)
        {
            lock (_gate)
            {
                IsRunning = false;
                LastCheckedAt ??= DateTimeOffset.UtcNow;
                StopReason = TimelineMessage(stopReason);
                NextCheckAt = null;
            }
        }

        public void PauseForShutdown(string stopReason)
        {
            lock (_gate)
            {
                IsRunning = false;
                LastCheckedAt ??= DateTimeOffset.UtcNow;
                StopReason = TimelineMessage(stopReason);
            }
        }

        public void Fail(string error)
        {
            lock (_gate)
            {
                IsRunning = false;
                LastCheckedAt = DateTimeOffset.UtcNow;
                LastError = TimelineMessage(error);
                StopReason = "Continuation watch failed.";
                NextCheckAt = null;
            }
        }

        public ContinuationStoreEntry ToStoreEntry()
        {
            lock (_gate)
            {
                return new ContinuationStoreEntry(
                    GoalId,
                    StartedAt,
                    LastCheckedAt,
                    IterationCount,
                    StopReason,
                    LastError,
                    NextCheckAt);
            }
        }

        public DashboardContinuationStatusDto ToDto()
        {
            lock (_gate)
            {
                return new DashboardContinuationStatusDto(
                    GoalId,
                    IsRunning,
                    StartedAt,
                    LastCheckedAt,
                    IterationCount,
                    StopReason,
                    LastError,
                    NextCheckAt,
                    RestoredFromStore);
            }
        }
    }
}
