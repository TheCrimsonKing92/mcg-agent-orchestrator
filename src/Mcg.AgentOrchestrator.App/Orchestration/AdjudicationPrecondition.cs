using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AdjudicationPrecondition
{
    internal static AdjudicationPreconditionFacts Capture(Goal goal, TaskSpec task) => new(
        task.Status.ToString(),
        task.LastDispatch?.DispatchedAt.UtcTicks,
        task.LatestRetryAt?.UtcTicks,
        goal.Status.ToString(),
        goal.Tasks.Select(item => item.LastDispatch?.ResultCommit)
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim());

    internal static bool IsStale(
        AdjudicateOperatorIntentPayload payload,
        Goal goal,
        TaskSpec task)
    {
        if (payload.Precondition is not { } expected)
            return false;

        var current = Capture(goal, task);
        return !string.Equals(expected.TaskStatus, current.TaskStatus, StringComparison.Ordinal) ||
            expected.TaskDispatchedAtUtcTicks != current.TaskDispatchedAtUtcTicks ||
            expected.TaskLatestRetryAtUtcTicks != current.TaskLatestRetryAtUtcTicks ||
            !string.Equals(expected.GoalStatus, current.GoalStatus, StringComparison.Ordinal) ||
            !string.Equals(expected.GoalCandidateCommit?.Trim(), current.GoalCandidateCommit,
                StringComparison.Ordinal);
    }
}
