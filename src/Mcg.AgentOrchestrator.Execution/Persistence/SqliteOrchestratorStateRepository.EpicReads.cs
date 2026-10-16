using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOrchestratorStateRepository
{
    public static async Task<IReadOnlyList<GoalSummary>> ReadEpicMetadataAsync(SqliteConnection connection,
        IEnumerable<string> ids, bool includeTerminalCreatedAt, CancellationToken cancellationToken = default)
    {
        var results = new List<GoalSummary>();
        using var probe = connection.CreateCommand();
        probe.CommandText = "PRAGMA table_info(goals)";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = probe.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));
        if (columns.Count == 0) return results;
        var hasPromotionFacts = HasPromotionFactsTable(connection);
        var promotionColumns = hasPromotionFacts
            ? "promotion.goal_id, promotion.candidate_revision, promotion.prior_main_revision, promotion.promoted_main_revision, promotion.promoted_at, promotion.provenance"
            : "NULL, NULL, NULL, NULL, NULL, NULL";
        var promotionJoin = hasPromotionFacts
            ? "LEFT JOIN goal_promotion_facts AS promotion ON promotion.goal_id = goals.id" : string.Empty;
        foreach (var batch in ids.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            var scope = SqliteReadSession.BindIds(command, batch);
            var created = columns.Contains("snapshot_json") ? $"""
                CASE WHEN {MetadataNonTerminalPredicate()} THEN (
                    SELECT json_extract(evt.value, '$.OccurredAt')
                    FROM json_each(goals.snapshot_json, '$.Timeline') AS evt
                    ORDER BY CAST(evt.key AS INTEGER) ASC LIMIT 1
                ) WHEN $include_terminal_created_at = 1
                    THEN COALESCE(json_extract(goals.snapshot_json, '$.Timeline[0].OccurredAt'),
                        json_extract(goals.snapshot_json, '$.CreatedAt')) END
                """ : "NULL";
            var objective = columns.Contains("objective") ? "objective" : "''";
            var updated = columns.Contains("updated_at") ? "updated_at" : "''";
            var condition = columns.Contains("snapshot_json") ? ActiveWithFailedTaskConditionSql() : "NULL";
            command.CommandText = $"SELECT id, status, {objective}, {updated}, {created}, {condition}, {promotionColumns} FROM goals {promotionJoin} WHERE id IN ({scope}) ORDER BY {updated} DESC";
            if (columns.Contains("snapshot_json"))
                command.Parameters.AddWithValue("$include_terminal_created_at", includeTerminalCreatedAt ? 1 : 0);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    CreatedAt: reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    Condition: reader.IsDBNull(5) ? null : reader.GetString(5),
                    PromotionFact: ReadPromotionFact(reader, 6)));
        }
        return results;
    }

    public static async Task<IReadOnlyList<Goal>> ReadEpicGoalsAsync(SqliteConnection connection,
        IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var snapshots = new List<GoalSnapshot>();
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('goals') WHERE name = 'snapshot_json'";
        if (Convert.ToInt64(probe.ExecuteScalar()) == 0) return [];
        foreach (var batch in ids.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id, snapshot_json FROM goals WHERE id IN ({SqliteReadSession.BindIds(command, batch)})";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (TryReadGoalSnapshot(reader.GetString(0), reader.GetString(1), null, logQuarantine: true, out var snapshot))
                    snapshots.Add((await SnapshotArchive.HydrateAsync(connection, snapshot, cancellationToken))!);
        }
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(snapshots, [])).Goals.ToArray();
    }
}
