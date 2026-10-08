namespace Mcg.AgentOrchestrator.Core;

public sealed record DeferredNoChangeEvidenceEntry(
    string Outcome,
    TaskId DeveloperTaskId,
    string CandidateSha,
    string ReceiptId,
    IReadOnlyList<string> Selections,
    IReadOnlyList<string> NotRun,
    string? ResultPath,
    IReadOnlyList<string> FailingTests,
    IReadOnlyList<string>? Declared = null);

public static class DeferredNoChangeEvidenceIndexLines
{
    private const string Prefix = "finding-evidence deferred-no-change ";

    public static string FormatMarker(DeferredNoChangeEvidenceEntry entry)
    {
        static string Encode(IReadOnlyList<string> values) =>
            string.Join(',', values.Select(Uri.EscapeDataString));
        return Prefix + $"outcome={entry.Outcome}; task_id={entry.DeveloperTaskId.Value}; " +
            $"candidate_sha={entry.CandidateSha}; receipt_id={entry.ReceiptId}; " +
            $"selections={Encode(entry.Selections)}; not_run={Encode(entry.NotRun)}; " +
            $"result_path={Uri.EscapeDataString(entry.ResultPath ?? "none")}; " +
            $"failing_tests={Encode(entry.FailingTests)}" +
            (entry.Declared is null ? string.Empty : $"; declared={Encode(entry.Declared)}");
    }

    public static DeferredNoChangeEvidenceEntry? Latest(Goal goal, TaskId developerTaskId, string candidateSha)
        => Latest(goal, developerTaskId, candidateSha, null);

    public static DeferredNoChangeEvidenceEntry? Latest(
        Goal goal, TaskId developerTaskId, string candidateSha, IReadOnlyList<string>? declaredClasses)
    {
        foreach (var evt in goal.Timeline.Reverse())
        {
            if (evt.Kind is not (ProgressKind.FindingEvidenceRequestRecorded or ProgressKind.FindingEvidenceRunRecorded) ||
                !evt.Message.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            var fields = evt.Message[Prefix.Length..].Split(';', StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            if (!fields.TryGetValue("task_id", out var taskId) ||
                !string.Equals(taskId, developerTaskId.Value, StringComparison.Ordinal) ||
                !fields.TryGetValue("candidate_sha", out var sha) ||
                !string.Equals(sha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
                !fields.TryGetValue("outcome", out var outcome)) continue;
            static string[] Values(IReadOnlyDictionary<string, string> values, string key) =>
                values.TryGetValue(key, out var value) && value.Length > 0
                    ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(Uri.UnescapeDataString).ToArray()
                    : [];
            fields.TryGetValue("receipt_id", out var receipt);
            fields.TryGetValue("result_path", out var path);
            var entry = new DeferredNoChangeEvidenceEntry(
                outcome, developerTaskId, sha, receipt ?? "none", Values(fields, "selections"),
                Values(fields, "not_run"), path == "none" ? null : Uri.UnescapeDataString(path ?? string.Empty),
                Values(fields, "failing_tests"), fields.ContainsKey("declared") ? Values(fields, "declared") : null);
            if (declaredClasses is null || MatchesDeclaration(entry, declaredClasses)) return entry;
        }
        return null;
    }

    private static bool MatchesDeclaration(DeferredNoChangeEvidenceEntry entry, IReadOnlyList<string> declaredClasses)
    {
        static HashSet<string> ClassSet(IEnumerable<string> names) => names.Select(name => name.Trim())
            .Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
        var declared = ClassSet(declaredClasses);
        // Preserve the coverage comparison for legacy lines that have no recorded declaration.
        return entry.Declared is not null
            ? ClassSet(entry.Declared).SetEquals(declared)
            : entry.Selections.Count == declared.Count && declared.All(name => entry.Selections.Any(selection =>
                selection.EndsWith(":" + name, StringComparison.OrdinalIgnoreCase)));
    }
}
