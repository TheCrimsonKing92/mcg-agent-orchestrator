using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record TerminalGoalSweepOwnedRootResult(
    int ProcessedCount,
    int RemovedCount,
    int RetainedCount,
    int FailedCount,
    IReadOnlyList<string> ObserveOnlyReports,
    string? DeferredReason = null,
    IReadOnlyList<string>? ReclaimedGoalRoots = null,
    IReadOnlyList<string>? GoalRootReclaimFailures = null)
{
    internal IReadOnlyList<string> OperatorEvents
    {
        get
        {
            var events = new List<string>(ObserveOnlyReports.Count + 1);
            if (!string.IsNullOrWhiteSpace(DeferredReason))
                events.Add($"SWEEP_OWNED_ROOT_DEFERRED reason=\"{Sanitize(DeferredReason)}\"");
            events.AddRange(ObserveOnlyReports.Select(report =>
                $"SWEEP_OWNED_ROOT_OBSERVED detail=\"{Sanitize(report)}\""));
            if (ReclaimedGoalRoots is not null)
                events.AddRange(ReclaimedGoalRoots.Select(path =>
                    $"SWEEP_GOAL_ROOT_RECLAIMED path=\"{Sanitize(path)}\""));
            if (GoalRootReclaimFailures is not null)
                events.AddRange(GoalRootReclaimFailures.Select(failure =>
                    $"SWEEP_GOAL_ROOT_RECLAIM_FAILED detail=\"{Sanitize(failure)}\""));
            return events;
        }
    }

    private static string Sanitize(string value) => value
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Replace('"', '\'');
}

internal static partial class TerminalGoalSweep
{
    internal const int MaxOwnedBuildRootsPerSweep = 25;
    private static readonly OwnedRootSweepState s_ownedRootState = new();

    internal sealed class OwnedRootSweepState
    {
        internal long ReapCursor;
        internal int ObservationCursor;
        internal readonly OwnedRunRootObservationLedger ObservationLedger = new();
    }

    private static TerminalGoalSweepOwnedRootResult ReapOwnedBuildRoots(string stateDbPath)
    {
        var storageRoot = DotnetBuildEnvironmentManager.CaptureStorageRoot();
        return ExecuteOwnedBuildRootReap(() => ReapOwnedBuildRootsCore(
            stateDbPath, storageRoot, s_ownedRootState, usesSharedStorageRoot: true), storageRoot);
    }

    // The per-user build folder is shared by every temporary and project state store in this
    // process. Only the default repository store may use absence from its goals table as
    // evidence that a goal root is orphaned.
    internal static bool IsCanonicalGoalRootStore(string stateDbPath)
    {
        var repoRoot = OrchestratorWorkspace.ResolveRepoRoot();
        var canonicalDb = OrchestratorWorkspace.ForDirectory(repoRoot).SqliteStatePath;
        return string.Equals(Path.GetFullPath(stateDbPath), Path.GetFullPath(canonicalDb),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    internal static TerminalGoalSweepOwnedRootResult ExecuteOwnedBuildRootReap(
        Func<TerminalGoalSweepOwnedRootResult> reap, DotnetBuildStorageRoot? storageRoot = null)
    {
        ArgumentNullException.ThrowIfNull(reap);
        TerminalGoalSweepOwnedRootResult result;
        try
        {
            result = reap();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            result = new TerminalGoalSweepOwnedRootResult(
                0,
                0,
                0,
                0,
                [],
                $"owned-root reap deferred: sqlite code {ex.SqliteErrorCode}: {ex.Message}");
        }

        var writeFailures = DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(
            MaxOwnedBuildRootsPerSweep, storageRoot);
        return writeFailures.Count == 0
            ? result
            : result with
            {
                ObserveOnlyReports = [.. result.ObserveOnlyReports, .. writeFailures]
            };
    }

    internal static TerminalGoalSweepOwnedRootResult ReapOwnedBuildRootsCore(
        string stateDbPath, DotnetBuildStorageRoot storageRoot, OwnedRootSweepState state,
        Func<int, bool>? isOwnerRunning = null, bool usesSharedStorageRoot = false)
    {
        ArgumentNullException.ThrowIfNull(storageRoot);
        ArgumentNullException.ThrowIfNull(state);
        if (!File.Exists(stateDbPath) || !StateDbMigrations.IsUpToDate(stateDbPath))
            return new TerminalGoalSweepOwnedRootResult(0, 0, 0, 0, []);

        var registry = new OwnedRunRootRegistry(stateDbPath);
        var reaper = new OwnedRunRootReaper(
            registry,
            storageRoot,
            TimeProvider.System,
            new SystemOwnedRunRootProcessInspector(),
            new SystemOwnedRunRootFileSystem(),
            TimeSpan.FromMinutes(5));
        var reap = reaper.Reap(Interlocked.Read(ref state.ReapCursor), MaxOwnedBuildRootsPerSweep);
        Interlocked.Exchange(ref state.ReapCursor, reap.NextCursor);
        GoalBuildRootReclaimResult reclaimed = new([], []);
        if (!usesSharedStorageRoot || IsCanonicalGoalRootStore(stateDbPath))
        {
            var storedGoalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var connection = StateDbConnectionFactory.Open(stateDbPath, StateDbConnectionProfile.FastFailRead))
            using (var goals = connection.CreateCommand())
            {
                goals.CommandText = "SELECT id FROM goals";
                using var reader = goals.ExecuteReader();
                while (reader.Read())
                    storedGoalIds.Add(reader.GetString(0));
            }
            reclaimed = new GoalBuildRootReclaimer(storageRoot, registry, isOwnerRunning)
                .Reclaim(storedGoalIds, MaxOwnedBuildRootsPerSweep);
        }
        var observer = new OwnedRunRootObserver(
            registry,
            storageRoot,
            new SystemOwnedRunRootDirectoryEnumerator(),
            state.ObservationLedger);
        var observation = observer.ObserveUnregisteredRoots(
            Volatile.Read(ref state.ObservationCursor),
            MaxOwnedBuildRootsPerSweep);
        Volatile.Write(ref state.ObservationCursor, observation.NextCursor);
        return new TerminalGoalSweepOwnedRootResult(
            reap.ProcessedCount,
            reap.RemovedCount,
            reap.RetainedCount,
            reap.FailedCount,
            observation.UnregisteredRoots,
            ReclaimedGoalRoots: reclaimed.ReclaimedRoots,
            GoalRootReclaimFailures: reclaimed.Failures);
    }
}
