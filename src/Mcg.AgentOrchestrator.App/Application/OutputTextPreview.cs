namespace Mcg.AgentOrchestrator.App.Application;

public sealed record OutputTextPreview(string Text, bool IsTruncated, int OriginalLength)
{
    private const int PayloadMaxChars = 4000;
    private const int PayloadTailChars = 1200;
    private const int TimelineMaxChars = 600;
    private const int TimelineTailChars = 180;
    private const int SummaryMaxChars = 600;
    private const int SummaryTailChars = 180;
    private const int VerificationLogMaxChars = 2000;
    private const int VerificationLogTailChars = 400;

    public static OutputTextPreview Create(string text)
    {
        return Create(text, PayloadMaxChars, PayloadTailChars, null);
    }

    public static OutputTextPreview CreateTimeline(string text)
    {
        return Create(text, TimelineMaxChars, TimelineTailChars, null);
    }

    public static OutputTextPreview CreateSummary(string text)
    {
        return Create(text, SummaryMaxChars, SummaryTailChars, null);
    }

    public static OutputTextPreview CreateVerificationLog(string text, string? logPath = null)
    {
        const string offendingCitationLabel = "Offending citation: '";
        var offendingCitationStart = text.LastIndexOf(offendingCitationLabel, StringComparison.Ordinal);
        return Create(
            text,
            VerificationLogMaxChars,
            VerificationLogTailChars,
            logPath,
            offendingCitationStart >= 0 ? offendingCitationStart : null);
    }

    private static OutputTextPreview Create(
        string text,
        int maxChars,
        int tailChars,
        string? logPath,
        int? nonElidableTailStart = null)
    {
        if (text.Length <= maxChars)
        {
            return new OutputTextPreview(text, false, text.Length);
        }

        var retainedTailChars = nonElidableTailStart is { } protectedStart
            ? Math.Max(tailChars, text.Length - protectedStart)
            : tailChars;
        var headChars = Math.Max(0, maxChars - retainedTailChars);
        var omittedChars = text.Length - headChars - retainedTailChars;
        if (omittedChars <= 0)
        {
            return new OutputTextPreview(text, false, text.Length);
        }

        var pathSuffix = logPath is null ? string.Empty : $" — full log: {logPath}";
        var marker = $"{Environment.NewLine}{Environment.NewLine}[truncated {omittedChars} chars{pathSuffix}]{Environment.NewLine}{Environment.NewLine}";
        return new OutputTextPreview(
            text[..headChars] + marker + text[^retainedTailChars..],
            true,
            text.Length);
    }
}
