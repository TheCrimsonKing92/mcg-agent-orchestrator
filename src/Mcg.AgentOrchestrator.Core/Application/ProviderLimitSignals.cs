using System.Text.RegularExpressions;
using static Mcg.AgentOrchestrator.Core.DispatchFailureClassifier;

namespace Mcg.AgentOrchestrator.Core;

public static class ProviderLimitSignals
{
    private static readonly TimeSpan BareClockRetryStalenessTolerance = TimeSpan.FromHours(1);

    private static readonly Regex TryAgainInPattern = new(
        @"\btry again in\s+(?<value>\d+)\s*(?<unit>ms|milliseconds?|s|sec|secs|seconds?|m|mins?|minutes?|h|hrs?|hours?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ResetsInPattern = new(
        @"\bresets?\s+in\s*:?\s*(?:(?<hours>\d+)\s*h(?:ours?)?)?\s*(?:(?<minutes>\d+)\s*m(?:in(?:ute)?s?)?)?\s*(?:(?<seconds>\d+)\s*s(?:ec(?:ond)?s?)?)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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

    public static bool TryGetProviderSubscriptionCooldown(
        Goal goal,
        TaskId candidateTaskId,
        string providerName,
        DateTimeOffset now,
        out ProviderSubscriptionCooldown cooldown)
    {
        cooldown = default!;
        foreach (var task in goal.Tasks)
        {
            // Provider authority belongs to the failed dispatch, independently of the task's
            // current assignment, latest dispatch or task-local deferral release.
            foreach (var verification in task.VerificationHistory)
                if ((task.Id != candidateTaskId ||
                     task.SubscriptionDeferralReleasedAt is { } releasedAt && verification.CompletedAt <= releasedAt) &&
                    TryGetSubscriptionCooldown(task, verification, now, out var observed) &&
                    observed.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) &&
                    (cooldown is null || observed.RetryAfter > cooldown.RetryAfter))
                    cooldown = observed;
        }

        return cooldown is not null;
    }

    public static bool TryGetTaskSubscriptionCooldown(
        TaskSpec task, DateTimeOffset now, out ProviderSubscriptionCooldown cooldown)
    {
        cooldown = default!;
        return IsSubscriptionRetryDeferred(task, now, out var retryAfter) &&
            task.VerificationHistory.LastOrDefault() is { } verification &&
            TryGetSubscriptionCooldown(task, verification, now, out cooldown) &&
            cooldown.RetryAfter == retryAfter;
    }

    private static bool TryGetSubscriptionCooldown(TaskSpec task, TaskVerificationRecord verification,
        DateTimeOffset now, out ProviderSubscriptionCooldown cooldown)
    {
        cooldown = default!;
        if (verification.Succeeded || !IsRecoverableSubscriptionLimitFailure(verification) ||
            !TryGetSubscriptionLimitRetryAfter(verification, out var retryAfter) || retryAfter <= now)
            return false;
        var dispatch = FindDispatchForVerification(task, verification);
        if (dispatch?.ProviderName is not { Length: > 0 } providerName ||
            !IsSubscriptionProviderCliDispatch(dispatch) ||
            (verification.DispatchStartedAt is { } startedAt && dispatch.DispatchedAt != startedAt))
            return false;
        cooldown = new(providerName, task.Id, retryAfter)
        {
            BindingKey = BuildProviderBindingKey(providerName, dispatch.ClaudeCredentialSourceDirectory).Key,
            EvidenceReceipt = !string.IsNullOrWhiteSpace(verification.StandardErrorPath)
                ? verification.StandardErrorPath : $"verification:{verification.CompletedAt:O}",
            DispatchStartedAt = dispatch.DispatchedAt
        };
        return true;
    }

    public static bool TryGetProviderBudgetExhaustionHold(
        IEnumerable<Goal> goals,
        string providerName,
        string? credentialBinding,
        out ProviderBudgetExhaustionHold hold)
    {
        ArgumentNullException.ThrowIfNull(goals);
        var goalList = goals.ToArray();
        var requestedBinding = BuildProviderBindingKey(providerName, credentialBinding);
        var observations = new List<(
            Goal Goal,
            TaskSpec Task,
            TaskVerificationRecord Verification,
            string ProviderName,
            ProviderBindingKey Binding)>();
        ProviderBudgetExhaustionHold? latest = null;

        foreach (var goal in goalList)
        {
            foreach (var task in goal.Tasks)
            {
                foreach (var verification in task.VerificationHistory)
                {
                    var dispatch = FindDispatchForVerification(task, verification);
                    if (dispatch is null)
                    {
                        continue;
                    }

                    var observedBinding = BuildProviderBindingKey(
                        dispatch.ProviderName ?? dispatch.WorkerName,
                        dispatch.ClaudeCredentialSourceDirectory);
                    observations.Add((
                        goal,
                        task,
                        verification,
                        dispatch.ProviderName ?? dispatch.WorkerName,
                        observedBinding));
                }
            }
        }

        var latestAvailabilityAt = observations
            .Where(observation =>
                observation.Binding.Equals(requestedBinding) &&
                observation.Verification.Succeeded)
            .Select(observation => (DateTimeOffset?)observation.Verification.CompletedAt)
            .Max();

        foreach (var observation in observations)
        {
            var (goal, task, verification, observedProviderName, observedBinding) = observation;
            if (verification.ProviderFailureKind != ProviderFailureKind.BudgetExhausted ||
                !observedBinding.Equals(requestedBinding) ||
                HasExplicitBudgetRecoveryForBinding(task, observedBinding, verification.CompletedAt) ||
                latestAvailabilityAt > verification.CompletedAt)
            {
                continue;
            }

            var receipt = !string.IsNullOrWhiteSpace(verification.StandardErrorPath)
                ? verification.StandardErrorPath!
                : $"verification:{verification.CompletedAt:O}";
            var candidate = new ProviderBudgetExhaustionHold(
                observedProviderName,
                observedBinding.Key,
                observedBinding.Scope,
                goal.Id,
                task.Id,
                verification.CompletedAt,
                receipt);
            if (latest is null || candidate.PlacedAt > latest.PlacedAt)
            {
                latest = candidate;
            }
        }

        hold = latest!;
        return latest is not null;
    }

    private static bool HasExplicitBudgetRecoveryForBinding(
        TaskSpec task,
        ProviderBindingKey observedBinding,
        DateTimeOffset exhaustedAt)
    {
        if (task.LatestProviderBudgetRecoveryAt is not { } recoveredAt || recoveredAt <= exhaustedAt)
        {
            return false;
        }

        var recoveredDispatch = task.DispatchHistory
            .LastOrDefault(dispatch => dispatch.DispatchedAt <= recoveredAt);
        return recoveredDispatch is not null &&
            BuildProviderBindingKey(
                recoveredDispatch.ProviderName ?? recoveredDispatch.WorkerName,
                recoveredDispatch.ClaudeCredentialSourceDirectory)
            .Equals(observedBinding);
    }

    public static bool TryGetSubscriptionLimitRetryAfter(TaskSpec task, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (task.SubscriptionRetryAfter is { } storedRetryAfter)
        {
            retryAfter = storedRetryAfter;
            return true;
        }

        if (task.VerificationHistory.LastOrDefault() is not { Succeeded: false } latest ||
            !IsRecoverableSubscriptionLimitFailure(latest))
        {
            return false;
        }

        if (task.SubscriptionDeferralReleasedAt is { } releasedAt && latest.CompletedAt <= releasedAt)
        {
            return false;
        }

        // RetryTask is the operator's explicit reset boundary. Keep the historical receipt, but do not
        // let a verification from before that boundary regenerate a deferral after the stored value was cleared.
        if (task.LatestRetryAt is { } latestRetryAt &&
            latest.CompletedAt <= latestRetryAt &&
            task.PendingRetryCause != RetryCause.ProviderInterruption)
        {
            return false;
        }

        return TryGetSubscriptionLimitRetryAfter(latest, out retryAfter);
    }

    public static string DescribeSubscriptionRetrySource(TaskSpec task)
    {
        if (task.SubscriptionRetryAfter is not null)
        {
            return "stored task field SubscriptionRetryAfter";
        }

        if (task.VerificationHistory.LastOrDefault() is { } latest)
        {
            var recordNumber = task.VerificationHistory.Count;
            return $"verification history record {recordNumber} of {recordNumber}, completed {latest.CompletedAt:u}";
        }

        return "retry source unavailable";
    }

    public static bool TryGetSubscriptionLimitRetryAfter(TaskVerificationRecord verification, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (IsDirtyDispatchGuardFailure(verification) ||
            !TryGetRecoverableSubscriptionLimitLine(verification, out var output))
        {
            return false;
        }

        if (TryGetRelativeRetryAfter(output, out var retryAfterDelay))
        {
            retryAfter = verification.CompletedAt.Add(retryAfterDelay);
            return true;
        }

        if (TryGetAbsoluteResetRetryAfter(output, verification.CompletedAt, out retryAfter))
        {
            return true;
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

        // Provider notices are emitted before the worker exits. A clock time shortly before completion
        // therefore names an already-expired same-day window, while a substantially earlier time still
        // denotes the next occurrence of that clock time.
        if (retryAfter <= basis && basis - retryAfter > BareClockRetryStalenessTolerance)
        {
            retryAfter = retryAfter.AddDays(1);
        }

        return true;
    }

    internal static bool TryGetProviderBudgetExhaustionLine(TaskVerificationRecord verification, out string line) =>
        TryGetProviderBudgetExhaustionLine(verification, out line, out _);

    internal static bool TryGetProviderBudgetExhaustionLine(
        TaskVerificationRecord verification,
        out string line,
        out string source)
    {
        source = "stderr";
        if (ProviderLimitEvidenceParser.TryGetBudgetExhaustionEvidenceLine(
            EnumerateEvidenceLines(verification, includeStandardOutput: false, includeStandardError: true),
            out line))
        {
            return true;
        }

        if (ProviderLimitEvidenceParser.TryGetCliCreditExhaustionLine(
            verification.ExitCode,
            EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: false),
            out line))
        {
            source = "stdout";
            return true;
        }

        return false;
    }

    internal static bool TryGetRelativeRetryAfter(string output, out TimeSpan retryAfter)
    {
        var tryAgainMatch = TryAgainInPattern.Match(output);
        if (tryAgainMatch.Success &&
            int.TryParse(tryAgainMatch.Groups["value"].Value, out var retryValue))
        {
            retryAfter = ToTimeSpan(retryValue, tryAgainMatch.Groups["unit"].Value);
            return retryAfter > TimeSpan.Zero;
        }

        var resetsMatch = ResetsInPattern.Match(output);
        if (resetsMatch.Success)
        {
            var hours = ParseIntGroup(resetsMatch, "hours");
            var minutes = ParseIntGroup(resetsMatch, "minutes");
            var seconds = ParseIntGroup(resetsMatch, "seconds");
            retryAfter = new TimeSpan(hours, minutes, seconds);
            return retryAfter > TimeSpan.Zero;
        }

        retryAfter = default;
        return false;
    }

    private static int ParseIntGroup(Match match, string groupName) =>
        match.Groups[groupName].Success && int.TryParse(match.Groups[groupName].Value, out var value)
            ? value
            : 0;

    private static TimeSpan ToTimeSpan(int value, string unit)
    {
        if (unit.StartsWith("ms", StringComparison.OrdinalIgnoreCase) ||
            unit.StartsWith("millisecond", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromMilliseconds(value);
        }

        if (unit.StartsWith("m", StringComparison.OrdinalIgnoreCase) &&
            !unit.StartsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromMinutes(value);
        }

        if (unit.StartsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromHours(value);
        }

        return TimeSpan.FromSeconds(value);
    }

    private static bool TryGetAbsoluteResetRetryAfter(
        string line, DateTimeOffset completedAt, out DateTimeOffset retryAfter) =>
        TryGetCliAbsoluteResetRetryAfter(line, completedAt, out retryAfter);
}
