using System.Data;
using Microsoft.Data.Sqlite;
using Mcg.AgentOrchestrator.Infrastructure;

[assembly: Xunit.AssemblyFixture(typeof(AssemblyTempRootCleanupFixture))]

public sealed class AssemblyTempRootCleanupFixture : IAsyncDisposable
{
    private readonly StateDbOpenConnectionTracker connectionTracker = new();
    private readonly Func<TempRootDeleteOutcome?> releaseOwnedRoot;
    private readonly TextWriter? diagnostics;
    private readonly Func<ProcessCommandLineSnapshot> processSnapshot;

    public AssemblyTempRootCleanupFixture()
        : this(() => AssemblyTempRedirect.ReleaseOwnedRoot(AssemblyTempRootCleanupOwner.AssemblyFixture), null)
    {
    }

    internal AssemblyTempRootCleanupFixture(
        Func<TempRootDeleteOutcome?> releaseOwnedRoot,
        TextWriter? diagnostics,
        Func<ProcessCommandLineSnapshot>? processSnapshot = null)
    {
        this.releaseOwnedRoot = releaseOwnedRoot;
        this.diagnostics = diagnostics;
        this.processSnapshot = processSnapshot ?? ProcessCommandLines.Snapshot;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            (diagnostics ?? Console.Error).WriteLine(
                LaunchLockTelemetry.FormatSummary(LaunchLockTelemetry.Process.Snapshot(), Environment.ProcessId));
            var outcome = releaseOwnedRoot();
            if (outcome?.Status == TempRootDeleteStatus.Failed)
            {
                var openConnections = connectionTracker.FindWithin(outcome.Path);
                Console.Error.WriteLine(
                    $"state-db-connection-tracking source=StateDbConnectionFactory " +
                    $"root={Quote(outcome.Path)} open_count={openConnections.Count}");
                foreach (var connection in openConnections.Take(8))
                {
                    Console.Error.WriteLine(connection.FormatDiagnostic());
                }
                if (openConnections.Count > 8)
                {
                    Console.Error.WriteLine($"state-db-open-connection omitted_count={openConnections.Count - 8}");
                }
            }

            EnsureSuccessful(outcome, outcome?.Status == TempRootDeleteStatus.Failed
                ? AssemblyTempRootCleanupHolderDiagnostics.Describe(outcome.Path, processSnapshot)
                : null);
            return ValueTask.CompletedTask;
        }
        finally
        {
            connectionTracker.Dispose();
        }
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    internal sealed class StateDbOpenConnectionTracker : IDisposable
    {
        private readonly object gate = new();
        private readonly Dictionary<int, TrackedConnection> trackedConnections = [];
        private int nextConnectionId;
        private bool disposed;

        public StateDbOpenConnectionTracker()
        {
            StateDbConnectionFactory.ConnectionOpenedForDiagnostics += ObserveConnection;
        }

        internal IReadOnlyList<OpenConnection> FindWithin(string root)
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            lock (gate)
            {
                if (disposed)
                {
                    return [];
                }

                var deadConnectionIds = new List<int>();
                var openConnections = new List<OpenConnection>();
                foreach (var (id, tracked) in trackedConnections)
                {
                    if (!tracked.Connection.TryGetTarget(out var connection))
                    {
                        deadConnectionIds.Add(id);
                        continue;
                    }

                    if (connection.State == ConnectionState.Open &&
                        tracked.Diagnostic.DatabasePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        openConnections.Add(tracked.Diagnostic);
                    }
                }

                foreach (var id in deadConnectionIds)
                {
                    trackedConnections.Remove(id);
                }

                return openConnections
                    .OrderBy(candidate => candidate.Id)
                    .ToArray();
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                trackedConnections.Clear();
            }

            StateDbConnectionFactory.ConnectionOpenedForDiagnostics -= ObserveConnection;
        }

        private void ObserveConnection(SqliteConnection connection, string databasePath)
        {
            var creationSite = Environment.StackTrace
                .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line =>
                    line.Contains("Mcg.AgentOrchestrator.Infrastructure.Tests", StringComparison.Ordinal) &&
                    !line.Contains(nameof(StateDbOpenConnectionTracker), StringComparison.Ordinal))
                ?? "unavailable";
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                var id = ++nextConnectionId;
                trackedConnections[id] = new TrackedConnection(
                    new WeakReference<SqliteConnection>(connection),
                    new OpenConnection(
                        id,
                        databasePath,
                        Environment.CurrentManagedThreadId,
                        creationSite));
            }
        }

        private sealed record TrackedConnection(
            WeakReference<SqliteConnection> Connection,
            OpenConnection Diagnostic);
    }

    internal sealed record OpenConnection(int Id, string DatabasePath, int ThreadId, string CreationSite)
    {
        internal string FormatDiagnostic() =>
            $"state-db-open-connection id={Id} thread={ThreadId} path={Quote(DatabasePath)} " +
            $"creation_site={Quote(Bound(CreationSite, 512))}";

        private static string Bound(string value, int maximumLength) =>
            value.Length <= maximumLength ? value : value[..maximumLength];
    }

    internal static void EnsureSuccessful(TempRootDeleteOutcome? outcome)
        => EnsureSuccessful(outcome, null);

    private static void EnsureSuccessful(TempRootDeleteOutcome? outcome, string? holderDiagnostics)
    {
        if (outcome?.Status != TempRootDeleteStatus.Failed)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assembly temp-root cleanup failed for '{outcome.Path}' " +
            $"({outcome.ExceptionType ?? "unknown"}, " +
            $"hresult={FormatHResult(outcome.ExceptionHResult)}, " +
            $"attempts={outcome.DeleteAttempts}, " +
            $"at '{outcome.FailurePath ?? "unknown"}'): " +
            $"{outcome.ExceptionMessage ?? "message unavailable"}" +
            (holderDiagnostics is null ? string.Empty : Environment.NewLine + holderDiagnostics));
    }

    private static string FormatHResult(int? hresult) =>
        hresult is null ? "unknown" : $"0x{unchecked((uint)hresult.Value):X8}";
}

internal enum AssemblyTempRootCleanupOwner
{
    AssemblyFixture,
    ProcessExitFallback
}
