using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOrchestratorStateRepository
{
    public async Task<IReadOnlyCollection<GoalId>> ListBacklogRelatedGoalIdsAsync(
        IReadOnlyCollection<string> backlogItemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backlogItemIds);
        if (backlogItemIds.Count == 0) return [];
        _readObserver?.Invoke(new("backlog-relationships", null));
        await using var connection = OpenConnection();
        var ids = new HashSet<GoalId>();
        foreach (var batch in backlogItemIds.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            using var command = connection.CreateCommand();
            var parameters = SqliteReadSession.BindIds(command, batch);
            command.CommandText = $"""
                SELECT owner_goal_id FROM source_backlog_claims WHERE backlog_item_id IN ({parameters})
                UNION
                SELECT id FROM goals WHERE source_backlog_item_id IN ({parameters})
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(new GoalId(reader.GetString(0)));
        }
        return ids;
    }

    public async Task<IReadOnlyList<SimilarityDocument>> ListCompletedGoalSimilarityDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        _readObserver?.Invoke(new("similarity-metadata", null));
        await using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // The former selected loader's id-IN query walks the primary-key index. Metadata
        // listing order is not corpus order; preserve the index order for scorer ties.
        command.CommandText = "SELECT id, CAST(snapshot_json AS BLOB) FROM goals WHERE status = 'Completed' ORDER BY id";
        var documents = new List<SimilarityDocument>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            GoalSimilarityFields? fields;
            try
            {
                using var stream = reader.GetStream(1);
                fields = JsonSerializer.Deserialize<GoalSimilarityFields>(stream, SerializerOptions);
            }
            catch (Exception ex) when (IsSnapshotDeserializeException(ex))
            {
                Console.Error.WriteLine($"QUARANTINED goal {ShortGoalId(id)}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            if (fields is { IsMetadataOnly: true })
            {
                using var archive = connection.CreateCommand();
                archive.CommandText = "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id";
                archive.Parameters.AddWithValue("$id", id);
                object? value = null;
                try { value = await archive.ExecuteScalarAsync(cancellationToken); }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
                    ex.Message.Contains("no such table: goal_snapshot_archive", StringComparison.OrdinalIgnoreCase)) { }
                using var stream = GoalSnapshotArchiveCodec.OpenDecodedStream(value);
                if (stream is not null)
                    fields = JsonSerializer.Deserialize<GoalSimilarityFields>(stream, SerializerOptions);
            }
            if (fields is not { Status: GoalStatus.Completed }) continue;
            var objective = fields.Objective.Trim();
            // Goal.FromSnapshot uses a bounded first-line title and metadata timestamps only
            // for metadata-only snapshots. Full goals use their latest timeline event.
            if (fields.IsMetadataOnly)
            {
                objective = objective.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault() ?? objective;
                if (objective.Length > 240) objective = objective[..240];
            }
            var title = objective.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "-";
            var updated = fields.IsMetadataOnly
                ? fields.TerminatedAt ?? fields.CreatedAt ?? DateTimeOffset.MinValue
                : fields.Timeline?.Latest ?? DateTimeOffset.MinValue;
            documents.Add(new("goal", fields.Id, fields.Status.ToString(), title, updated, objective));
            _readObserver?.Invoke(new("similarity-projected", id, objective.Length + title.Length));
        }
        return documents;
    }

    // JsonSerializer skips all other fields; no GoalSnapshot, task or timeline collection is created.
    private sealed record GoalSimilarityFields(string Id, string Objective, GoalStatus Status,
        bool IsMetadataOnly, DateTimeOffset? CreatedAt, DateTimeOffset? TerminatedAt,
        [property: JsonConverter(typeof(TimelineLatestConverter))] TimelineLatest? Timeline);

    private sealed record TimelineLatest(DateTimeOffset? Latest);

    private sealed class TimelineLatestConverter : JsonConverter<TimelineLatest>
    {
        public TimelineLatestConverter() { }

        public override TimelineLatest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected timeline array.");
            DateTimeOffset? latest = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected timeline event.");
                var occurredAt = DateTimeOffset.MinValue;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var isTime = reader.ValueTextEquals("OccurredAt"u8);
                    reader.Read();
                    if (isTime) occurredAt = reader.GetDateTimeOffset();
                    else reader.Skip();
                }
                if (latest is null || occurredAt > latest) latest = occurredAt;
            }
            return new(latest);
        }

        public override void Write(Utf8JsonWriter writer, TimelineLatest value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }
}
