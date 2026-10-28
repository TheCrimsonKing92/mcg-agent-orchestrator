using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOrchestratorStateRepository
{
    public async Task<IReadOnlyList<AcceptanceQueueFact>> ListAcceptanceQueueFactsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // Same facts as GoalSchedulingOrder, projected in SQLite rather than hydrating peer Goals.
        command.CommandText = """
            SELECT id,
              CASE WHEN COALESCE(json_extract(snapshot_json, '$.PriorityScheduling.GateOvertakes'), 0) >= 3
                THEN 'Critical' ELSE COALESCE(json_extract(snapshot_json, '$.PriorityScheduling.EffectiveLevel'),
                  json_extract(snapshot_json, '$.Priority'), 'Normal') END,
              COALESCE((SELECT MIN(json_extract(evt.value, '$.OccurredAt'))
                FROM json_each(goals.snapshot_json, '$.Timeline') evt
                WHERE json_extract(evt.value, '$.Kind') = 'GoalPolicyDecision'
                  AND instr(lower(json_extract(evt.value, '$.Message')), 'verified') > 0),
                (SELECT MAX(json_extract(task.value, '$.LastVerification.CompletedAt'))
                  FROM json_each(goals.snapshot_json, '$.Tasks') task),
                json_extract(snapshot_json, '$.Timeline[0].OccurredAt')),
              json_extract(snapshot_json, '$.Timeline[0].OccurredAt')
            FROM goals WHERE status = 'Verified' AND json_extract(snapshot_json, '$.CurrentHold') IS NULL
            """;
        var facts = new List<AcceptanceQueueFact>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            facts.Add(new(reader.GetString(0), GoalPriorities.ParseOrDefault(reader.GetString(1)),
                Instant(2), Instant(3)));
        return facts;

        DateTimeOffset Instant(int ordinal) => reader.IsDBNull(ordinal) ? DateTimeOffset.MinValue :
            DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
    }
}
