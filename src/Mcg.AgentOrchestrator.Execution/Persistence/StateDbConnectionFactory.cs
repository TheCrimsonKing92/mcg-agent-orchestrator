using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum StateDbConnectionProfile
{
    ReadWrite,
    FastFailRead,
    QueryOnlyRead,
    MigrationProbeRead
}

internal static class StateDbConnectionFactory
{
    internal const int DefaultBusyTimeoutMilliseconds = 30_000;
    internal const int FastFailBusyTimeoutMilliseconds = 1_000;
    internal static event Action<SqliteConnection, string>? ConnectionOpenedForDiagnostics;

    internal static SqliteConnection Open(
        string dbPath,
        StateDbConnectionProfile profile,
        int? busyTimeoutMilliseconds = null,
        Action<string>? statementObserver = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Value cannot be empty.", nameof(dbPath));

        var timeoutMilliseconds = busyTimeoutMilliseconds ??
            (profile == StateDbConnectionProfile.FastFailRead
                ? FastFailBusyTimeoutMilliseconds
                : DefaultBusyTimeoutMilliseconds);
        if (timeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));

        if (profile == StateDbConnectionProfile.ReadWrite)
        {
            _ = StateDatabaseOfflineConversion.RecoverInterruptedReplacement(dbPath);
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = profile == StateDbConnectionProfile.ReadWrite
                ? SqliteOpenMode.ReadWriteCreate
                : SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(timeoutMilliseconds / 1000d))
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            ExecuteNonQuery(
                connection,
                "PRAGMA busy_timeout=" + timeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
                statementObserver);

            if (profile != StateDbConnectionProfile.ReadWrite)
                ExecuteNonQuery(connection, "PRAGMA query_only=1", statementObserver);

            var journalMode = ReadJournalMode(connection, statementObserver);
            if (profile == StateDbConnectionProfile.ReadWrite &&
                !journalMode.Equals("wal", StringComparison.OrdinalIgnoreCase))
            {
                journalMode = ExecuteScalar(
                    connection,
                    "PRAGMA journal_mode=WAL",
                    statementObserver)?.ToString() ?? string.Empty;
            }

            if (profile != StateDbConnectionProfile.MigrationProbeRead &&
                !journalMode.Equals("wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"State database journal mode must be WAL; found '{journalMode}'.");
            }

            NotifyConnectionOpenedForDiagnostics(connection, Path.GetFullPath(dbPath));
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void NotifyConnectionOpenedForDiagnostics(SqliteConnection connection, string databasePath)
    {
        if (ConnectionOpenedForDiagnostics is not { } observers)
        {
            return;
        }

        foreach (Action<SqliteConnection, string> observer in observers.GetInvocationList())
        {
            try
            {
                observer(connection, databasePath);
            }
            catch
            {
                // Diagnostics must never change connection-open behavior.
            }
        }
    }

    internal static string ReadJournalMode(
        SqliteConnection connection,
        Action<string>? statementObserver = null) =>
        ExecuteScalar(connection, "PRAGMA journal_mode", statementObserver)?.ToString() ?? string.Empty;

    private static object? ExecuteScalar(
        SqliteConnection connection,
        string sql,
        Action<string>? statementObserver)
    {
        statementObserver?.Invoke(sql);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        string sql,
        Action<string>? statementObserver)
    {
        statementObserver?.Invoke(sql);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
