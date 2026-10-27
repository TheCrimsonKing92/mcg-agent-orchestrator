using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
                fields = ReadGoalSimilarityFields(stream, () => reader.GetStream(1));
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
                    fields = ReadGoalSimilarityFields(stream, () => GoalSnapshotArchiveCodec.OpenDecodedStream(value)!);
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

    private static GoalSimilarityFields? ReadGoalSimilarityFields(Stream stream, Func<Stream> reopenStream)
    {
        try
        {
            return JsonSerializer.Deserialize<GoalSimilarityFields>(stream, SerializerOptions);
        }
        catch (InvalidSimilarityTaskShapeException)
        {
            // Let the standard collection converter produce its original type, path and
            // byte location from the original document. An empty JsonException thrown by
            // our converter instead names GoalSimilarityFields, changing the diagnostic.
            // Replay only malformed task shapes/members; valid payloads stay on the projection path.
            using var diagnosticStream = reopenStream();
            _ = JsonSerializer.Deserialize<SimilarityTaskShapeDiagnostic>(diagnosticStream, SerializerOptions);
            throw new InvalidOperationException("Malformed task shape unexpectedly passed standard deserialization.");
        }
    }

    private sealed record SimilarityTaskShapeDiagnostic(IReadOnlyList<TaskSnapshot>? Tasks);
    private sealed class InvalidSimilarityTaskShapeException : Exception { }

    // Validate the task collection and typed members without retaining valid payloads. A malformed hot
    // collection must still be quarantined; the same corruption in an archive must throw.
    // No GoalSnapshot, task or timeline collection is created on the valid projection path.
    private sealed record GoalSimilarityFields(string Id, string Objective, GoalStatus Status,
        bool IsMetadataOnly, DateTimeOffset? CreatedAt, DateTimeOffset? TerminatedAt,
        [property: JsonConverter(typeof(SimilarityTaskShapeConverter))] IReadOnlyList<TaskSnapshot>? Tasks,
        [property: JsonConverter(typeof(TimelineLatestConverter))] TimelineLatest? Timeline);

    private sealed class SimilarityTaskShapeConverter : JsonConverter<IReadOnlyList<TaskSnapshot>>
    {
        public SimilarityTaskShapeConverter() { }

        public override IReadOnlyList<TaskSnapshot> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new InvalidSimilarityTaskShapeException();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.Null))
                    throw new InvalidSimilarityTaskShapeException();
                try
                {
                    ValidateTaskValue(ref reader, typeof(TaskSnapshot), options);
                }
                catch (Exception ex) when (IsSnapshotDeserializeException(ex))
                {
                    // Replay the original document to retain the standard member path,
                    // exception type and byte position, including nested task members.
                    throw new InvalidSimilarityTaskShapeException();
                }
            }
            return Array.Empty<TaskSnapshot>();
        }

        private static void ValidateTaskValue(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            // The serializer contract owns member types. Do not maintain a second task schema
            // here, or decode unused descriptions, outputs and collection contents into a DOM.
            if (type == typeof(string) && reader.TokenType is JsonTokenType.String or JsonTokenType.Null)
                return;
            var contract = options.GetTypeInfo(type);
            if (contract.Kind == JsonTypeInfoKind.Object && reader.TokenType == JsonTokenType.StartObject
                && !contract.Properties.Any(property => property.IsRequired || property.CustomConverter is not null))
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    JsonPropertyInfo? member = null;
                    foreach (var property in contract.Properties)
                        if (reader.ValueTextEquals(property.Name))
                        {
                            member = property;
                            break;
                        }
                    reader.Read();
                    if (member is null || (member.Set is null &&
                        (member.Get is null || member.AssociatedParameter is null))) reader.Skip();
                    else ValidateTaskValue(ref reader, member.PropertyType, options);
                }
                return;
            }
            if (contract.Kind == JsonTypeInfoKind.Enumerable && reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    ValidateTaskValue(ref reader, contract.ElementType!, options);
                return;
            }
            // Scalars, nulls and custom converters use the real serializer's validation.
            // Unusual contracts (e.g. required members) also retain its complete validation.
            _ = JsonSerializer.Deserialize(ref reader, type, options);
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<TaskSnapshot> value,
            JsonSerializerOptions options) => throw new NotSupportedException();
    }

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
