using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed record PreDispatchIntegrationReceipt(
    GoalId GoalId,
    TaskId TaskId,
    DateTimeOffset RetryAt,
    string OriginalCandidateSha,
    string IntegratedMainSha,
    string ResultingCandidateSha)
{
    public bool IsBoundTo(GoalId goalId, TaskSpec task) =>
        GoalId == goalId &&
        TaskId == task.Id &&
        RetryAt == task.LatestRetryAt &&
        task.PendingRetryCause == RetryCause.MainDriftConflict &&
        IsCommitSha(OriginalCandidateSha) &&
        IsCommitSha(IntegratedMainSha) &&
        IsCommitSha(ResultingCandidateSha) &&
        !string.Equals(OriginalCandidateSha, ResultingCandidateSha, StringComparison.OrdinalIgnoreCase);

    public bool Matches(GoalId? goalId, TaskSpec task, string? dispatchCandidateSha) =>
        goalId is { } currentGoalId &&
        IsBoundTo(currentGoalId, task) &&
        string.Equals(ResultingCandidateSha, dispatchCandidateSha, StringComparison.OrdinalIgnoreCase);

    public static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);
}

public static partial class FreshVerificationEvidence
{
    [GeneratedRegex(@"\b(?:total|executed|passed)\s*[:=]\s*(?<count>\d+)\b|\b(?<ratio>\d+)\s*/\s*\d+\b|\b(?<passed>\d+)\s+(?:tests?\s+)?passed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonZeroCountPattern();

    [GeneratedRegex(@"\bfailed\s*[:=]\s*(?<count>\d+)\b|\b(?<leading>\d+)\s+(?:tests?\s+)?failed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FailureCountPattern();

    public static bool HasNonZeroTestCount(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return false;
        }

        foreach (Match match in FailureCountPattern().Matches(evidence))
        {
            var value = match.Groups["count"].Success
                ? match.Groups["count"].Value
                : match.Groups["leading"].Value;
            if (!int.TryParse(value, out var failed) || failed > 0)
            {
                return false;
            }
        }

        var hasPositiveCount = false;
        foreach (Match match in NonZeroCountPattern().Matches(evidence))
        {
            var value = match.Groups["count"].Success
                ? match.Groups["count"].Value
                : match.Groups["ratio"].Success
                    ? match.Groups["ratio"].Value
                    : match.Groups["passed"].Value;
            if (!int.TryParse(value, out var count) || count == 0)
            {
                return false;
            }

            hasPositiveCount = true;
        }

        return hasPositiveCount;
    }
}
