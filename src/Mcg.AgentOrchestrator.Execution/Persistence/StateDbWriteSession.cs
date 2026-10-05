using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Makes the connection that owns the current state.db write transaction available to
/// same-process state helpers. A helper targeting that same database must join this
/// connection instead of opening a second SQLite writer and waiting on its own lock.
/// </summary>
internal static class StateDbWriteSession
{
    private sealed class Session(
        string databasePath,
        SqliteConnection connection,
        Session? previous)
    {
        public string DatabasePath { get; } = databasePath;
        public SqliteConnection Connection { get; } = connection;
        public Session? Previous { get; } = previous;
        public object SyncRoot { get; } = new();
        public bool IsActive { get; set; } = true;
    }

    private sealed class Scope(Session session) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (session.SyncRoot)
            {
                if (_disposed)
                    return;

                _disposed = true;
                session.IsActive = false;
            }

            Current.Value = session.Previous;
        }
    }

    private static readonly AsyncLocal<Session?> Current = new();
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static IDisposable Enter(string databasePath, SqliteConnection connection)
    {
        var session = new Session(Normalize(databasePath), connection, Current.Value);
        Current.Value = session;
        return new Scope(session);
    }

    public static bool TryExecute(string databasePath, Action<SqliteConnection> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var normalized = Normalize(databasePath);
        for (var session = Current.Value; session is not null; session = session.Previous)
        {
            lock (session.SyncRoot)
            {
                if (!session.IsActive)
                    return false;

                if (string.Equals(session.DatabasePath, normalized, PathComparison))
                {
                    action(session.Connection);
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsActiveFor(string databasePath)
    {
        var normalized = Normalize(databasePath);
        for (var session = Current.Value; session is not null; session = session.Previous)
        {
            lock (session.SyncRoot)
            {
                if (session.IsActive &&
                    string.Equals(session.DatabasePath, normalized, PathComparison))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Normalize(string databasePath) =>
        Path.GetFullPath(databasePath);
}
