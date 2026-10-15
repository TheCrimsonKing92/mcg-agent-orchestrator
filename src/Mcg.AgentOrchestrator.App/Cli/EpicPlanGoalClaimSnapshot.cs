using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record EpicPlanGoalClaims(IReadOnlyDictionary<string, EpicPlanSourceClaim> Persisted,
    IReadOnlyDictionary<string, EpicPlanLinkedGoal> Goals, IReadOnlyDictionary<string, string> ByGoal)
{
    internal EpicPlanSourceFacts Resolve(BacklogItem source)
    {
        var linked = ByGoal.Where(pair => pair.Value == source.Id && Goals.ContainsKey(pair.Key))
            .Select(pair => Goals[pair.Key]).OrderBy(goal => goal.Id, StringComparer.Ordinal).ToArray();
        var persisted = Persisted.GetValueOrDefault(source.Id);
        var ownerId = persisted?.OwnerGoalId ?? (linked.Length == 1 ? linked[0].Id : null);
        var owner = ownerId is null ? null : Goals.GetValueOrDefault(ownerId);
        var rawCoverage = persisted is not null ? persisted.Coverage : owner?.Coverage;
        // Null compatibility is only for legacy goal links, never a malformed persisted claim.
        SourceBacklogCoverage? coverage = rawCoverage switch
        {
            "Full" => SourceBacklogCoverage.Full,
            "Slice" => SourceBacklogCoverage.Slice,
            null when persisted is null && owner is not null => SourceBacklogCoverage.Full,
            _ => null
        };
        var resolution = persisted is null && linked.Length == 0 ? EpicPlanOwnershipResolution.Unclaimed :
            persisted is null && linked.Length > 1 ? EpicPlanOwnershipResolution.Ambiguous :
            owner is null ? EpicPlanOwnershipResolution.MissingOwner :
            coverage is null ? EpicPlanOwnershipResolution.InvalidCoverage :
            persisted is not null ? EpicPlanOwnershipResolution.Persisted : EpicPlanOwnershipResolution.Legacy;
        var delivered = linked.Concat(owner is null ? [] : new[] { owner })
            .Where(goal => goal.Status == nameof(GoalStatus.Completed)).DistinctBy(goal => goal.Id)
            .OrderBy(goal => goal.Id, StringComparer.Ordinal).ToArray();
        return new(source.Status, ownerId, coverage, resolution, owner, linked, delivered);
    }
}

// A read-only, request-scoped index. Unreadable authoritative state throws rather than implying no claims.
internal static class EpicPlanGoalClaimSnapshot
{
    internal static EpicPlanGoalClaims ReadScoped(SqliteConnection connection, IEnumerable<string> backlogIds,
        IEnumerable<string> goalIds, CancellationToken cancellationToken)
    {
        var scope = backlogIds.Distinct(StringComparer.Ordinal).ToArray();
        var persisted = new Dictionary<string, EpicPlanSourceClaim>(StringComparer.Ordinal);
        var goals = new Dictionary<string, EpicPlanLinkedGoal>(StringComparer.Ordinal);
        var byGoal = new Dictionary<string, string>(StringComparer.Ordinal);
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'source_backlog_claims'";
        var hasPersisted = probe.ExecuteScalar() is not null;
        if (hasPersisted)
            foreach (var batch in scope.Chunk(400))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT backlog_item_id, owner_goal_id, coverage FROM source_backlog_claims WHERE backlog_item_id IN ({SqliteReadSession.BindIds(command, batch)})";
                using var reader = command.ExecuteReader();
                while (reader.Read()) persisted[reader.GetString(0)] = new(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        var columns = Columns(connection);
        if (columns.Count == 0) return new(persisted, goals, byGoal);
        var source = columns.Contains("source_backlog_item_id") ? "source_backlog_item_id" : "NULL";
        var requiredGoals = goalIds.Concat(persisted.Values.Select(claim => claim.OwnerGoalId)).Distinct(StringComparer.Ordinal).ToArray();
        if (columns.Contains("source_backlog_item_id"))
            foreach (var batch in scope.Chunk(400)) ReadBatch("source_backlog_item_id", batch);
        foreach (var batch in requiredGoals.Except(goals.Keys, StringComparer.Ordinal).Chunk(400)) ReadBatch("id", batch);
        // Version 1 emits each relevant goal's own coverage in Owner, LinkedGoals and
        // DeliveredGoals, independently of the authoritative persisted source claim.
        // Hydrate only this request's scoped goals, in the same state read snapshot.
        if (columns.Contains("snapshot_json"))
        {
            foreach (var batch in goals.Keys.ToArray().Chunk(400))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT id, json_extract(snapshot_json, '$.SourceBacklogCoverage') FROM goals WHERE id IN ({SqliteReadSession.BindIds(command, batch)})";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    goals[id] = goals[id] with { Coverage = reader.IsDBNull(1) ? null : reader.GetValue(1).ToString() };
                }
            }
        }
        return new(persisted, goals, byGoal);

        void ReadBatch(string column, string[] batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            var predicate = $"{column} IN ({SqliteReadSession.BindIds(command, batch)})";
            command.CommandText = $"SELECT id, status, {source} FROM goals WHERE {predicate}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                goals[id] = new(id, reader.GetString(1), null);
                if (!reader.IsDBNull(2)) byGoal[id] = reader.GetString(2);
            }
        }
    }

    internal static HashSet<string> Columns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(goals)";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    internal static EpicPlanGoalClaims Read(string path, IReadOnlyList<Goal>? currentGoals = null)
    {
        var persisted = new Dictionary<string, EpicPlanSourceClaim>(StringComparer.Ordinal);
        var goals = new Dictionary<string, EpicPlanLinkedGoal>(StringComparer.Ordinal);
        var byGoal = new Dictionary<string, string>(StringComparer.Ordinal);
        if (currentGoals is not null)
            foreach (var goal in currentGoals)
            {
                goals[goal.Id.Value] = new(goal.Id.Value, goal.Status.ToString(), goal.SourceBacklogCoverage?.ToString());
                if (goal.SourceBacklogItemId is not null) byGoal[goal.Id.Value] = goal.SourceBacklogItemId;
            }
        if (!File.Exists(path)) return new(persisted, goals, byGoal);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(goals)";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));
        if (currentGoals is null && columns.Count != 0)
        {
            var source = columns.Contains("source_backlog_item_id") ? "source_backlog_item_id" : "NULL";
            var coverage = columns.Contains("snapshot_json") ? "json_extract(snapshot_json, '$.SourceBacklogCoverage')" : "NULL";
            command.CommandText = $"SELECT id, status, {source}, {coverage} FROM goals";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                goals[id] = new(id, reader.GetString(1), reader.IsDBNull(3) ? null : reader.GetValue(3).ToString());
                if (!reader.IsDBNull(2)) byGoal[id] = reader.GetString(2);
            }
        }
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'source_backlog_claims'";
        if (command.ExecuteScalar() is not null)
        {
            command.CommandText = "SELECT backlog_item_id, owner_goal_id, coverage FROM source_backlog_claims";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                persisted[reader.GetString(0)] = new(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        return new(persisted, goals, byGoal);
    }
}
