namespace Mcg.AgentOrchestrator.Core;

internal static class PromptContextFormatter
{
    private const int PromptBlockMaxChars = 1200;
    private const int PromptBlockHeadChars = 800;
    private const int PromptBlockTailChars = 400;
    private const int SimpleEvidenceBlockMaxChars = 600;
    private const int SimpleEvidenceBlockHeadChars = 390;
    private const int SimpleEvidenceBlockTailChars = 180;
    private const int ComplexEvidenceBlockMaxChars = 1200;
    private const int ComplexEvidenceBlockHeadChars = 800;
    private const int ComplexEvidenceBlockTailChars = 400;
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

    public static string TrimEvidenceBlock(string value, TaskComplexity complexity)
    {
        var trimmed = value.Trim();
        return complexity == TaskComplexity.Complex
            ? TrimBlock(trimmed, ComplexEvidenceBlockMaxChars, ComplexEvidenceBlockHeadChars, ComplexEvidenceBlockTailChars)
            : TrimBlock(trimmed, SimpleEvidenceBlockMaxChars, SimpleEvidenceBlockHeadChars, SimpleEvidenceBlockTailChars);
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

    public static IReadOnlyList<ProgressEvent> SelectPromptTimelineEvents(IEnumerable<ProgressEvent> events, int maxEvents)
    {
        var ordered = events.OrderBy(evt => evt.OccurredAt).ToList();
        var decisionEvents = ordered.Where(IsDecisionRelevantTimelineEvent).ToList();
        var selected = decisionEvents.Count > 0 ? decisionEvents : ordered;
        return selected.TakeLast(maxEvents).ToList();
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

    private static bool IsDecisionRelevantTimelineEvent(ProgressEvent evt)
    {
        return evt.Kind is
            ProgressKind.HumanInputRequested or
            ProgressKind.HumanInputReceived or
            ProgressKind.TaskFailed or
            ProgressKind.TaskOutputRecorded or
            ProgressKind.TaskVerificationRecorded or
            ProgressKind.TaskDispatchRecorded or
            ProgressKind.TaskProcessStarted or
            ProgressKind.TaskCancelled or
            ProgressKind.TaskRetried or
            ProgressKind.TaskVerificationPlanUpdated;
    }
}
