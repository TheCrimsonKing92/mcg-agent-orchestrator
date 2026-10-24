namespace Mcg.AgentOrchestrator.Core;

/// <summary>A terminal rerun receipt, bound to the original pre-Tester receipt and candidate.</summary>
public sealed record PreTesterOutOfDiffRerunMarker(
    string CandidateSha,
    string SourceReceipt,
    string RerunReceipt,
    string Rerun,
    IReadOnlyList<string> FailingTests,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FailureHistory,
    string Verdict)
{
    public const string Prefix = "finding-evidence pre-tester-out-of-diff-rerun ";
    public const string RequestPrefix = "finding-evidence pre-tester-out-of-diff-rerun-request ";

    public string HistoryText => string.Join(',', FailureHistory.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => Uri.EscapeDataString(pair.Key) + "@" +
            string.Join('+', pair.Value.Select(Uri.EscapeDataString))));

    public string Format() => Prefix + $"candidate_sha={CandidateSha}; source_receipt={SourceReceipt}; " +
        $"rerun_receipt={RerunReceipt}; rerun={Rerun}; " +
        $"failing_tests={string.Join(',', FailingTests.Select(Uri.EscapeDataString))}; " +
        $"failure_history={HistoryText}; verdict={Verdict}";

    public string FormatRequest() => RequestPrefix + Format()[Prefix.Length..];

    public static string DecideVerdict(string rerun, IReadOnlyList<string> failureKeys,
        IReadOnlyDictionary<string, IReadOnlyList<string>> history) =>
        rerun == "green" && failureKeys.Count > 0 && failureKeys.All(key =>
            history.TryGetValue(key, out var goals) && goals.Count > 0)
            ? "non-blocking" : "blocking";

    public static PreTesterOutOfDiffRerunMarker? Parse(string message)
    {
        if (!message.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in message[Prefix.Length..].Split(';', StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !fields.TryAdd(pair[0], pair[1])) return null;
        }
        if (new[] { "candidate_sha", "source_receipt", "rerun_receipt", "rerun", "failing_tests", "failure_history", "verdict" }
            .Any(key => !fields.ContainsKey(key))) return null;
        if (fields["verdict"] is not ("blocking" or "non-blocking") ||
            new[] { "candidate_sha", "source_receipt", "rerun_receipt", "rerun" }.Any(key => fields[key].Length == 0)) return null;
        var history = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var item in fields["failure_history"].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('@', 2);
            if (pair.Length != 2 || !history.TryAdd(Uri.UnescapeDataString(pair[0]),
                    pair[1].Split('+', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray())) return null;
        }
        return new(fields["candidate_sha"], fields["source_receipt"], fields["rerun_receipt"], fields["rerun"],
            fields["failing_tests"].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray(),
            history, fields["verdict"]);
    }

    public static PreTesterOutOfDiffRerunMarker? Latest(Goal goal, TaskId testerId, string candidateSha, string sourceReceipt) =>
        goal.Timeline.Reverse().Where(evt => evt.TaskId == testerId && evt.Kind == ProgressKind.FindingEvidenceRunRecorded)
            .Select(evt => Parse(evt.Message)).FirstOrDefault(marker => marker is not null &&
                string.Equals(marker.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                marker.SourceReceipt == sourceReceipt);

    public static PreTesterOutOfDiffRerunMarker? LatestRequest(Goal goal, TaskId testerId, string candidateSha, string sourceReceipt) =>
        goal.Timeline.Reverse().Where(evt => evt.TaskId == testerId && evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                evt.Message.StartsWith(RequestPrefix, StringComparison.Ordinal))
            .Select(evt => Parse(Prefix + evt.Message[RequestPrefix.Length..])).FirstOrDefault(marker => marker is not null &&
                string.Equals(marker.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                marker.SourceReceipt == sourceReceipt);
}
