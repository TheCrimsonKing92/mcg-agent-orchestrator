using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed record PreReviewRepeatedTestDetail(string? AssertionMessage, string? StackTrace, string? UnavailableReason = null);

public static partial class PreReviewRepeatedFailureStatement
{
    public const int AssertionMessageMaxChars = 600;
    public const int StackFrameMaxChars = 300;
    public const int MaxDetailedTests = 10;
    public const string StartMarker = "<!-- PRE_REVIEW_REPEATED_FAILURE_START -->";
    public const string EndMarker = "<!-- PRE_REVIEW_REPEATED_FAILURE_END -->";

    public static string Format(
        PreReviewRepeatedFailureSummary summary,
        IReadOnlyDictionary<string, PreReviewRepeatedTestDetail> details)
    {
        if (!summary.HasRepeat)
            return string.Empty;

        var lines = new List<string>
        {
            StartMarker,
            $"The same failing-test set has failed {summary.ConsecutiveRounds} consecutive focused pre-review evidence rounds despite new Developer commits."
        };
        foreach (var name in summary.RepeatedTests.Take(MaxDetailedTests))
        {
            lines.Add($"Test: {name}");
            if (!details.TryGetValue(name, out var detail))
            {
                lines.Add("Assertion: detail unavailable: no matching result");
                lines.Add("Repository frame: detail unavailable: no matching result");
                continue;
            }
            if (detail.UnavailableReason is { Length: > 0 } reason)
            {
                lines.Add($"Assertion: detail unavailable: {reason}");
                lines.Add($"Repository frame: detail unavailable: {reason}");
                continue;
            }
            lines.Add($"Assertion: {Bound(detail.AssertionMessage ?? "detail unavailable: assertion missing", AssertionMessageMaxChars)}");
            lines.Add($"Repository frame: {Bound(SelectRepositoryFrame(detail.StackTrace), StackFrameMaxChars)}");
        }
        if (summary.RepeatedTests.Length > MaxDetailedTests)
        {
            var remaining = summary.RepeatedTests.Skip(MaxDetailedTests).ToArray();
            lines.Add($"{remaining.Length} additional repeated tests (names only): {string.Join(", ", remaining)}");
        }
        lines.Add(EndMarker);
        return string.Join(Environment.NewLine, lines);
    }

    public static string SelectRepositoryFrame(string? stackTrace)
    {
        foreach (var line in (stackTrace ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (RepositoryPathPattern().IsMatch(line) || line.Contains("Mcg.AgentOrchestrator.", StringComparison.Ordinal))
                return line.Trim();
        }
        return "no repository frame";
    }

    private static string Bound(string value, int limit)
    {
        var singleLine = WhitespacePattern().Replace(value, " ").Trim();
        return singleLine.Length <= limit ? singleLine : singleLine[..(limit - 1)] + "…";
    }

    [GeneratedRegex(@"(?:^|[\\/])(?:src|tests)[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex RepositoryPathPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
