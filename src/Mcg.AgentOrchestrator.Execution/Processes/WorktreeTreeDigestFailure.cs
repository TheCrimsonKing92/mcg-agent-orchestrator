namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record WorktreeTreeDigestFailure(
    string Step, int ExitCode, bool TimedOut, string? ExceptionType, string? StandardErrorFirstLine)
{
    internal static WorktreeTreeDigestFailure FromStep(string step, WorktreeTreeDigestGitStep result) =>
        new(step, result.ExitCode, result.TimedOut, null, FirstLine(result.StandardErrorFirstLine));

    internal static WorktreeTreeDigestFailure FromException(Exception exception) =>
        new("exception", -1, false, exception.GetType().Name, null);

    internal static string? FirstLine(string? text)
    {
        if (text is null) return null;
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    internal string Describe()
    {
        var description = $"step={FirstLine(Step)} exit={ExitCode}";
        if (TimedOut) description += " timed-out=true";
        if (!string.IsNullOrEmpty(ExceptionType)) description += $" exception={FirstLine(ExceptionType)}";
        var error = FirstLine(StandardErrorFirstLine);
        if (!string.IsNullOrEmpty(error)) description += $" stderr={error}";
        return description.Length <= 240 ? description : description[..240];
    }
}
