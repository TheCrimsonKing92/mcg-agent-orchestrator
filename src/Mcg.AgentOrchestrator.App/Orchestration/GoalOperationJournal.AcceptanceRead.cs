using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class GoalOperationJournal
{
    // Read the existing event copy without creating a store or scanning worker/journal files.
    // The goal and candidate pair restrict the query; only one bounded payload crosses the seam.
    internal static async Task<GoalOperationJournalEntry?> ReadNewestAcceptanceOutcomeAsync(
        string storePath, GoalId goalId, string candidate, string main, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var session = new SqliteReadSession(storePath);
        using var command = session.Connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN length(CAST(payload_json AS BLOB)) <= 1056000 THEN payload_json ELSE NULL END
            FROM run_events
            WHERE event_type = $type AND goal_id = $goal
              AND CASE WHEN json_valid(payload_json) THEN
                trim(json_extract(payload_json, '$.branchHeadSha')) = $candidate COLLATE NOCASE
                AND trim(json_extract(payload_json, '$.mainHeadSha')) = $main COLLATE NOCASE
                AND trim(coalesce(json_extract(payload_json, '$.acceptanceOutcome'), '')) <> ''
              ELSE 1 END
            ORDER BY julianday(occurred_at) DESC, seq DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$type", RunEventTypes.GoalOperation);
        command.Parameters.AddWithValue("$goal", goalId.Value);
        command.Parameters.AddWithValue("$candidate", candidate);
        command.Parameters.AddWithValue("$main", main);
        var payload = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (payload is null) return null;
        if (payload is not string text || JsonSerializer.Deserialize<GoalOperationJournalEntry>(text, JsonOptions) is not { } entry ||
            entry.GoalId != goalId || !entry.HasCandidate(candidate, main))
            throw new InvalidDataException("Acceptance event is oversized or has incompatible identity.");
        return NewestAcceptanceOutcomeForCandidate(new(storePath, [entry], [], []), candidate, main);
    }
}
