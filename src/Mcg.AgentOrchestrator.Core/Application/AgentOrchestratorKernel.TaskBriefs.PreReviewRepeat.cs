namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private static IReadOnlyList<string> BuildDeveloperPreReviewRepeatBriefBlock(Goal goal, TaskSpec task)
    {
        if (task.RequiredRole != AgentRole.Developer || task.LatestRetryAt is null)
            return [];

        var message = goal.Timeline
            .Where(evt => evt.TaskId == task.Id && evt.OccurredAt >= task.LatestRetryAt &&
                evt.Kind is ProgressKind.TaskRetried or ProgressKind.TaskRetryFeedbackUpdated &&
                evt.Message.Contains(PreReviewRepeatedFailureStatement.StartMarker, StringComparison.Ordinal))
            .OrderByDescending(evt => evt.OccurredAt)
            .Select(evt => evt.Message)
            .FirstOrDefault();
        if (message is null)
            return [];

        var start = message.IndexOf(PreReviewRepeatedFailureStatement.StartMarker, StringComparison.Ordinal);
        var end = message.IndexOf(PreReviewRepeatedFailureStatement.EndMarker, start, StringComparison.Ordinal);
        if (start < 0 || end < 0)
            return [];
        var block = message[start..(end + PreReviewRepeatedFailureStatement.EndMarker.Length)];
        return ["## Pre-Review Repeated Failing Tests (conductor-owned)", block, string.Empty];
    }
}
