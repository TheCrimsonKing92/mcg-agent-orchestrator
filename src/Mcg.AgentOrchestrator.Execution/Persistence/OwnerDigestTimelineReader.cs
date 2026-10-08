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

    public IReadOnlyList<OwnerDigestGoalInput> Read(string stateDatabasePath,
        IReadOnlyDictionary<string, DateTimeOffset> landedAt,
        DateTimeOffset since, DateTimeOffset until)
    {
        var landings = new Dictionary<string, DateTimeOffset>(landedAt, StringComparer.OrdinalIgnoreCase);
        using var connection = StateDbConnectionFactory.Open(stateDatabasePath, StateDbConnectionProfile.QueryOnlyRead);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT goals.id,
                CASE WHEN json_valid(snapshot_json)
                    THEN COALESCE(json_extract(snapshot_json, '$.Id'), goals.id)
                    ELSE goals.id END,
                evt.key, evt.type, evt.value
            FROM goals
            LEFT JOIN json_each(
                CASE WHEN json_valid(snapshot_json) THEN
                    CASE WHEN json_type(snapshot_json, '$.Timeline') = 'array'
                        AND COALESCE(json_extract(snapshot_json, '$.IsMetadataOnly'), 0) = 0
                        THEN snapshot_json END
                END, '$.Timeline') AS evt
            ORDER BY goals.rowid, CAST(evt.key AS INTEGER)
            """;
        using var reader = command.ExecuteReader();
        var inputs = new List<OwnerDigestGoalInput>();
        string? rowId = null;
        string? goalId = null;
        var events = new List<ProgressEvent>();
        var malformed = false;
        while (reader.Read())
        {
            var nextId = reader.GetString(0);
            if (nextId != rowId)
            {
                if (rowId is not null)
                    inputs.Add(new(goalId!, null, null, events.OrderBy(e => e.OccurredAt).ToArray()));
                rowId = nextId;
                goalId = reader.GetString(1);
                events.Clear();
                malformed = false;
            }
            if (malformed || reader.IsDBNull(2)) continue;
            try
            {
                if (reader.GetString(3) != "object")
                    throw new JsonException("Timeline entry must be an object.");
                var snapshot = JsonSerializer.Deserialize<ProgressEventSnapshot>(reader.GetString(4), SerializerOptions)
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
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                events.Clear();
                malformed = true;
            }
        }
        if (rowId is not null)
            inputs.Add(new(goalId!, null, null, events.OrderBy(e => e.OccurredAt).ToArray()));
        return inputs;
    }
}
