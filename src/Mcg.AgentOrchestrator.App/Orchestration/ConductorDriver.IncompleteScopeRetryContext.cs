using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
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

    private IReadOnlyList<string> BuildCriterionRetryFeedback(
        Goal goal, TaskId targetTaskId, FailedGoalRecoveryDecision decision)
    {
        var feedback = new List<string>
        {
            decision.FeedbackCommand ?? throw new InvalidOperationException("Criterion retry command evidence is missing."),
            decision.FeedbackEvidence ?? throw new InvalidOperationException("Criterion retry failure evidence is missing.")
        };
        if (decision.DiscriminatingEvidence != "real-worker-or-command-failure")
            return feedback;
        var task = goal.Tasks.Single(task => task.Id == targetTaskId);
        var verification = task.LastVerification;
        if (task.RequiredRole != AgentRole.Developer || verification is null ||
            !TaskOutcomeClassifier.IsIncompleteScopeDeclaration(TaskOutcomeClassifier.TryExtractRule(
                DispatchFailureClassifier.Classify(task, verification).ClassifierReceipt)))
            return feedback;

        feedback.Add(BuildPreviousRoundAccount(verification.AuthoritativeStandardOutput ?? verification.StandardOutput));
        try
        {
            var detail = BuildCandidateFailureDetailEntry(goal);
            if (detail is not null)
                feedback.Add(detail);
        }
        catch (Exception)
        {
            // Evidence enrichment must not prevent the already-decided retry.
        }
        return feedback;
    }

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

    private string? BuildCandidateFailureDetailEntry(Goal goal)
    {
        var candidateSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (string.IsNullOrEmpty(candidateSha))
            return null;

        var newest = goal.Tasks
            .SelectMany(task => task.VerificationHistory)
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

        var rendered = AppendActionableCandidateRedFailureDetail(
            "Candidate failure detail:", newest.receipt.ReceiptId,
            newest.arm.FailingTestIdentities ?? [], newest.arm.TestResultPaths);
        return rendered.Contains("[FAIL] ", StringComparison.Ordinal) ? rendered : null;
    }
}
