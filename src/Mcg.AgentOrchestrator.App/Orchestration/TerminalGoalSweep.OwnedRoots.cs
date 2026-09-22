using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record TerminalGoalSweepOwnedRootResult(
    int ProcessedCount,
    int RemovedCount,
    int RetainedCount,
    int FailedCount,
    IReadOnlyList<string> ObserveOnlyReports,
    string? DeferredReason = null)
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
    private static long s_ownedRootCursor;
    private static int s_ownedRootObservationCursor;

    private static TerminalGoalSweepOwnedRootResult ReapOwnedBuildRoots(string stateDbPath)
        => ExecuteOwnedBuildRootReap(() => ReapOwnedBuildRootsCore(stateDbPath));

    internal static TerminalGoalSweepOwnedRootResult ExecuteOwnedBuildRootReap(
        Func<TerminalGoalSweepOwnedRootResult> reap)
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
            MaxOwnedBuildRootsPerSweep);
        return writeFailures.Count == 0
            ? result
            : result with
            {
                ObserveOnlyReports = [.. result.ObserveOnlyReports, .. writeFailures]
            };
    }

    private static TerminalGoalSweepOwnedRootResult ReapOwnedBuildRootsCore(string stateDbPath)
    {
        if (!File.Exists(stateDbPath) || !StateDbMigrations.IsUpToDate(stateDbPath))
            return new TerminalGoalSweepOwnedRootResult(0, 0, 0, 0, []);

        var storageRoot = DotnetBuildEnvironmentManager.CaptureStorageRoot();
        var registry = new OwnedRunRootRegistry(stateDbPath);
        var reaper = new OwnedRunRootReaper(
            registry,
            storageRoot,
            TimeProvider.System,
            new SystemOwnedRunRootProcessInspector(),
            new SystemOwnedRunRootFileSystem(),
            TimeSpan.FromMinutes(5));
        var reap = reaper.Reap(Interlocked.Read(ref s_ownedRootCursor), MaxOwnedBuildRootsPerSweep);
        Interlocked.Exchange(ref s_ownedRootCursor, reap.NextCursor);
        var observer = new OwnedRunRootObserver(
            registry,
            storageRoot,
            new SystemOwnedRunRootDirectoryEnumerator());
        var observation = observer.ObserveUnregisteredRoots(
            Volatile.Read(ref s_ownedRootObservationCursor),
            MaxOwnedBuildRootsPerSweep);
        Volatile.Write(ref s_ownedRootObservationCursor, observation.NextCursor);
        return new TerminalGoalSweepOwnedRootResult(
            reap.ProcessedCount,
            reap.RemovedCount,
            reap.RetainedCount,
            reap.FailedCount,
            observation.UnregisteredRoots);
    }
}
