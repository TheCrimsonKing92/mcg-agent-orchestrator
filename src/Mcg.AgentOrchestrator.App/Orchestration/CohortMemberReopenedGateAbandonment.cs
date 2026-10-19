using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class CohortMemberReopenedGateAbandonment(
    ConductorGroupedGateAttemptCoordinator grouped,
    ConductEventLogWriter events,
    Func<DateTimeOffset>? utcNow = null)
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    internal IReadOnlyList<string> Apply(Goal goal)
    {
        var reopening = goal.Timeline.LastOrDefault(progress =>
            progress.OperatorIntentApplied is { } applied &&
            string.Equals(applied.Outcome, "applied", StringComparison.Ordinal) &&
            goal.Tasks.Any(task => task.Id.Value == applied.TaskId &&
                task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)));
        if (reopening is null) return [];

        var goalId = goal.Id.Value;
        var shortGoalId = goalId.Length <= 8 ? goalId : goalId[..8];
        var intentId = reopening.OperatorIntentApplied!.IntentId;
        var candidates = grouped.ReadAll()
            .Where(attempt => attempt.Kind is "cohort" or "train" &&
                attempt.ReconciledAt is null && attempt.Outcome == "Running" &&
                attempt.Members.Any(member => member.GoalId == goalId) &&
                !File.Exists(attempt.ResultPath) && !File.Exists(attempt.ExitCodePath) &&
                grouped.ReservesCapacity(attempt) && attempt.StartedAt <= reopening.OccurredAt)
            .OrderBy(attempt => attempt.AttemptId, StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var attempt in candidates)
        {
            var stopped = grouped.AbandonForReopenedMember(attempt, goalId, intentId);
            if (stopped)
            {
                var members = string.Join('+', attempt.Members.Select(member => member.GoalId));
                events.AppendRequired("gate-cancel", goalId,
                    $"GATE_CANCELLED attempt={attempt.AttemptId} kind={attempt.Kind} members={members} intent={intentId} reason=cohort-member-reopened goal={goalId}",
                    _utcNow());
            }
            var outcome = stopped ? "ABANDONED" : "ABANDON_UNPROVEN";
            lines.Add($"COHORT_MEMBER_REOPENED_GATE_{outcome} attempt={attempt.AttemptId} kind={attempt.Kind} goal={shortGoalId} intent={intentId}");
        }
        return lines;
    }
}
