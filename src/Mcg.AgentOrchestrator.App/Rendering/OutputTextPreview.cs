namespace Mcg.AgentOrchestrator.App.Rendering;

internal sealed record OutputTextPreview(string Text, bool IsTruncated, int OriginalLength)
{
    private const int PayloadMaxChars = 4000;
    private const int PayloadTailChars = 1200;
    private const int TimelineMaxChars = 600;
    private const int TimelineTailChars = 180;
    private const int SummaryMaxChars = 600;
    private const int SummaryTailChars = 180;

    public static OutputTextPreview Create(string text)
    {
        return Create(text, PayloadMaxChars, PayloadTailChars);
    }

    public static OutputTextPreview CreateTimeline(string text)
    {
        return Create(text, TimelineMaxChars, TimelineTailChars);
    }

    public static OutputTextPreview CreateSummary(string text)
    {
        return Create(text, SummaryMaxChars, SummaryTailChars);
    }

    private static OutputTextPreview Create(string text, int maxChars, int tailChars)
    {
        if (text.Length <= maxChars)
        {
            return new OutputTextPreview(text, false, text.Length);
        }

        var headChars = maxChars - tailChars;
        var omittedChars = text.Length - headChars - tailChars;
        var marker = $"{Environment.NewLine}{Environment.NewLine}[truncated {omittedChars} chars]{Environment.NewLine}{Environment.NewLine}";
        return new OutputTextPreview(
            text[..headChars] + marker + text[^tailChars..],
            true,
            text.Length);
    }
}
