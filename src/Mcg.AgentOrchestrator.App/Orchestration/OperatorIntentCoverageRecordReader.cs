using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OperatorIntentCoverageRecordReader
{
    internal static (IReadOnlyList<OperatorIntentCoverageIntent> Intents,
        IReadOnlyList<OperatorIntentCoverageTrigger> Triggers) Read(OrchestratorWorkspace workspace) =>
        (ReadRows(workspace, "operator-intents.db",
            "SELECT id, verb, goal_id, task_id, payload_json, channel, actor_kind, status, created_at FROM operator_intents",
            r =>
            {
                if (!Timestamp(r.GetString(8), out var at)) return null;
                OperatorActorKind? actor = null;
                if (!r.IsDBNull(6))
                {
                    if (!Enum.TryParse<OperatorActorKind>(r.GetString(6), out var parsed) || !Enum.IsDefined(parsed))
                        return null;
                    actor = parsed;
                }
                return new OperatorIntentCoverageIntent(r.GetString(0), r.GetString(1), r.GetString(2),
                    Nullable(r, 3), Nullable(r, 4), r.GetString(5), actor, r.GetString(7), at);
            }),
        ReadRows(workspace, "steward-triggers.db",
            "SELECT goal_id, task_id, kind, first_occurrence, intent_id FROM steward_triggers",
            r => Timestamp(r.GetString(3), out var at)
                ? new OperatorIntentCoverageTrigger(r.GetString(0), r.GetString(1), r.GetString(2), at, Nullable(r, 4))
                : null));

    private static IReadOnlyList<T> ReadRows<T>(OrchestratorWorkspace workspace, string file,
        string sql, Func<SqliteDataReader, T?> parse) where T : class
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, file);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Warning: intent-coverage: {file} is missing.");
            return [];
        }
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            var skipped = 0;
            while (reader.Read())
            {
                var row = parse(reader);
                if (row is null) skipped++; else rows.Add(row);
            }
            if (skipped > 0)
                Console.Error.WriteLine($"Warning: intent-coverage: {file}: skipped {skipped} invalid rows.");
            return rows;
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine($"Warning: intent-coverage: {file} unavailable: {ex.Message}");
            return [];
        }
    }

    private static bool Timestamp(string text, out DateTimeOffset at) => DateTimeOffset.TryParse(text,
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);
    private static string? Nullable(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : reader.GetString(column);
}
