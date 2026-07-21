using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AutoReviewRetryConvergenceBriefBuilder
{
    internal const string AcceptedShapePreamble =
        "the existing implementation is accepted in shape — do NOT rewrite it; close only the residual blockers below";

    internal const string RerunMandate =
        "rerun the focused test classes at your final commit and quote receipts";

    private const string ReviewerBlockerPrefix = "Reviewer WORKER_RESULT reported blocker:";
    private const string WorkerResultBlockerPrefix = "WORKER_RESULT reported blocker:";

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingPriorityPattern = new(@"^\s*P\d+\s*[:.)-]?\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RoundReferencePattern = new(@"\bround\s+\d+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LocationPattern = new(
        @"(?<location>(?:[A-Za-z]:\\|\.{0,2}[/\\])?[\w .-]+(?:[/\\][\w .-]+)*[/\\][\w .-]+\.[A-Za-z0-9]+(?::\d+)?|[\w.-]+\.[A-Za-z0-9]+(?::\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string BuildConvergenceBrief(
        Goal goal,
        TaskSpec triggeringTask,
        string currentFinding,
        int round,
        string outputArtifact)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(triggeringTask);

        var findings = CollectFindings(goal, triggeringTask, currentFinding, round);
        var builder = new StringBuilder();
        builder.AppendLine(AcceptedShapePreamble);
        builder.AppendLine();
        builder.AppendLine("Residual blockers:");

        if (findings.Count == 0)
        {
            builder.AppendLine("- no structured Reviewer/Tester blocker text was recorded; inspect the latest verifier output.");
        }
        else
        {
            foreach (var finding in findings)
            {
                builder.Append("- ");
                builder.Append(finding.Text);
                if (finding.Rounds.Count > 1)
                {
                    builder.Append(" (seen in rounds ");
                    builder.Append(string.Join(", ", finding.Rounds));
                    builder.Append(')');
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine();
        builder.Append(RerunMandate);
        if (!string.IsNullOrWhiteSpace(outputArtifact))
        {
            builder.Append("; latest verifier output: ");
            builder.Append(outputArtifact.Trim());
        }

        return builder.ToString();
    }

    private static IReadOnlyList<CollapsedFinding> CollectFindings(
        Goal goal,
        TaskSpec triggeringTask,
        string currentFinding,
        int currentRound)
    {
        var findings = new Dictionary<string, FindingAccumulator>(StringComparer.Ordinal);
        var timelineRound = 1;

        foreach (var evt in goal.Timeline)
        {
            if (IsAutoReviewRetry(evt))
            {
                timelineRound++;
                continue;
            }

            if (IsReviewerOrTesterBlocker(goal, evt))
            {
                AddFinding(findings, ExtractBlocker(evt.Message), timelineRound);
            }
        }

        if (!string.IsNullOrWhiteSpace(currentFinding) &&
            !goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.TaskFailed &&
                evt.TaskId == triggeringTask.Id &&
                string.Equals(ExtractBlocker(evt.Message), currentFinding.Trim(), StringComparison.Ordinal)))
        {
            AddFinding(findings, currentFinding, currentRound);
        }

        return findings.Values
            .OrderBy(item => item.FirstSeenOrder)
            .Select(item => new CollapsedFinding(item.LatestText, item.Rounds.Order().ToArray()))
            .ToArray();
    }

    private static bool IsAutoReviewRetry(ProgressEvent evt) =>
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase);

    private static bool IsReviewerOrTesterBlocker(Goal goal, ProgressEvent evt)
    {
        if (evt.Kind != ProgressKind.TaskFailed ||
            evt.TaskId is not { } taskId ||
            goal.Tasks.FirstOrDefault(task => task.Id == taskId)?.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester))
        {
            return false;
        }

        return evt.Message.StartsWith(ReviewerBlockerPrefix, StringComparison.Ordinal) ||
            evt.Message.StartsWith(WorkerResultBlockerPrefix, StringComparison.Ordinal);
    }

    private static string ExtractBlocker(string message)
    {
        if (message.StartsWith(ReviewerBlockerPrefix, StringComparison.Ordinal))
        {
            return message[ReviewerBlockerPrefix.Length..].Trim();
        }

        return message.StartsWith(WorkerResultBlockerPrefix, StringComparison.Ordinal)
            ? message[WorkerResultBlockerPrefix.Length..].Trim()
            : message.Trim();
    }

    private static void AddFinding(Dictionary<string, FindingAccumulator> findings, string text, int round)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var key = BuildDedupKey(trimmed);
        if (!findings.TryGetValue(key, out var item))
        {
            item = new FindingAccumulator(findings.Count, trimmed);
            findings.Add(key, item);
        }

        item.LatestText = trimmed;
        item.Rounds.Add(round);
    }

    private static string BuildDedupKey(string text)
    {
        var location = ExtractLocation(text);
        var normalized = NormalizeFindingText(text);
        return location.Length == 0 ? normalized : location + "|" + normalized;
    }

    private static string ExtractLocation(string text)
    {
        var match = LocationPattern.Match(text);
        if (!match.Success)
        {
            return string.Empty;
        }

        return match.Groups["location"].Value
            .Replace('\\', '/')
            .Trim()
            .ToLowerInvariant();
    }

    private static string NormalizeFindingText(string text)
    {
        var normalized = text.Trim().ToLower(CultureInfo.InvariantCulture);
        normalized = LocationPattern.Replace(normalized, string.Empty);
        normalized = RoundReferencePattern.Replace(normalized, string.Empty);
        normalized = LeadingPriorityPattern.Replace(normalized, string.Empty);
        normalized = normalized.Replace('-', ' ');
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}\s]", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\b(the|a|an|still|remains?|leaves?)\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = WhitespacePattern.Replace(normalized, " ").Trim();
        return normalized;
    }

    private sealed class FindingAccumulator(int firstSeenOrder, string latestText)
    {
        public int FirstSeenOrder { get; } = firstSeenOrder;
        public string LatestText { get; set; } = latestText;
        public HashSet<int> Rounds { get; } = [];
    }

    private sealed record CollapsedFinding(string Text, IReadOnlyList<int> Rounds);
}
