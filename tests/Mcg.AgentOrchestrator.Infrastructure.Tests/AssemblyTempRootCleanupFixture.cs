using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;
using Mcg.AgentOrchestrator.Infrastructure;

[assembly: Xunit.AssemblyFixture(typeof(AssemblyTempRootCleanupFixture))]

public sealed class AssemblyTempRootCleanupFixture : IAsyncDisposable
{
    private readonly StateDbOpenConnectionTracker connectionTracker = new();

    public ValueTask DisposeAsync()
    {
        try
        {
            var outcome = AssemblyTempRedirect.ReleaseOwnedRoot(AssemblyTempRootCleanupOwner.AssemblyFixture);
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

            EnsureSuccessful(outcome);
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
        private readonly ConcurrentDictionary<int, OpenConnection> openConnections = new();
        private int nextConnectionId;
        private int disposed;

        public StateDbOpenConnectionTracker()
        {
            StateDbConnectionFactory.ConnectionOpenedForDiagnostics += ObserveConnection;
        }

        internal IReadOnlyList<OpenConnection> FindWithin(string root)
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return openConnections.Values
                .Where(candidate => candidate.DatabasePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.Id)
                .ToArray();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            StateDbConnectionFactory.ConnectionOpenedForDiagnostics -= ObserveConnection;
            openConnections.Clear();
        }

        private void ObserveConnection(SqliteConnection connection, string databasePath)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            var id = Interlocked.Increment(ref nextConnectionId);
            var creationSite = Environment.StackTrace
                .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line =>
                    line.Contains("Mcg.AgentOrchestrator.Infrastructure.Tests", StringComparison.Ordinal) &&
                    !line.Contains(nameof(StateDbOpenConnectionTracker), StringComparison.Ordinal))
                ?? "unavailable";
            openConnections[id] = new OpenConnection(
                id,
                databasePath,
                Environment.CurrentManagedThreadId,
                creationSite);

            void Remove(object? sender, EventArgs args) => RemoveConnection(id);
            connection.Disposed += Remove;
            connection.StateChange += (sender, args) =>
            {
                if (args.CurrentState == ConnectionState.Closed)
                {
                    RemoveConnection(id);
                }
            };
        }

        private void RemoveConnection(int id) => openConnections.TryRemove(id, out _);
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
            $"{outcome.ExceptionMessage ?? "message unavailable"}");
    }

    private static string FormatHResult(int? hresult) =>
        hresult is null ? "unknown" : $"0x{unchecked((uint)hresult.Value):X8}";
}

internal enum AssemblyTempRootCleanupOwner
{
    AssemblyFixture,
    ProcessExitFallback
}
