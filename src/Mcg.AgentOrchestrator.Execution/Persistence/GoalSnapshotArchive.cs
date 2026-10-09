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
        SqliteConnection connection, DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = $"""
                SELECT id FROM goals
                WHERE status IN ('{GoalStatus.Completed}', '{GoalStatus.Cancelled}', '{GoalStatus.Superseded}')
                  AND updated_at < $cutoff
                  AND CASE WHEN json_valid(snapshot_json)
                      THEN COALESCE(json_extract(snapshot_json, '$.IsMetadataOnly'), 0) = 0
                        AND json_extract(snapshot_json, '$.CurrentHold') IS NULL
                        AND NOT EXISTS (
                            SELECT 1 FROM json_each(goals.snapshot_json, '$.Tasks') AS task
                            WHERE json_extract(task.value, '$.Status') NOT IN
                                ('{WorkTaskStatus.Completed}', '{WorkTaskStatus.Cancelled}')
                        )
                      ELSE 0 END
                  AND NOT EXISTS (SELECT 1 FROM goal_snapshot_archive WHERE goal_id = goals.id)
                ORDER BY id
                """;
            select.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetString(0));
        }

        var archived = 0;
        var archivedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var id in ids)
        {
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
            copy.CommandText = """
                INSERT INTO goal_snapshot_archive (goal_id, snapshot_json, archived_at)
                SELECT id, snapshot_json, $archived_at FROM goals WHERE id = $id
                ON CONFLICT(goal_id) DO NOTHING
                """;
            copy.Parameters.AddWithValue("$id", id);
            copy.Parameters.AddWithValue("$archived_at", archivedAt);
            if (await copy.ExecuteNonQueryAsync(cancellationToken) == 0)
                continue;

            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE goals SET snapshot_json = $stub WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$stub", JsonSerializer.Serialize(stub, serializerOptions));
            archived += await update.ExecuteNonQueryAsync(cancellationToken);
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
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
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
