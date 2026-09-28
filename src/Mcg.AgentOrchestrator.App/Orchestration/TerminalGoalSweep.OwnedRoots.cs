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

    private static TerminalGoalSweepOwnedRootResult ReapOwnedBuildRoots(
        string stateDbPath, bool reclaimGoalRoots)
    {
        var storageRoot = DotnetBuildEnvironmentManager.CaptureStorageRoot();
        return ExecuteOwnedBuildRootReap(() => ReapOwnedBuildRootsCore(
            stateDbPath, storageRoot, s_ownedRootState, usesSharedStorageRoot: true,
            reclaimGoalRoots: reclaimGoalRoots, requireSharedRootOwnership: reclaimGoalRoots));
    }

    // A temporary or scoped store cannot prove that a root in the shared folder is orphaned.
    internal static bool IsCanonicalGoalRootStore(string stateDbPath)
    {
        var repoRoot = OrchestratorWorkspace.ResolveRepoRoot();
        var canonicalDb = OrchestratorWorkspace.ForDirectory(repoRoot).SqliteStatePath;
        return string.Equals(Path.GetFullPath(stateDbPath), Path.GetFullPath(canonicalDb),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    internal static TerminalGoalSweepOwnedRootResult ExecuteOwnedBuildRootReap(
        Func<TerminalGoalSweepOwnedRootResult> reap,
        DotnetBuildStorageRoot? writeFailureScope = null)
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
            MaxOwnedBuildRootsPerSweep, writeFailureScope);
        string artifactReport;
        try
        {
            artifactReport = TempRootJanitor.ReapLeakedTempArtifacts(
                Path.GetTempPath(), OrchestratorTempRoot.GetParent(), TimeProvider.System).SummaryLine;
        }
        catch (Exception ex)
        {
            artifactReport = $"temp-artifact-janitor failed={ex.GetType().Name}";
        }
        return result with
        {
            ObserveOnlyReports = [.. result.ObserveOnlyReports, .. writeFailures, artifactReport]
        };
    }

    internal static TerminalGoalSweepOwnedRootResult ReapOwnedBuildRootsCore(
        string stateDbPath, DotnetBuildStorageRoot storageRoot, OwnedRootSweepState state,
        Func<int, bool>? isOwnerRunning = null, bool usesSharedStorageRoot = false,
        string? canonicalRepoRoot = null, OrchestratorProjectRegistry? projectRegistry = null,
        bool reclaimGoalRoots = true, bool requireSharedRootOwnership = false)
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
        string? discoveryFailure = null;
        var repoRoot = canonicalRepoRoot ?? OrchestratorWorkspace.ResolveRepoRoot();
        var hasReclaimAuthority = !requireSharedRootOwnership ||
            string.Equals(Path.GetFullPath(stateDbPath),
                Path.GetFullPath(OrchestratorWorkspace.ForDirectory(repoRoot).SqliteStatePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var sharedStores = !reclaimGoalRoots || !hasReclaimAuthority ? null : usesSharedStorageRoot
            ? GetSharedGoalStorePaths(stateDbPath, out discoveryFailure, canonicalRepoRoot, projectRegistry)
            : [stateDbPath];
        if (sharedStores is null)
        {
            if (discoveryFailure is not null)
                reclaimed = new GoalBuildRootReclaimResult([], [discoveryFailure]);
        }
        else if (TryReadGoalIds(sharedStores, out var storedGoalIds, out var readFailure))
        {
            reclaimed = new GoalBuildRootReclaimer(storageRoot, registry, repoRoot, isOwnerRunning)
                .Reclaim(storedGoalIds, MaxOwnedBuildRootsPerSweep);
        }
        else
            reclaimed = new GoalBuildRootReclaimResult([], [readFailure!]);
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
