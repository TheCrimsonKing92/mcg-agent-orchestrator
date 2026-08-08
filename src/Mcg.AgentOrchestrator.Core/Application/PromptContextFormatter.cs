namespace Mcg.AgentOrchestrator.Core;

internal static class PromptContextFormatter
{
    private const int PromptBlockMaxChars = 1200;
    private const int PromptBlockHeadChars = 800;
    private const int PromptBlockTailChars = 400;
    private const int SimpleEvidenceBlockMaxChars = 600;
    private const int SimpleEvidenceBlockHeadChars = 390;
    private const int SimpleEvidenceBlockTailChars = 180;
    private const int SimpleVerificationPlanMaxChars = 600;
    private const int SimpleVerificationPlanHeadChars = 390;
    private const int SimpleVerificationPlanTailChars = 180;
    private const int ComplexEvidenceBlockMaxChars = 1200;
    private const int ComplexEvidenceBlockHeadChars = 800;
    private const int ComplexEvidenceBlockTailChars = 400;
    private const int SimplePrimaryContextMaxChars = 1200;
    private const int SimplePrimaryContextHeadChars = 800;
    private const int SimplePrimaryContextTailChars = 400;
    private const int PrimaryContextMaxChars = 2400;
    private const int PrimaryContextHeadChars = 1600;
    private const int PrimaryContextTailChars = 800;
    private const int WorkspaceDiffMaxChars = 3200;
    private const int WorkspaceDiffHeadChars = 2200;
    private const int WorkspaceDiffTailChars = 800;
    private const int PromptTitleMaxChars = 160;
    private const int TimelineMessageMaxChars = 240;
    private const int TimelineMessageHeadChars = 170;
    private const int TimelineMessageTailChars = 60;
    private const int SimpleTimelineMessageMaxChars = 140;
    private const int SimpleTimelineMessageHeadChars = 100;
    private const int SimpleTimelineMessageTailChars = 30;

    public static int TaskBriefCharacterBudget(AgentRole role, bool usesFileAccessContext)
    {
        if (!usesFileAccessContext)
        {
            return role switch
            {
                AgentRole.Planner => 12000,
                AgentRole.Researcher => 14000,
                AgentRole.Developer => 16000,
                AgentRole.Tester => 15000,
                AgentRole.Reviewer => 14000,
                _ => 12000
            };
        }

        return role switch
        {
            AgentRole.Planner => 7000,
            AgentRole.Researcher => 8000,
            AgentRole.Developer => 9000,
            AgentRole.Tester => 8500,
            AgentRole.Reviewer => 8000,
            _ => 7000
        };
    }

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

    public static string TrimVerificationPlanBlock(string value, TaskComplexity complexity)
    {
        var trimmed = value.Trim();
        return complexity == TaskComplexity.Complex
            ? TrimBlock(trimmed, PromptBlockMaxChars, PromptBlockHeadChars, PromptBlockTailChars)
            : TrimBlock(trimmed, SimpleVerificationPlanMaxChars, SimpleVerificationPlanHeadChars, SimpleVerificationPlanTailChars);
    }

    public static string TrimPrimaryContextBlock(string value)
    {
        var trimmed = value.Trim();
        return TrimBlock(trimmed, PrimaryContextMaxChars, PrimaryContextHeadChars, PrimaryContextTailChars);
    }

    public static string TrimPrimaryContextBlock(string value, TaskComplexity complexity)
    {
        var trimmed = value.Trim();
        return complexity == TaskComplexity.Complex
            ? TrimBlock(trimmed, PrimaryContextMaxChars, PrimaryContextHeadChars, PrimaryContextTailChars)
            : TrimBlock(trimmed, SimplePrimaryContextMaxChars, SimplePrimaryContextHeadChars, SimplePrimaryContextTailChars);
    }

    public static string? BuildResponseBudgetGuidance(TaskComplexity complexity)
    {
        return complexity == TaskComplexity.Simple
            ? "Keep the response concise: changed files, verification result, blockers or human input only; omit restated goals and generic progress."
            : "Keep the response evidence-focused: summarize design or code changes, verification, risks, blockers, and human input; omit generic progress and long logs unless they change acceptance.";
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
        return FormatTimelineEvent(evt, includeTimestamp, TaskComplexity.Complex);
    }

    public static string FormatTimelineEvent(ProgressEvent evt, bool includeTimestamp, TaskComplexity complexity)
    {
        var timestamp = includeTimestamp ? $"{evt.OccurredAt:u} " : string.Empty;
        return $"- {timestamp}{evt.Kind}: {TrimTimelineMessage(evt.Message, complexity)}";
    }

    public static IReadOnlyList<ProgressEvent> SelectPromptTimelineEvents(IEnumerable<ProgressEvent> events, int maxEvents)
    {
        return SelectPromptTimelineEvents(events, maxEvents, TaskComplexity.Complex);
    }

    public static IReadOnlyList<ProgressEvent> SelectPromptTimelineEvents(IEnumerable<ProgressEvent> events, int maxEvents, TaskComplexity complexity)
    {
        var ordered = events.OrderBy(evt => evt.OccurredAt).ToList();
        var decisionEvents = ordered.Where(IsDecisionRelevantTimelineEvent).ToList();
        var selected = decisionEvents.Count > 0 || complexity == TaskComplexity.Simple
            ? decisionEvents
            : ordered;
        return selected.TakeLast(maxEvents).ToList();
    }

    private static string TrimTimelineMessage(string message, TaskComplexity complexity)
    {
        var trimmed = message.Trim();
        var maxChars = complexity == TaskComplexity.Complex
            ? TimelineMessageMaxChars
            : SimpleTimelineMessageMaxChars;
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        var headChars = complexity == TaskComplexity.Complex
            ? TimelineMessageHeadChars
            : SimpleTimelineMessageHeadChars;
        var tailChars = complexity == TaskComplexity.Complex
            ? TimelineMessageTailChars
            : SimpleTimelineMessageTailChars;
        var omitted = trimmed.Length - headChars - tailChars;
        return trimmed[..headChars] +
            $" ...[truncated {omitted} chars]... " +
            trimmed[^tailChars..];
    }

    public static IReadOnlyList<string> BuildPriorTaskEvidenceLines(IReadOnlyList<TaskSpec> goalTasks, TaskId taskId, TaskComplexity complexity)
    {
        var priorCompletedTasks = goalTasks
            .TakeWhile(t => t.Id != taskId)
            .Where(t => t.Status == WorkTaskStatus.Completed && t.LastVerification is not null)
            .TakeLast(3)
            .ToList();

        if (priorCompletedTasks.Count == 0)
        {
            return [];
        }

        var lines = new List<string> { "## Prior Task Evidence" };
        foreach (var priorTask in priorCompletedTasks)
        {
            lines.Add($"### {priorTask.RequiredRole}: {TrimPromptTitle(priorTask.Description)}");
            lines.Add(TrimEvidenceBlock(priorTask.LastVerification!.StandardOutput, complexity));
        }
        lines.Add(string.Empty);
        return lines;
    }

    public static IReadOnlyList<string> BuildPriorTaskEvidencePointerLines(IReadOnlyList<TaskSpec> goalTasks, TaskId taskId)
    {
        var priorCompletedTasks = goalTasks
            .TakeWhile(t => t.Id != taskId)
            .Where(t => t.Status == WorkTaskStatus.Completed && t.LastVerification is not null)
            .TakeLast(3)
            .ToList();

        if (priorCompletedTasks.Count == 0)
        {
            return [];
        }

        var lines = new List<string>
        {
            "## Prior Task Evidence",
            "Read prior-task-summaries.md first for compact prior files, behavior, verification, risks, and model fit; open prior-task-evidence.md second only when fuller verification output is needed."
        };
        if (priorCompletedTasks.Any(task => task.RequiredRole == AgentRole.Planner))
        {
            lines.Add("A completed Planner is present: check prior-task-summaries.md for its durable-plan status. When available, read the complete Durable Planner Plan in prior-task-evidence.md before implementation; when marked UNAVAILABLE, stop and retry Planner rather than using a summary or external path.");
        }

        foreach (var priorTask in priorCompletedTasks)
        {
            var verification = priorTask.LastVerification!;
            lines.Add($"- {priorTask.RequiredRole}: {TrimPromptTitle(priorTask.Description)}; verification exit {verification.ExitCode}; summary pointer: prior-task-summaries.md; full evidence pointer: prior-task-evidence.md.");
        }

        lines.Add(string.Empty);
        return lines;
    }

    public static string BuildWorkspaceDiffSection(string diff)
    {
        var trimmed = TrimBlock(diff.Trim(), WorkspaceDiffMaxChars, WorkspaceDiffHeadChars, WorkspaceDiffTailChars);
        return $"## Workspace Diff{Environment.NewLine}{trimmed}";
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
            ProgressKind.TaskRedelegated or
            ProgressKind.TaskVerificationPlanUpdated or
            ProgressKind.TaskNote or
            ProgressKind.OperatorTaskNote or
            ProgressKind.TaskSubscriptionLimitReviewAcknowledged or
            ProgressKind.ReviewerEvidenceRequestReceived or
            ProgressKind.ReviewerEvidenceRunRecorded or
            ProgressKind.FindingEvidenceRequestRecorded or
            ProgressKind.FindingEvidenceRunRecorded or
            ProgressKind.PreReviewEvidenceRecorded or
            ProgressKind.GoalPolicyDecision;
    }
}
