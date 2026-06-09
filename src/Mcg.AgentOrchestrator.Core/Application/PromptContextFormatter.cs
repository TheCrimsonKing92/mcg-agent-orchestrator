namespace Mcg.AgentOrchestrator.Core;

internal static class PromptContextFormatter
{
    private const int PromptBlockMaxChars = 1200;
    private const int PromptBlockHeadChars = 800;
    private const int PromptBlockTailChars = 400;
    private const int PrimaryContextMaxChars = 2400;
    private const int PrimaryContextHeadChars = 1600;
    private const int PrimaryContextTailChars = 800;
    private const int PromptTitleMaxChars = 160;
    private const int TimelineMessageMaxChars = 240;
    private const int TimelineMessageHeadChars = 170;
    private const int TimelineMessageTailChars = 60;

    public static string TrimPromptBlock(string value)
    {
        var trimmed = value.Trim();
        return TrimBlock(trimmed, PromptBlockMaxChars, PromptBlockHeadChars, PromptBlockTailChars);
    }

    public static string TrimPrimaryContextBlock(string value)
    {
        var trimmed = value.Trim();
        return TrimBlock(trimmed, PrimaryContextMaxChars, PrimaryContextHeadChars, PrimaryContextTailChars);
    }

    private static string TrimBlock(string trimmed, int maxChars, int headChars, int tailChars)
    {
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        var omitted = trimmed.Length - headChars - tailChars;
        return trimmed[..headChars] +
            $"{Environment.NewLine}...[truncated {omitted} chars for prompt budget]...{Environment.NewLine}" +
            trimmed[^tailChars..];
    }

    public static string TrimPromptTitle(string value)
    {
        var trimmed = value.Trim().ReplaceLineEndings(" ");
        while (trimmed.Contains("  ", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (trimmed.Length <= PromptTitleMaxChars)
        {
            return trimmed;
        }

        return trimmed[..(PromptTitleMaxChars - 3)] + "...";
    }

    public static string FormatTimelineEvent(ProgressEvent evt, bool includeTimestamp)
    {
        var timestamp = includeTimestamp ? $"{evt.OccurredAt:u} " : string.Empty;
        return $"- {timestamp}{evt.Kind}: {TrimTimelineMessage(evt.Message)}";
    }

    private static string TrimTimelineMessage(string message)
    {
        var trimmed = message.Trim();
        if (trimmed.Length <= TimelineMessageMaxChars)
        {
            return trimmed;
        }

        var omitted = trimmed.Length - TimelineMessageHeadChars - TimelineMessageTailChars;
        return trimmed[..TimelineMessageHeadChars] +
            $" ...[truncated {omitted} chars]... " +
            trimmed[^TimelineMessageTailChars..];
    }
}
