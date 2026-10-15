using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class IncompleteScopeRetryEvidence
{
    private static readonly Regex WorkerResultMarker = new(
        @"^[ \t]*WORKER_RESULT[ \t]*:?[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WorkerResultEnd = new(
        @"^[ \t]*END_WORKER_RESULT[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WorkerResultField = new(
        @"^[ \t]*(?:-[ \t]*)?(?<name>files|tests|blockers|assigned_scope_complete)[ \t]*:.*\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string BuildPreviousRoundAccount(string? output)
    {
        const string label = "Previous round's account:";
        var text = output ?? string.Empty;
        var markers = WorkerResultMarker.Matches(text);
        if (markers.Count == 0)
            return label + Environment.NewLine + "previous round account unavailable";

        var marker = markers[markers.Count - 1];
        var precedingText = text[..marker.Index];
        var previousEnds = WorkerResultEnd.Matches(precedingText);
        if (previousEnds.Count > 0)
            precedingText = precedingText[(previousEnds[previousEnds.Count - 1].Index +
                previousEnds[previousEnds.Count - 1].Length)..];
        var preceding = precedingText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var proseLines = preceding.Split('\n').ToList();
        while (proseLines.Count > 0 && string.IsNullOrWhiteSpace(proseLines[^1]))
            proseLines.RemoveAt(proseLines.Count - 1);
        var prose = string.Join(Environment.NewLine, proseLines.TakeLast(25));
        if (prose.Length > 2500)
            prose = prose[^2500..];

        var blockStart = marker.Index + marker.Length;
        var end = WorkerResultEnd.Match(text, blockStart);
        var block = text[blockStart..(end.Success ? end.Index : text.Length)];
        var fields = WorkerResultField.Matches(block)
            .Cast<Match>()
            .GroupBy(match => match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value.TrimEnd('\r'),
                StringComparer.OrdinalIgnoreCase);
        var lines = new List<string> { label };
        foreach (var name in new[] { "files", "tests", "blockers", "assigned_scope_complete" })
            if (fields.TryGetValue(name, out var line))
                lines.Add(line);
        if (prose.Length > 0)
            lines.Add(prose);
        return string.Join(Environment.NewLine, lines);
    }

    internal static (string ReceiptId, IReadOnlyList<string> FailingTestIdentities,
        IReadOnlyList<string>? TestResultPaths)? SelectNewestCandidateRed(
        IEnumerable<TaskVerificationRecord> verifications, string candidateSha)
    {
        var newest = verifications
            .SelectMany(verification => (verification.FindingEvidenceReceipts ?? [])
                .SelectMany(receipt => (receipt.Arms ?? [])
                    .Where(arm => arm.Arm == FindingEvidenceArm.Candidate &&
                        arm.Disposition == FindingEvidenceArmDisposition.Red &&
                        string.Equals(arm.Sha, candidateSha, StringComparison.OrdinalIgnoreCase))
                    .Select(arm => (verification.CompletedAt, receipt, arm))))
            .OrderByDescending(item => item.CompletedAt)
            .ThenBy(item => item.receipt.ReceiptId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (newest.receipt is null)
            return null;

        return (newest.receipt.ReceiptId, newest.arm.FailingTestIdentities ?? [], newest.arm.TestResultPaths);
    }
}
