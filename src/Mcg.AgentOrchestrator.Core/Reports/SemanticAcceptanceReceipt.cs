using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed record SemanticJudgeReceiptEntry(
    string Judge,
    bool Valid,
    bool CriteriaMet);

public sealed record SemanticAcceptanceReceipt(
    DateTimeOffset At,
    string GoalId,
    bool? Consensus,
    IReadOnlyList<SemanticJudgeReceiptEntry> Judges);

// Parses the append-only JSONL receipt log written by AppendSemanticAcceptanceReceipt.
// Each line is one JSON object; malformed lines are silently skipped.
public static class SemanticAcceptanceReceiptReader
{
    public static IReadOnlyList<SemanticAcceptanceReceipt> ParseAll(string jsonlText)
    {
        var results = new List<SemanticAcceptanceReceipt>();
        foreach (var line in jsonlText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                results.Add(ParseDocument(doc.RootElement));
            }
            catch { /* skip unparseable lines */ }
        }
        return results;
    }

    private static SemanticAcceptanceReceipt ParseDocument(JsonElement root)
    {
        var goalId = root.TryGetProperty("goalId", out var goalIdEl) && goalIdEl.ValueKind == JsonValueKind.String
            ? goalIdEl.GetString()!
            : string.Empty;

        DateTimeOffset at = DateTimeOffset.MinValue;
        if (root.TryGetProperty("at", out var atEl) && atEl.ValueKind == JsonValueKind.String)
            _ = DateTimeOffset.TryParse(atEl.GetString(), out at);

        bool? consensus = null;
        if (root.TryGetProperty("consensus", out var consensusEl))
        {
            consensus = consensusEl.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        var judges = new List<SemanticJudgeReceiptEntry>();
        if (root.TryGetProperty("judges", out var judgesEl) && judgesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var j in judgesEl.EnumerateArray())
            {
                var name = j.TryGetProperty("judge", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString()!
                    : string.Empty;
                var valid = j.TryGetProperty("valid", out var validEl) && validEl.ValueKind == JsonValueKind.True;
                var criteriaMet = j.TryGetProperty("criteriaMet", out var cmEl) && cmEl.ValueKind == JsonValueKind.True;
                judges.Add(new SemanticJudgeReceiptEntry(name, valid, criteriaMet));
            }
        }

        return new SemanticAcceptanceReceipt(at, goalId, consensus, judges);
    }
}
