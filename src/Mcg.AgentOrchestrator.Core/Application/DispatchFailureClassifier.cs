using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public static class DispatchFailureClassifier
{
    public const int RecoverableSubscriptionLimitReviewThreshold = 2;

    private static readonly Regex PowerShellNativeErrorPrefix = new(
        "^[^:\\r\\n]{1,120}\\s+:\\s+(?<error>ERROR:|Error:|error:)",
        RegexOptions.CultureInvariant);

    public static bool IsRecoverableSubscriptionLimitFailure(TaskVerificationRecord verification)
    {
        return TryGetRecoverableSubscriptionLimitLine(verification, out _);
    }

    public static bool HasProviderNeutralProgressStallFailure(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Failed &&
            task.LastVerification is { Succeeded: false } latest &&
            IsProviderNeutralProgressStallFailure(latest);
    }

    public static bool IsProviderNeutralProgressStallFailure(TaskVerificationRecord verification)
    {
        var output = string.Join(
            Environment.NewLine,
            verification.StandardOutput,
            verification.StandardError);

        return output.Contains("no observable progress", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("stall timeout", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("heartbeat", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasRecoverableSubscriptionLimitHistory(TaskSpec task)
    {
        return task.Status is WorkTaskStatus.Assigned or WorkTaskStatus.Pending &&
            task.LastVerification is null &&
            task.VerificationHistory.LastOrDefault() is { Succeeded: false } latest &&
            IsRecoverableSubscriptionLimitFailure(latest);
    }

    public static int CountRecoverableSubscriptionLimitFailures(TaskSpec task)
    {
        return task.VerificationHistory.Count(verification =>
            !verification.Succeeded &&
            IsRecoverableSubscriptionLimitFailure(verification));
    }

    public static bool RequiresSubscriptionLimitReview(TaskSpec task)
    {
        var failureCount = CountRecoverableSubscriptionLimitFailures(task);
        return HasRecoverableSubscriptionLimitHistory(task) &&
            failureCount >= RecoverableSubscriptionLimitReviewThreshold &&
            task.SubscriptionLimitReviewedFailureCount < failureCount;
    }

    public static bool IsSubscriptionRetryDeferred(TaskSpec task, DateTimeOffset now, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (!TryGetSubscriptionLimitRetryAfter(task, out retryAfter))
        {
            return false;
        }

        return retryAfter > now;
    }

    public static bool TryGetSubscriptionLimitRetryAfter(TaskSpec task, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (task.VerificationHistory.LastOrDefault() is not { Succeeded: false } latest ||
            !IsRecoverableSubscriptionLimitFailure(latest))
        {
            return false;
        }

        if (task.SubscriptionRetryAfter is { } storedRetryAfter)
        {
            retryAfter = storedRetryAfter;
            return true;
        }

        return TryGetSubscriptionLimitRetryAfter(latest, out retryAfter);
    }

    public static bool TryGetSubscriptionLimitRetryAfter(TaskVerificationRecord verification, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (!TryGetRecoverableSubscriptionLimitLine(verification, out var output))
        {
            return false;
        }

        var marker = "try again at ";
        var markerIndex = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var start = markerIndex + marker.Length;
        var remaining = output[start..].TrimStart();
        var token = string.Join(
            " ",
            remaining
                .Split([' ', '\r', '\n', '\t', '.', ','], StringSplitOptions.RemoveEmptyEntries)
                .Take(2));

        if (!TimeOnly.TryParse(token, out var time))
        {
            return false;
        }

        var basis = verification.CompletedAt;
        retryAfter = new DateTimeOffset(
            basis.Year,
            basis.Month,
            basis.Day,
            time.Hour,
            time.Minute,
            0,
            basis.Offset);

        if (retryAfter <= basis)
        {
            retryAfter = retryAfter.AddDays(1);
        }

        return true;
    }

    private static bool TryGetRecoverableSubscriptionLimitLine(TaskVerificationRecord verification, out string line)
    {
        foreach (var candidate in GetProviderErrorLines(verification.StandardOutput))
        {
            if (IsRecoverableSubscriptionLimitText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        foreach (var candidate in GetProviderErrorLines(verification.StandardError))
        {
            if (IsRecoverableSubscriptionLimitText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    private static bool IsRecoverableSubscriptionLimitText(string text)
    {
        return text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("try again", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("purchase more credits", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> GetProviderErrorLines(string output)
    {
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (IsProviderErrorLine(line))
            {
                yield return line;
                continue;
            }

            var powerShellError = PowerShellNativeErrorPrefix.Match(line);
            if (powerShellError.Success)
            {
                yield return line[powerShellError.Groups["error"].Index..];
            }
        }
    }

    private static bool IsProviderErrorLine(string line)
    {
        return line.StartsWith("ERROR:", StringComparison.Ordinal) ||
            line.StartsWith("Error:", StringComparison.Ordinal) ||
            line.StartsWith("error:", StringComparison.Ordinal);
    }
}
