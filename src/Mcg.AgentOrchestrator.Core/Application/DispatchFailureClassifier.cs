using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed record ProviderSubscriptionCooldown(
    string ProviderName,
    TaskId SourceTaskId,
    DateTimeOffset RetryAfter);

public static class DispatchFailureClassifier
{
    public const int RecoverableSubscriptionLimitReviewThreshold = 2;

    private static readonly Regex PowerShellNativeErrorPrefix = new(
        "^[^:\\r\\n]{1,120}\\s+:\\s+(?<error>ERROR:|Error:|error:)",
        RegexOptions.CultureInvariant);
    private static readonly Regex PassedCountPattern = new(
        @"\bPassed:\s*[1-9]\d*\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FractionPattern = new(
        @"\b\d+\s*/\s*\d+\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex ExitCodeZeroPattern = new(
        @"\bexit code\s*0\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsRecoverableSubscriptionLimitFailure(TaskVerificationRecord verification)
    {
        return TryGetRecoverableSubscriptionLimitLine(verification, out _);
    }

    public static bool TryBuildDirtyDispatchRecovery(TaskSpec task, out DirtyDispatchRecovery recovery)
    {
        recovery = DirtyDispatchRecovery.None;
        if (task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester) ||
            task.LastVerification is not { Succeeded: false } verification ||
            !IsDirtyDispatchGuardFailure(verification))
        {
            return false;
        }

        var changedFiles = ExtractChangedFiles(verification.StandardError);
        var verificationEvidence = ExtractVerificationEvidence(verification.StandardOutput, verification.StandardError);
        var label = verificationEvidence.Length > 0 ? "dirty-useful" : "dirty-unverified";
        recovery = new DirtyDispatchRecovery(
            label,
            changedFiles,
            verificationEvidence,
            verification.WorkingDirectory);
        return true;
    }

    public static bool HasVerificationEvidence(string standardOutput, string standardError)
    {
        var output = $"{standardOutput}\n{standardError}";
        if (output.Contains("test run successful", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hasVerificationTerm =
            output.Contains("test", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("suite", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("verification", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("smoke", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("check", StringComparison.OrdinalIgnoreCase);

        return hasVerificationTerm &&
            (PassedCountPattern.IsMatch(output) ||
             FractionPattern.IsMatch(output) ||
             ExitCodeZeroPattern.IsMatch(output));
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

    public static bool HasRecoverableProviderConnectivityFailure(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Failed &&
            IsSubscriptionProviderCliDispatch(task) &&
            task.LastVerification is { Succeeded: false } latest &&
            IsRecoverableProviderConnectivityFailure(latest);
    }

    public static bool HasRecoverableProviderModelRejectionFailure(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Failed &&
            IsSubscriptionProviderCliDispatch(task) &&
            task.LastVerification is { Succeeded: false } latest &&
            IsRecoverableProviderModelRejectionFailure(latest);
    }

    public static int CountRecoverableProviderConnectivityFailures(TaskSpec task)
    {
        return task.VerificationHistory.Count(verification =>
            !verification.Succeeded &&
            IsRecoverableProviderConnectivityFailure(verification));
    }

    public static bool IsRecoverableProviderConnectivityFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        var output = string.Join(
            Environment.NewLine,
            verification.StandardOutput,
            verification.StandardError);

        return ContainsRecoverableProviderConnectivityText(output) &&
            !HasUsefulPreWorkOutput(verification.StandardOutput);
    }

    public static bool IsRecoverableProviderModelRejectionFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        var output = string.Join(
            Environment.NewLine,
            verification.StandardOutput,
            verification.StandardError);

        return ContainsProviderModelRejectionText(output) &&
            !HasUsefulPreWorkOutput(verification.StandardOutput);
    }

    public static int CountRecoverableProviderModelRejectionFailures(TaskSpec task)
    {
        return task.VerificationHistory.Count(verification =>
            !verification.Succeeded &&
            IsRecoverableProviderModelRejectionFailure(verification));
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
            if (task.Id == candidateTaskId ||
                task.LastDispatch is not { ProviderName: { Length: > 0 } dispatchProviderName } ||
                !dispatchProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) ||
                !IsSubscriptionRetryDeferred(task, now, out var retryAfter))
            {
                continue;
            }

            if (cooldown is null || retryAfter > cooldown.RetryAfter)
            {
                cooldown = new ProviderSubscriptionCooldown(dispatchProviderName, task.Id, retryAfter);
            }
        }

        return cooldown is not null;
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

    private static bool IsSubscriptionProviderCliDispatch(TaskSpec task)
    {
        var dispatch = task.LastDispatch;
        return ContainsSubscriptionProviderCliName(dispatch?.WorkerName) ||
            ContainsSubscriptionProviderCliName(dispatch?.Command) ||
            ContainsSubscriptionProviderCliName(task.LastVerification?.Command);
    }

    private static bool ContainsSubscriptionProviderCliName(string? text)
    {
        return text?.Contains("codex-cli", StringComparison.OrdinalIgnoreCase) == true ||
            text?.Contains("claude-cli", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool ContainsRecoverableProviderConnectivityText(string text)
    {
        return (text.Contains("websocket", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("os error", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("10013", StringComparison.OrdinalIgnoreCase))) ||
            text.Contains("Unable to connect to API", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ConnectionRefused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DNS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("host resolution", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("name or service not known", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("temporary failure in name resolution", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ENOTFOUND", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no such host", StringComparison.OrdinalIgnoreCase) ||
            (text.Contains("transport", StringComparison.OrdinalIgnoreCase) &&
             (text.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("failed", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ContainsProviderModelRejectionText(string text)
    {
        return (text.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("profile", StringComparison.OrdinalIgnoreCase)) &&
            (text.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("unknown model", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("invalid model", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("400", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasUsefulPreWorkOutput(string output)
    {
        return HasVerificationEvidence(output, string.Empty) ||
            output.Contains("HUMAN_INPUT:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Model fit:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Changed files:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Files changed:", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsDirtyDispatchGuardFailure(TaskVerificationRecord verification)
    {
        return verification.StandardError.Contains(
            "Developer/Tester dispatch exited 0 but left the worktree dirty",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ExtractChangedFiles(string standardError)
    {
        foreach (var rawLine in standardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var marker = "status_short=";
            var markerIndex = rawLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            var value = rawLine[(markerIndex + marker.Length)..].Trim();
            if (value.EndsWith(".", StringComparison.Ordinal))
            {
                value = value[..^1];
            }

            var entries = value
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => !string.Equals(entry, "clean", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry, "unavailable", StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .ToArray();
            return entries.Length == 0 ? ["unavailable"] : entries;
        }

        return ["unavailable"];
    }

    private static string[] ExtractVerificationEvidence(string standardOutput, string standardError)
    {
        var lines = $"{standardOutput}\n{standardError}"
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains("Developer/Tester dispatch exited 0 but left the worktree dirty", StringComparison.OrdinalIgnoreCase))
            .Where(line => HasVerificationEvidence(line, string.Empty))
            .Take(3)
            .ToArray();

        return lines;
    }
}

public sealed record DirtyDispatchRecovery(
    string Label,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> VerificationEvidence,
    string WorkingDirectory)
{
    public bool HasUsefulVerification => VerificationEvidence.Count > 0;

    public static DirtyDispatchRecovery None { get; } = new(
        string.Empty,
        [],
        [],
        string.Empty);
}
