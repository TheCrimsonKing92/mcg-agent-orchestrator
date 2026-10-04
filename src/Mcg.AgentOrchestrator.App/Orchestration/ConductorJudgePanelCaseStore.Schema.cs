using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorJudgePanelCaseStore
{
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(new SqliteConnectionStringBuilder(_connectionString).DataSource)!);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS panel_cases (
                case_id TEXT PRIMARY KEY, goal_id TEXT NOT NULL, candidate_sha TEXT NOT NULL,
                base_sha TEXT NOT NULL, criteria_version TEXT NOT NULL, trigger_id TEXT NOT NULL,
                key_json TEXT NOT NULL, packet_hash TEXT NOT NULL, enrollment_window INTEGER,
                admitted INTEGER NOT NULL, status TEXT NOT NULL, claim_token TEXT, claimed_at TEXT,
                terminal TEXT, reason TEXT, event_written INTEGER NOT NULL DEFAULT 0,
                UNIQUE(goal_id, candidate_sha, base_sha, criteria_version, trigger_id));
            CREATE INDEX IF NOT EXISTS panel_queue ON panel_cases(status);
            CREATE TABLE IF NOT EXISTS panel_calls (
                case_id TEXT NOT NULL, judge TEXT NOT NULL, launched INTEGER NOT NULL,
                outcome TEXT, result_json TEXT, PRIMARY KEY(case_id, judge));
            CREATE TABLE IF NOT EXISTS panel_judge_health (
                judge TEXT PRIMARY KEY, consecutive_failures INTEGER NOT NULL,
                state TEXT NOT NULL, suspended_case_id TEXT);
            CREATE TABLE IF NOT EXISTS panel_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS panel_goals (ordinal INTEGER PRIMARY KEY,
                goal_id TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL);
            """);
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }
    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }
    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }
}
