using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class RunGoalService
{
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    internal delegate Task SleepFunc(TimeSpan delay, CancellationToken ct);
    private const int OutputTailLineCount = 20;

    internal sealed record RunGoalResult(
        bool Executed,
        string StopReason,
        NextActionDto? BlockingAction,
        IReadOnlyList<RunGoalTaskSummary> CompletedTasks,
        RunGoalStopEvidence? StopEvidence,
        DateTimeOffset? ContinueAfter = null);

    internal sealed record RunGoalTaskSummary(
        int TaskNumber,
        string TaskId,
        string Description,
        bool Succeeded,
        string? OutputTail);

    internal sealed record RunGoalStopEvidence(
        int? TaskNumber,
        string? TaskId,
        string Reason,
        string? OutputTail);

    public static async Task<RunGoalResult> RunAsync(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        OrchestratorWorkspace workspace,
        Goal goal,
        bool allowLargePaidSubscriptionStart,
        TimeSpan? pollInterval = null,
        SleepFunc? sleep = null,
        IClock? clock = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        var sleepImpl = sleep ?? ((delay, ct) => Task.Delay(delay, ct));
        var clockImpl = clock ?? new SystemClock();
        var completedTasks = new List<RunGoalTaskSummary>();
        var completedTaskIds = new HashSet<TaskId>();
        var executed = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var priorStatuses = goal.Tasks.ToDictionary(t => t.Id, t => t.Status);

            var result = GoalManagementCommandService.AdvanceGoalWithSubscriptionsUntilBlocked(
                kernel, agents, profiles, workspace, goal, allowLargePaidSubscriptionStart);

            if (result.StepCount > 0) executed = true;

            AddNewTerminalTaskSummaries(goal, completedTasks, completedTaskIds, priorStatuses);

            if (result.ContinueAfter.HasValue)
            {
                return new RunGoalResult(
                    executed,
                    result.StopReason,
                    result.BlockingAction,
                    completedTasks,
                    BuildStopEvidence(goal, result, clockImpl),
                    result.ContinueAfter);
            }

            if (result.StopReason.Contains("Background work is still running", StringComparison.OrdinalIgnoreCase))
            {
                await sleepImpl(interval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return new RunGoalResult(
                executed,
                result.StopReason,
                result.BlockingAction,
                completedTasks,
                BuildStopEvidence(goal, result, clockImpl));
        }

        return new RunGoalResult(
            executed,
            "run-goal was cancelled.",
            null,
            completedTasks,
            new RunGoalStopEvidence(null, null, "run-goal was cancelled.", null));
    }

    private static void AddNewTerminalTaskSummaries(
        Goal goal,
        List<RunGoalTaskSummary> completedTasks,
        HashSet<TaskId> completedTaskIds,
        Dictionary<TaskId, WorkTaskStatus> priorStatuses)
    {
        foreach (var task in goal.Tasks)
        {
            if (completedTaskIds.Contains(task.Id) ||
                task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Failed))
            {
                continue;
            }

            if (priorStatuses.TryGetValue(task.Id, out var prior) &&
                prior is WorkTaskStatus.Completed or WorkTaskStatus.Failed)
            {
                continue;
            }

            completedTaskIds.Add(task.Id);
            completedTasks.Add(new RunGoalTaskSummary(
                TaskDisplayNumber.Resolve(goal, task.Id),
                task.Id.Value,
                task.Description,
                task.LastVerification?.Succeeded == true,
                BuildOutputTail(task)));
        }
    }

    private static RunGoalStopEvidence? BuildStopEvidence(Goal goal, AdvanceLoopResultDto result, IClock clock)
    {
        var task = ResolveStopTask(goal, result.BlockingAction)
            ?? goal.Tasks.FirstOrDefault(task => task.Status is WorkTaskStatus.Failed or WorkTaskStatus.WaitingForHuman)
            ?? goal.Tasks.FirstOrDefault(task => DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, clock.UtcNow, out _))
            ?? goal.Tasks.FirstOrDefault(task => DispatchFailureClassifier.RequiresSubscriptionLimitReview(task));

        if (task is null)
        {
            return null;
        }

        return new RunGoalStopEvidence(
            TaskDisplayNumber.Resolve(goal, task.Id),
            task.Id.Value,
            result.StopReason,
            BuildOutputTail(task));
    }

    private static TaskSpec? ResolveStopTask(Goal goal, NextActionDto? action)
    {
        if (action?.TaskId is null)
        {
            return null;
        }

        return goal.Tasks.FirstOrDefault(task =>
            task.Id.Value.StartsWith(action.TaskId, StringComparison.OrdinalIgnoreCase));
    }

    private static string? BuildOutputTail(TaskSpec task)
    {
        var verification = task.LastVerification;
        if (verification is null)
        {
            return null;
        }

        var output = string.Join(
            Environment.NewLine,
            new[] { verification.StandardOutput, verification.StandardError }
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(OutputTailLineCount);
        return OutputTextPreview.CreateVerificationLog(string.Join(Environment.NewLine, lines)).Text;
    }
}
