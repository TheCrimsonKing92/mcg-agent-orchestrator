using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Retains original terminal snapshots while their hot rows hold only metadata.</summary>
public sealed class GoalSnapshotArchive(JsonSerializerOptions serializerOptions)
{
    // The caller initializes the schema and owns the connection and transaction covering the entire eligible set.
    public async Task<int> ArchiveAsync(
        SqliteConnection connection, DateTimeOffset cutoff, long maxSnapshotBytes = 134_217_728,
        CancellationToken cancellationToken = default, bool compress = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSnapshotBytes, 1L);
        var candidates = new List<(string Id, long Bytes)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = $"""
                WITH candidates AS MATERIALIZED (
                SELECT id, CASE WHEN json_valid(snapshot_json) THEN (
                    SELECT MAX(julianday(json_extract(e.value, '$.OccurredAt')))
                    FROM json_each(goals.snapshot_json, '$.Timeline') AS e
                ) END AS terminal, length(snapshot_json) AS bytes FROM goals
                WHERE status IN ('{GoalStatus.Completed}', '{GoalStatus.Cancelled}', '{GoalStatus.Superseded}')
                  AND CASE WHEN json_valid(snapshot_json)
                      THEN COALESCE(json_extract(snapshot_json, '$.IsMetadataOnly'), 0) = 0
                        AND COALESCE(json_extract(snapshot_json, '$.CurrentHold.State') COLLATE NOCASE NOT IN
                            ('steward-owner-question', 'author-owner-question'), 1)
                        AND {TerminalOpenWorkIndex.OpenWorkFlagSql("status", "goals.snapshot_json")} = 0
                      ELSE 0 END
                  AND NOT EXISTS (SELECT 1 FROM goal_snapshot_archive WHERE goal_id = goals.id)
                )
                SELECT id, terminal, bytes FROM candidates
                WHERE terminal IS NOT NULL AND terminal < julianday($cutoff)
                ORDER BY terminal ASC, id ASC
                """;
            select.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add((reader.GetString(0), reader.GetInt64(2)));
        }

        var archived = 0;
        long archivedBytes = 0;
        var archivedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var (id, bytes) in candidates)
        {
            if (archivedBytes >= maxSnapshotBytes)
                break;
            cancellationToken.ThrowIfCancellationRequested();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
            read.Parameters.AddWithValue("$id", id);
            var json = (string)(await read.ExecuteScalarAsync(cancellationToken))!;
            GoalSnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<GoalSnapshot>(json, serializerOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                continue;
            }
            if (snapshot is null || snapshot.IsMetadataOnly)
                continue;

            var stub = CreateMetadataSnapshot(snapshot);
            await using var copy = connection.CreateCommand();
            if (compress)
            {
                copy.CommandText = """
                    INSERT INTO goal_snapshot_archive (goal_id, snapshot_json, archived_at)
                    VALUES ($id, $snapshot, $archived_at)
                    ON CONFLICT(goal_id) DO NOTHING
                    """;
                copy.Parameters.Add("$snapshot", SqliteType.Blob).Value = GoalSnapshotArchiveCodec.Encode(json);
            }
            else
            {
                copy.CommandText = """
                INSERT INTO goal_snapshot_archive (goal_id, snapshot_json, archived_at)
                SELECT id, snapshot_json, $archived_at FROM goals WHERE id = $id
                ON CONFLICT(goal_id) DO NOTHING
                """;
            }
            copy.Parameters.AddWithValue("$id", id);
            copy.Parameters.AddWithValue("$archived_at", archivedAt);
            if (await copy.ExecuteNonQueryAsync(cancellationToken) == 0)
                continue;

            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE goals SET snapshot_json = $stub WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$stub", JsonSerializer.Serialize(stub, serializerOptions));
            var updated = await update.ExecuteNonQueryAsync(cancellationToken);
            archived += updated;
            if (updated > 0)
                archivedBytes += bytes;
        }
        return archived;
    }

    public async Task<GoalSnapshot?> HydrateAsync(
        SqliteConnection connection, GoalSnapshot? snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot is not { IsMetadataOnly: true })
            return snapshot;

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id";
        command.Parameters.AddWithValue("$id", snapshot.Id);
        try
        {
            var json = GoalSnapshotArchiveCodec.Decode(await command.ExecuteScalarAsync(cancellationToken));
            return json is null ? snapshot : JsonSerializer.Deserialize<GoalSnapshot>(json, serializerOptions);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table: goal_snapshot_archive", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot;
        }
    }

    private static GoalSnapshot CreateMetadataSnapshot(GoalSnapshot snapshot)
    {
        var seed = new GoalSnapshot(
            snapshot.Id, snapshot.Objective, snapshot.Status, [], [], IsMetadataOnly: true,
            ResultCommit: snapshot.Tasks.Select(task => task.LastDispatch?.ResultCommit)
                .LastOrDefault(commit => !string.IsNullOrEmpty(commit)),
            CreatedAt: snapshot.Timeline.FirstOrDefault()?.OccurredAt,
            TerminatedAt: snapshot.Timeline.LastOrDefault()?.OccurredAt);
        // Public kernel APIs reach Goal.FromSnapshot and Goal.ToSnapshot in Core.
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([seed], []));
        return kernel.ExportGoalSnapshot(new GoalId(snapshot.Id));
    }
}
