namespace Mcg.AgentOrchestrator.Core;

/// <summary>Candidate-bound focused evidence recorded before a Tester is dispatched.</summary>
public sealed record PreTesterEvidenceEntry(
    string Outcome,
    string CandidateSha,
    string ReceiptId,
    IReadOnlyList<string> Selections,
    IReadOnlyList<string> NotRun,
    string? ResultPath,
    IReadOnlyList<string> FailingTests);

public static class PreTesterEvidenceIndexLines
{
    private const string Prefix = "finding-evidence pre-tester ";
    private static string EncodeList(IReadOnlyList<string> values) =>
        string.Join(',', values.Select(Uri.EscapeDataString));

    public static string FormatMarker(PreTesterEvidenceEntry entry) =>
        Prefix + $"outcome={entry.Outcome}; candidate_sha={entry.CandidateSha}; " +
        $"receipt_id={entry.ReceiptId}; selections={EncodeList(entry.Selections)}; " +
        $"not_run={EncodeList(entry.NotRun)}; result_path={Uri.EscapeDataString(entry.ResultPath ?? "none")}; " +
        $"failing_tests={EncodeList(entry.FailingTests)}";

    public static PreTesterEvidenceEntry? Parse(string message)
    {
        if (!message.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var fields = message[Prefix.Length..].Split(';', StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (!fields.TryGetValue("candidate_sha", out var sha) ||
            !fields.TryGetValue("outcome", out var outcome)) return null;
        static string[] Values(IReadOnlyDictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var value) && value.Length > 0
                ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Uri.UnescapeDataString).ToArray()
                : [];
        fields.TryGetValue("receipt_id", out var receipt);
        fields.TryGetValue("result_path", out var path);
        return new PreTesterEvidenceEntry(
            outcome, sha, receipt ?? "none", Values(fields, "selections"),
            Values(fields, "not_run"), path == "none" ? null : Uri.UnescapeDataString(path ?? string.Empty),
            Values(fields, "failing_tests"));
    }

    public static PreTesterEvidenceEntry? Latest(Goal goal, TaskId testerTaskId, string? candidateSha)
    {
        if (string.IsNullOrWhiteSpace(candidateSha)) return null;
        foreach (var evt in goal.Timeline.Reverse())
        {
            if (evt.TaskId != testerTaskId ||
                evt.Kind is not (ProgressKind.FindingEvidenceRequestRecorded or ProgressKind.FindingEvidenceRunRecorded)) continue;
            if (Parse(evt.Message) is not { } entry ||
                !string.Equals(entry.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase)) continue;
            return entry;
        }
        return null;
    }

    public static IReadOnlyList<string> ForTester(Goal goal, TaskSpec task, string? candidateSha)
    {
        if (task.RequiredRole != AgentRole.Tester) return [];
        var entry = Latest(goal, task.Id, candidateSha);
        if (entry is null || entry.Outcome == "started") return [];
        var state = entry.Outcome == "green" ? "executed-on-candidate" : entry.Outcome;
        var lines = new List<string>
        {
            $"evidence_index: selection={string.Join(',', entry.Selections)}; verdict={entry.Outcome}; " +
            $"receipt={entry.ReceiptId}; state={state}; candidate_sha={entry.CandidateSha}; " +
            $"provenance=developer-deferred-pre-tester; not_run={string.Join(',', entry.NotRun)}; " +
            $"result_path={entry.ResultPath ?? "none"}"
        };
        if (entry.NotRun.Count > 0) lines.Add($"evidence_not_run: {string.Join(',', entry.NotRun)}");
        if (entry.FailingTests.Count > 0) lines.Add($"evidence_failing_tests: {string.Join(',', entry.FailingTests)}");
        return lines;
    }
}
