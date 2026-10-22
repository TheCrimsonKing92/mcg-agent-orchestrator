namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record WorktreeTreeDigestFailure(
    string Step, int ExitCode, bool TimedOut, string? ExceptionType, string? StandardErrorFirstLine,
    string? TimeoutKind = null, string? ExceptionMessage = null)
{
    internal static WorktreeTreeDigestFailure FromStep(string step, WorktreeTreeDigestGitStep result) =>
        new(step, result.ExitCode, result.TimedOut, null, FirstLine(result.StandardErrorFirstLine), result.TimeoutKind);

    internal static WorktreeTreeDigestFailure FromException(Exception exception) =>
        new("exception", -1, false, exception.GetType().Name, null, ExceptionMessage: FirstLine(exception.Message));

    internal static string WithDetail(string reason, WorktreeTreeDigestFailure? failure) =>
        failure is null ? reason : reason + "; digest_detail=" + failure.Describe();

    internal static string? FirstLine(string? text)
    {
        if (text is null) return null;
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    internal string Describe()
    {
        var description = $"step={FirstLine(Step)} exit={ExitCode}";
        if (TimedOut)
        {
            description += " timed-out=true";
            if (TimeoutKind is not null) description += $" timeout={FirstLine(TimeoutKind)}";
        }
        if (!string.IsNullOrEmpty(ExceptionType)) description += $" exception={FirstLine(ExceptionType)}";
        var message = FirstLine(ExceptionMessage);
        if (!string.IsNullOrEmpty(message)) description += $" message={message}";
        var error = FirstLine(StandardErrorFirstLine);
        if (!string.IsNullOrEmpty(error)) description += $" stderr={error}";
        return description.Length <= 240 ? description : description[..240];
    }
}
