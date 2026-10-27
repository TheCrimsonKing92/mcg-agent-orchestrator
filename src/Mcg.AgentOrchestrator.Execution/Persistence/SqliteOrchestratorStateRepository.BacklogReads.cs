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
            if (fields is null)
            {
                Console.Error.WriteLine($"QUARANTINED goal {ShortGoalId(id)}: JsonException: deserialized goal snapshot was null");
                continue;
            }
            ValidateSimilarityTaskCount(fields);
            ValidateSimilarityDomain(fields, () => reader.GetStream(1));
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
                ValidateSimilarityTaskCount(fields);
                ValidateSimilarityDomain(fields, () => GoalSnapshotArchiveCodec.OpenDecodedStream(value)!);
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
            return JsonSerializer.Deserialize<GoalSimilarityFields>(stream, SimilarityJson.Options);
        }
        catch (InvalidSimilarityShapeException)
        {
            // Replay only malformed payloads with the original contract, preserving the
            // exception type, nested member path and byte location of full hydration.
            using var diagnosticStream = reopenStream();
            _ = JsonSerializer.Deserialize<GoalSnapshot>(diagnosticStream, SerializerOptions);
            throw new InvalidOperationException("Malformed snapshot unexpectedly passed standard deserialization.");
        }
    }

    private static void ValidateSimilarityTaskCount(GoalSimilarityFields? fields)
    {
        // Reconstruction runs after hot deserialization/quarantine, so this domain
        // failure must escape for both hot and archived full goals.
        if (fields is { IsMetadataOnly: false, Tasks.Count: 0 })
            throw new ArgumentException("A goal must have at least one task.", "tasks");
    }

    private static void ValidateSimilarityDomain(GoalSimilarityFields? fields, Func<Stream> reopenStream)
    {
        if (fields is not null && !string.IsNullOrWhiteSpace(fields.Objective) &&
            (fields.IsMetadataOnly || fields is { Tasks.RequiresReconstruction: false, Timeline.HasNullEntry: false }
                && !fields.Domain.RequiresReconstruction)) return;

        // These are reconstruction failures, not deserialization failures. Replay only
        // suspect documents, outside hot quarantine, so Core owns the exception contract.
        using var stream = reopenStream();
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(stream, SerializerOptions);
        _ = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot!], []));
    }

    private sealed class InvalidSimilarityShapeException : Exception { }

    private static class SimilarityJson
    {
        internal static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(contract =>
            {
                if (contract.Type != typeof(GoalSimilarityFields)) return;
                // The persisted contract owns all member types, including fields the scorer
                // does not use. Validate those fields without retaining their payloads.
                foreach (var member in contract.Options.GetTypeInfo(typeof(GoalSnapshot)).Properties)
                {
                    if (contract.Properties.Any(property => property.Name == member.Name)) continue;
                    var discarded = contract.CreateJsonPropertyInfo(typeof(object), member.Name);
                    discarded.CustomConverter = new DiscardedSimilarityValueConverter(member.PropertyType, member.Name);
                    discarded.Set = (instance, value) => ((GoalSimilarityFields)instance).Domain.Set(member.Name, value);
                    contract.Properties.Add(discarded);
                }
            });
            return new(SerializerOptions) { TypeInfoResolver = resolver };
        }
    }

    private sealed class DiscardedSimilarityValueConverter(Type valueType, string memberName) : JsonConverter<object>
    {
        public override bool HandleNull => true;

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var projection = reader;
            try { SimilarityTaskShapeConverter.ValidateTaskValue(ref reader, valueType, options); }
            catch (Exception ex) when (IsSnapshotDeserializeException(ex))
            {
                throw new InvalidSimilarityShapeException();
            }
            return memberName switch
            {
                nameof(GoalSnapshot.SourceBacklogItemId) => projection.TokenType == JsonTokenType.Null
                    ? (bool?)null : IsBlankSimilarityText(ref projection),
                nameof(GoalSnapshot.SourceBacklogCoverage) => projection.TokenType != JsonTokenType.Null,
                nameof(GoalSnapshot.ExplicitDisposition) => JsonSerializer.Deserialize<GoalExplicitDisposition>(ref projection, options),
                nameof(GoalSnapshot.ParkedLifecycleStatus) => JsonSerializer.Deserialize<GoalStatus?>(ref projection, options),
                nameof(GoalSnapshot.BriefVersions) or nameof(GoalSnapshot.RefinedSpecVersions) =>
                    HasInvalidSimilarityVersionChain(ref projection, options),
                _ => null
            };
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }

    // Validate the task collection and typed members without retaining valid payloads. A malformed hot
    // collection must still be quarantined; the same corruption in an archive must throw.
    // No GoalSnapshot, task or timeline collection is created on the valid projection path.
    private sealed record GoalSimilarityFields(string Id, string Objective, GoalStatus Status,
        bool IsMetadataOnly, DateTimeOffset? CreatedAt, DateTimeOffset? TerminatedAt,
        [property: JsonConverter(typeof(SimilarityTaskShapeConverter))] SimilarityTaskShape? Tasks,
        [property: JsonConverter(typeof(TimelineLatestConverter))] TimelineLatest? Timeline)
    {
        [JsonIgnore] public SimilarityDomainFields Domain { get; } = new();
    }

    // Retain only cross-member validation facts; discarded text/spec payloads stay streamed.
    private sealed class SimilarityDomainFields
    {
        private bool? _blankSourceId;
        private bool _hasCoverage;
        private GoalExplicitDisposition? _disposition;
        private GoalStatus? _parkedStatus;
        private bool _invalidBriefChain, _invalidSpecChain;

        internal bool RequiresReconstruction => _invalidBriefChain || _invalidSpecChain ||
            _blankSourceId == true || _blankSourceId is null && _hasCoverage ||
            (_disposition?.Action == GoalExplicitDispositionAction.Park
                ? _parkedStatus is null or GoalStatus.Parked : _parkedStatus is not null);

        internal void Set(string member, object? value)
        {
            switch (member)
            {
                case nameof(GoalSnapshot.SourceBacklogItemId): _blankSourceId = (bool?)value; break;
                case nameof(GoalSnapshot.SourceBacklogCoverage): _hasCoverage = (bool)value!; break;
                case nameof(GoalSnapshot.ExplicitDisposition): _disposition = (GoalExplicitDisposition?)value; break;
                case nameof(GoalSnapshot.ParkedLifecycleStatus): _parkedStatus = (GoalStatus?)value; break;
                case nameof(GoalSnapshot.BriefVersions): _invalidBriefChain = (bool)value!; break;
                case nameof(GoalSnapshot.RefinedSpecVersions): _invalidSpecChain = (bool)value!; break;
            }
        }
    }

    private sealed record SimilarityVersionLink(int Version, int? SupersededByVersion);

    private static bool HasInvalidSimilarityVersionChain(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return false;
        var links = new List<SimilarityVersionLink>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var link = JsonSerializer.Deserialize<SimilarityVersionLink>(ref reader, options);
            if (link is null) return true;
            links.Add(link);
        }
        links.Sort((left, right) => left.Version.CompareTo(right.Version));
        for (var index = 0; index < links.Count; index++)
            if (links[index].Version != index + 1 ||
                links[index].SupersededByVersion != (index == links.Count - 1 ? (int?)null : index + 2)) return true;
        return false;
    }

    private static bool IsBlankSimilarityText(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return true;
        // Avoid allocating large ordinary descriptions just to reject blank text.
        if (reader.HasValueSequence || reader.ValueIsEscaped) return string.IsNullOrWhiteSpace(reader.GetString());
        foreach (var value in reader.ValueSpan)
        {
            if (value >= 128) return string.IsNullOrWhiteSpace(reader.GetString());
            if (!char.IsWhiteSpace((char)value)) return false;
        }
        return true;
    }

    private sealed record SimilarityTaskShape(int Count, bool RequiresReconstruction);

    private sealed class SimilarityTaskShapeConverter : JsonConverter<SimilarityTaskShape>
    {
        public SimilarityTaskShapeConverter() { }

        public override SimilarityTaskShape Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new InvalidSimilarityShapeException();
            var count = 0;
            var requiresReconstruction = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                count++;
                if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.Null))
                    throw new InvalidSimilarityShapeException();
                try
                {
                    var task = reader;
                    var blankDescription = true;
                    if (task.TokenType == JsonTokenType.StartObject)
                        while (task.Read() && task.TokenType != JsonTokenType.EndObject)
                        {
                            var description = task.ValueTextEquals("Description"u8);
                            task.Read();
                            if (description && task.TokenType is JsonTokenType.String or JsonTokenType.Null)
                                blankDescription = IsBlankSimilarityText(ref task);
                            task.Skip();
                        }
                    requiresReconstruction |= blankDescription;
                    ValidateTaskValue(ref reader, typeof(TaskSnapshot), options);
                }
                catch (Exception ex) when (IsSnapshotDeserializeException(ex))
                {
                    // Replay the original document to retain the standard member path,
                    // exception type and byte position, including nested task members.
                    throw new InvalidSimilarityShapeException();
                }
            }
            return new(count, requiresReconstruction);
        }

        internal static void ValidateTaskValue(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
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

        public override void Write(Utf8JsonWriter writer, SimilarityTaskShape value,
            JsonSerializerOptions options) => throw new NotSupportedException();
    }

    private sealed record TimelineLatest(DateTimeOffset? Latest, bool HasNullEntry);

    private sealed class TimelineLatestConverter : JsonConverter<TimelineLatest>
    {
        public TimelineLatestConverter() { }

        public override TimelineLatest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new InvalidSimilarityShapeException();
            DateTimeOffset? latest = null;
            var hasNullEntry = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    hasNullEntry = true;
                    continue;
                }
                if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidSimilarityShapeException();
                var validationReader = reader;
                try
                {
                    SimilarityTaskShapeConverter.ValidateTaskValue(ref validationReader, typeof(ProgressEventSnapshot), options);
                }
                catch (Exception ex) when (IsSnapshotDeserializeException(ex))
                {
                    throw new InvalidSimilarityShapeException();
                }
                var occurredAt = DateTimeOffset.MinValue;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var isTime = reader.ValueTextEquals("OccurredAt"u8);
                    reader.Read();
                    if (isTime) occurredAt = reader.GetDateTimeOffset();
                    else reader.Skip();
                }
                // Stable OrderBy(...).LastOrDefault() keeps the last equal instant,
                // including its original offset (which affects the rendered date).
                if (latest is null || occurredAt >= latest) latest = occurredAt;
            }
            return new(latest, hasNullEntry);
        }

        public override void Write(Utf8JsonWriter writer, TimelineLatest value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }
}
