namespace Mcg.AgentOrchestrator.Core;

internal static class PromptContextFormatter
{
    private const int TimelineMessageMaxChars = 240;
    private const int TimelineMessageHeadChars = 170;
    private const int TimelineMessageTailChars = 60;

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
