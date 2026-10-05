using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public static partial class DispatchFailureClassifier
{
    private static IReadOnlyList<string> GetCliRefusalSignalLines(TaskVerificationRecord verification) =>
        ProviderLimitEvidenceParser.GetCliRefusalLines(
            verification.ExitCode,
            EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: false));

    private static bool TryGetCliRefusalLimitLine(TaskVerificationRecord verification, out string line)
    {
        line = GetCliRefusalSignalLines(verification)
            .FirstOrDefault(ProviderLimitEvidenceParser.IsCliRefusalLimitLine) ?? string.Empty;
        return line.Length > 0;
    }

    private static bool TryGetAbsoluteResetRetryAfter(
        string line,
        DateTimeOffset completedAt,
        out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        var match = AbsoluteCliReset().Match(line);
        if (!match.Success ||
            !DateTime.TryParseExact(match.Groups["month"].Value, "MMM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var month))
        {
            return false;
        }

        var zoneId = match.Groups["zone"].Value;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone) &&
            !(TimeZoneInfo.TryConvertIanaIdToWindowsId(zoneId, out var windowsId) &&
              TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out zone)))
        {
            return false;
        }

        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["minute"].Success
            ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture)
            : 0;
        if (hour is < 1 or > 12 || minute > 59)
        {
            return false;
        }

        hour = hour % 12 + (match.Groups["ampm"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var year = completedAt.UtcDateTime.Year;
        try
        {
            // Rebuild the local date on rollover so the zone's offset is resolved for that year.
            for (var candidateYear = year; candidateYear <= year + 1; candidateYear++)
            {
                var local = new DateTime(candidateYear, month.Month, day, hour, minute, 0, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local))
                {
                    return false;
                }

                var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
                if (utc >= completedAt)
                {
                    retryAfter = utc;
                    return true;
                }
            }
        }
        catch (ArgumentException)
        {
            // Impossible dates and unsupported years leave the existing limit hold without a reset.
        }

        return false;
    }

    private static bool IsApiErrorOverloadLine(string line)
    {
        var match = CliApiErrorStatus().Match(line);
        return match.Success && match.Groups["status"].Value != "429" &&
            (match.Groups["status"].Value == "529" || CliOverloadText().IsMatch(line));
    }

    private static string BuildProviderAuthenticationEvidenceSummary(TaskSpec task, TaskVerificationRecord verification)
    {
        var hasLine = TryGetProviderAuthenticationLine(verification, out var line);
        var dispatch = FindDispatchForVerification(task, verification) ?? task.LastDispatch;
        var remediation = "codex login / provider re-auth";
        if (dispatch is not null &&
            dispatch.WorkerProviderKind is not (ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexSpark or ProviderKind.OpenAICodexOssCli) &&
            !dispatch.WorkerName.StartsWith("codex", StringComparison.OrdinalIgnoreCase))
        {
            var command = ProviderLoginCommand().Match(line);
            remediation = command.Success
                ? $"run '{command.Groups["command"].Value}'"
                : $"provider re-login for {dispatch.WorkerName}";
        }

        return hasLine
            ? $"provider-authentication: {TruncateEvidence(line)}; remediation={remediation}"
            : $"provider-authentication; remediation={remediation}";
    }

    [GeneratedRegex(@"\bresets\s+(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+(?<day>\d{1,2}),?\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>am|pm)\s*\((?<zone>[A-Za-z_]+(?:/[A-Za-z0-9_+\-]+)+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteCliReset();

    [GeneratedRegex(@"^API Error:?\s*(?<status>[1-5]\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex CliApiErrorStatus();

    [GeneratedRegex(@"\b(?:Overloaded|overloaded_error)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CliOverloadText();

    [GeneratedRegex(@"\brun:?\s+(?<command>[a-z][\w.-]*\s+login(?:\s+--?[\w-]+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProviderLoginCommand();
}
