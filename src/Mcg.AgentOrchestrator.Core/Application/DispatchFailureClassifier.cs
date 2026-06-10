namespace Mcg.AgentOrchestrator.Core;

public static class DispatchFailureClassifier
{
    public const int RecoverableSubscriptionLimitReviewThreshold = 2;

    public static bool IsRecoverableSubscriptionLimitFailure(TaskVerificationRecord verification)
    {
        var output = $"{verification.StandardOutput}\n{verification.StandardError}";
        return output.Contains("usage limit", StringComparison.OrdinalIgnoreCase) &&
            (output.Contains("try again", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("purchase more credits", StringComparison.OrdinalIgnoreCase));
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
        return HasRecoverableSubscriptionLimitHistory(task) &&
            CountRecoverableSubscriptionLimitFailures(task) >= RecoverableSubscriptionLimitReviewThreshold;
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
        if (!HasRecoverableSubscriptionLimitHistory(task))
        {
            return false;
        }

        if (task.SubscriptionRetryAfter is { } storedRetryAfter)
        {
            retryAfter = storedRetryAfter;
            return true;
        }

        return TryGetSubscriptionLimitRetryAfter(task.VerificationHistory.Last(), out retryAfter);
    }

    public static bool TryGetSubscriptionLimitRetryAfter(TaskVerificationRecord verification, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (!IsRecoverableSubscriptionLimitFailure(verification))
        {
            return false;
        }

        var output = $"{verification.StandardOutput}\n{verification.StandardError}";
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
}
