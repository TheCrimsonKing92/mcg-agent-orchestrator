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
                !TryParse(evt.Message, out var entry) || entry.DeveloperTaskId != developerTaskId ||
                !string.Equals(entry.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase)) continue;
            if (declaredClasses is null || MatchesDeclaration(entry, declaredClasses)) return entry;
        }
        return null;
    }

    // Decoding supplies a locator only. Store custody, canonical identity and evidence validity
    // must be established by the host reader before any result can be used as proof.
    public static bool TryParse(string message, out DeferredNoChangeEvidenceEntry entry)
    {
        entry = default!;
        if (!message.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in message[Prefix.Length..].Split(';', StringSplitOptions.TrimEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2 || !fields.TryAdd(pair[0], pair[1])) return false;
            }
            if (!fields.TryGetValue("task_id", out var task) || string.IsNullOrWhiteSpace(task) ||
                !fields.TryGetValue("candidate_sha", out var sha) || string.IsNullOrWhiteSpace(sha) ||
                !fields.TryGetValue("outcome", out var outcome) ||
                outcome is not ("started" or "green" or "red" or "unusable" or "interrupted") ||
                !fields.TryGetValue("receipt_id", out var receipt) ||
                !fields.TryGetValue("result_path", out var path)) return false;
            string[] Values(string key) => fields.TryGetValue(key, out var value) && value.Length > 0
                ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Uri.UnescapeDataString).ToArray() : [];
            entry = new(outcome, new TaskId(task), sha, receipt, Values("selections"),
                Values("not_run"), path == "none" ? null : Uri.UnescapeDataString(path),
                Values("failing_tests"), fields.ContainsKey("declared") ? Values("declared") : null);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return false;
        }
    }

    public static bool MatchesDeclaration(DeferredNoChangeEvidenceEntry entry, IReadOnlyList<string> declaredClasses)
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
