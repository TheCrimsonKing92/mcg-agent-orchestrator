using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class OwnerDigestTimelineReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public int RowsRead { get; private set; }
    public long TimelineCharsRead { get; private set; }

    public IReadOnlyList<OwnerDigestGoalInput> Read(string stateDatabasePath,
        IReadOnlyDictionary<string, DateTimeOffset> landedAt,
        DateTimeOffset since, DateTimeOffset until)
    {
        RowsRead = 0;
        TimelineCharsRead = 0;
        var landings = new Dictionary<string, DateTimeOffset>(landedAt, StringComparer.OrdinalIgnoreCase);
        using var connection = StateDbConnectionFactory.Open(stateDatabasePath, StateDbConnectionProfile.QueryOnlyRead);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT goals.id,
                CASE WHEN json_valid(snapshot_json)
                    THEN COALESCE(json_extract(snapshot_json, '$.Id'), goals.id)
                    ELSE goals.id END,
                CASE WHEN json_valid(snapshot_json) THEN
                    CASE WHEN json_type(snapshot_json, '$.Timeline') = 'array'
                        AND COALESCE(json_extract(snapshot_json, '$.IsMetadataOnly'), 0) = 0
                        THEN json_extract(snapshot_json, '$.Timeline') END END
            FROM goals
            ORDER BY goals.rowid
            """;
        using var reader = command.ExecuteReader();
        var inputs = new List<OwnerDigestGoalInput>();
        while (reader.Read())
        {
            RowsRead++;
            var goalId = reader.GetString(1);
            var events = new List<ProgressEvent>();
            if (!reader.IsDBNull(2))
            {
                var timeline = reader.GetString(2);
                TimelineCharsRead += timeline.Length;
                try
                {
                    using var document = JsonDocument.Parse(timeline);
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.Object)
                            throw new JsonException("Timeline entry must be an object.");
                        var snapshot = JsonSerializer.Deserialize<ProgressEventSnapshot>(element, SerializerOptions)
                            ?? throw new JsonException("Timeline entry is missing.");
                        var evt = new ProgressEvent(new GoalId(snapshot.GoalId),
                            snapshot.TaskId is null ? null : new TaskId(snapshot.TaskId),
                            snapshot.Kind, snapshot.Message, snapshot.OccurredAt, snapshot.RequeueSkipped,
                            snapshot.OperatorGates, snapshot.OperatorIntentApplied, snapshot.HumanInputSuperseded,
                            snapshot.TickOutcome);
                        // Latest decisions use window events even when they occur after a landing.
                        if ((landings.TryGetValue(goalId!, out var landing) && evt.OccurredAt <= landing) ||
                            (evt.OccurredAt >= since && evt.OccurredAt < until))
                            events.Add(evt);
                    }
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
                {
                    events.Clear();
                }
            }
            inputs.Add(new(goalId, null, null, events.OrderBy(e => e.OccurredAt).ToArray()));
        }
        return inputs;
    }
}
