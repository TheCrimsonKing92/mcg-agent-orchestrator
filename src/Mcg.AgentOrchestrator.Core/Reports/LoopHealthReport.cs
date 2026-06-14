namespace Mcg.AgentOrchestrator.Core;

public sealed record LoopHealthSnapshot(
    int GoalCount,
    int CompletedGoalCount,
    int TotalTaskCount,
    int TotalDispatchCount,
    double DispatchesPerSuccessfulMerge,
    double FalseCompletionCatchRate,
    double OperatorPromptsPerGoal,
    double ReworkRetryRate,
    double? MedianTimeToAcceptanceHours,
    IReadOnlyList<ModelOutcomeRecord> ModelOutcomeMix);

public static class LoopHealthReport
{
    public static LoopHealthSnapshot Build(
        IEnumerable<Goal> goals,
        IEnumerable<HumanInputRequest> humanInputRequests,
        int? lastN = null,
        int modelOutcomeWindowSize = ModelOutcomeScorecard.DefaultWindowSize)
    {
        var window = ApplyWindow(goals.ToList(), lastN);
        var goalIds = window.Select(g => g.Id).ToHashSet();
        var requests = humanInputRequests.Where(r => goalIds.Contains(r.GoalId)).ToList();

        var allTasks = window.SelectMany(g => g.Tasks).ToList();
        var completedGoals = window.Where(g => g.Status == GoalStatus.Completed).ToList();

        var totalDispatches = window.Sum(g =>
            g.Timeline.Count(e => e.Kind == ProgressKind.TaskDispatchRecorded));

        var dispatchesPerMerge = completedGoals.Count > 0
            ? (double)totalDispatches / completedGoals.Count
            : 0.0;

        var tasksWithAnyVerification = allTasks
            .Where(t => t.VerificationHistory.Count > 0)
            .ToList();
        var falseCompletionCount = tasksWithAnyVerification
            .Count(IsFalseCompletionCatch);
        var falseCompletionRate = tasksWithAnyVerification.Count > 0
            ? (double)falseCompletionCount / tasksWithAnyVerification.Count
            : 0.0;

        var promptsPerGoal = window.Count > 0
            ? (double)requests.Count / window.Count
            : 0.0;

        var retriedTaskIds = window
            .SelectMany(g => g.Timeline)
            .Where(e => e.Kind == ProgressKind.TaskRetried && e.TaskId is not null)
            .Select(e => e.TaskId!)
            .Distinct()
            .ToHashSet();
        var retryRate = allTasks.Count > 0
            ? (double)retriedTaskIds.Count / allTasks.Count
            : 0.0;

        double? medianHours = null;
        if (completedGoals.Count > 0)
        {
            var durations = completedGoals
                .Select(ComputeGoalDurationHours)
                .Where(h => h.HasValue)
                .Select(h => h!.Value)
                .OrderBy(h => h)
                .ToList();
            if (durations.Count > 0)
            {
                medianHours = ComputeMedian(durations);
            }
        }

        var modelMix = ModelOutcomeScorecard.Build(allTasks, modelOutcomeWindowSize);

        return new LoopHealthSnapshot(
            window.Count,
            completedGoals.Count,
            allTasks.Count,
            totalDispatches,
            dispatchesPerMerge,
            falseCompletionRate,
            promptsPerGoal,
            retryRate,
            medianHours,
            modelMix);
    }

    // A false completion is caught when the earliest verification succeeded but a
    // later verification failed — the worker claimed completion, then the verifier caught it.
    private static bool IsFalseCompletionCatch(TaskSpec task)
    {
        var ordered = task.VerificationHistory
            .OrderBy(v => v.CompletedAt)
            .ToList();
        return ordered.Count >= 2 &&
            ordered[0].Succeeded &&
            ordered.Skip(1).Any(v => !v.Succeeded);
    }

    private static List<Goal> ApplyWindow(List<Goal> goals, int? lastN)
    {
        if (lastN is null or <= 0)
        {
            return goals;
        }

        return goals
            .OrderBy(g => g.Timeline
                .FirstOrDefault(e => e.Kind == ProgressKind.GoalCreated)?.OccurredAt
                ?? DateTimeOffset.MinValue)
            .TakeLast(lastN.Value)
            .ToList();
    }

    private static double? ComputeGoalDurationHours(Goal goal)
    {
        var events = goal.Timeline.OrderBy(e => e.OccurredAt).ToList();
        if (events.Count < 2)
        {
            return null;
        }

        return (events[^1].OccurredAt - events[0].OccurredAt).TotalHours;
    }

    private static double ComputeMedian(List<double> sorted)
    {
        var n = sorted.Count;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}
