using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record EpicPlanGoalClaims(IReadOnlyDictionary<string, string> ByBacklog, IReadOnlyDictionary<string, string> ByGoal);

// Claims are a read-only index into state.db, without loading a kernel or creating stores.
internal static class EpicPlanGoalClaimSnapshot
{
    internal static EpicPlanGoalClaims Read(string path)
    {
        var byBacklog = new Dictionary<string, string>(StringComparer.Ordinal);
        var byGoal = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return new(byBacklog, byGoal);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(goals)";
        var hasSource = false;
        using (var reader = command.ExecuteReader())
            while (reader.Read()) hasSource |= reader.GetString(1) == "source_backlog_item_id";
        if (hasSource)
        {
            command.CommandText = """
                SELECT id, source_backlog_item_id FROM goals WHERE source_backlog_item_id IS NOT NULL
                ORDER BY CASE WHEN status IN ('Completed', 'Cancelled', 'Superseded', 'Failed', 'AcceptanceFailed', 'Parked') THEN 1 ELSE 0 END,
                         updated_at DESC, id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                byBacklog.TryAdd(reader.GetString(1), reader.GetString(0));
                byGoal[reader.GetString(0)] = reader.GetString(1);
            }
        }
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'source_backlog_claims'";
        if (command.ExecuteScalar() is not null)
        {
            command.CommandText = "SELECT backlog_item_id, owner_goal_id FROM source_backlog_claims";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                byBacklog[reader.GetString(0)] = reader.GetString(1);
                byGoal[reader.GetString(1)] = reader.GetString(0);
            }
        }
        return new(byBacklog, byGoal);
    }
}
