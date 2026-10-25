using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerGoalReturnExplanation
{
    internal static IReadOnlyList<string> Lines(Goal goal, TimeZoneInfo zone)
    {
        var developer = goal.Tasks.LastOrDefault(task =>
            task.RequiredRole == AgentRole.Developer && task.LastDispatch is not null);
        if (developer?.LastDispatch is not { } dispatch) return [];

        var previous = goal.Tasks.Where(task => task.RequiredRole is AgentRole.Tester or AgentRole.Reviewer)
            .SelectMany(task => task.VerificationHistory.Select(check => (task.RequiredRole, Check: check)))
            .Where(item => item.Check.CompletedAt < dispatch.DispatchedAt)
            .OrderByDescending(item => item.Check.CompletedAt).FirstOrDefault();
        if (previous.Check is not { } check) return [];

        var receipt = developer.RetryAdmissionHistory.Where(item => item.RecordedAt >= check.CompletedAt)
            .OrderByDescending(item => item.RecordedAt).FirstOrDefault();
        // The shared parser can fall back to a log; restrict it to retained goal state.
        var retainedCheck = check with { StandardOutputPath = null };
        var findings = (WorkerResultBlockers.TryFindReviewFindingRound(retainedCheck, out var round, out _)
                ? round.Findings : [])
            .Concat(check.MergedReviewFindings ?? [])
            .Where(finding => finding.State == ReviewFindingState.Open).ToArray();
        var finding = findings.FirstOrDefault(item =>
            receipt?.StableFindingIds?.Contains(item.StableId, StringComparer.Ordinal) == true)
            ?? findings.FirstOrDefault();

        string Time(DateTimeOffset value) =>
            TimeZoneInfo.ConvertTime(value, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        var candidate = string.IsNullOrWhiteSpace(check.ReviewedCommit) ? ""
            : " on candidate " + check.ReviewedCommit[..Math.Min(8, check.ReviewedCommit.Length)];
        var lines = new List<string>
        {
            "Returned to Developer at " + Time(dispatch.DispatchedAt),
            $"  Previous check: {previous.RequiredRole} at {Time(check.CompletedAt)}{candidate}"
        };
        var hasReason = false;
        if (receipt is not null)
        {
            lines.Add($"  Cause: {receipt.Cause}, route {receipt.Route}");
            hasReason = true;
        }
        if (finding is not null && FirstLine(finding.Description) is { } description)
        {
            lines.Add($"  Finding {finding.StableId}: {description}");
            hasReason = true;
        }
        if (FirstLine(developer.CriterionRetryFeedback.FirstOrDefault()) is { } feedback)
        {
            lines.Add("  Criterion feedback: " + feedback);
            hasReason = true;
        }
        if (receipt?.EvidenceIdentities is { Count: > 0 } evidence)
            lines.Add("  Evidence: " + string.Join(", ", evidence.Take(3)));
        if (!hasReason) lines.Add("  Reason: not recorded in goal state");
        return lines;
    }

    private static string? FirstLine(string? text)
    {
        if (text is null) return null;
        var end = text.IndexOfAny(['\r', '\n']);
        var line = (end < 0 ? text : text[..end])
            .Replace('{', ' ').Replace('}', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }
}
