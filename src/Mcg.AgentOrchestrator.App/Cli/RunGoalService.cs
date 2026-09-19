using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class RunGoalService
{
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    internal delegate Task SleepFunc(TimeSpan delay, CancellationToken ct);

    // Test-only seam. Mirrors GoalAdvancementOperations.AdvanceGoalWithSubscriptionsUntilBlocked so the
    // control-flow tests can drive RunAsync in-process; production callers leave it unset and get the real step.
    internal delegate GoalAdvanceLoopOutcome AdvanceStepFunc(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        OrchestratorWorkspace workspace,
        Goal goal,
        bool allowLargePaidSubscriptionStart,
        IModelProviderRegistry? providers);

    private const int OutputTailLineCount = 20;
    private const int MaxAutomaticFailoverAttemptsPerTask = 3;

    internal sealed record RunGoalResult(
        bool Executed,
        string StopReason,
        NextActionItem? BlockingAction,
        IReadOnlyList<RunGoalTaskSummary> CompletedTasks,
        RunGoalStopEvidence? StopEvidence,
        DateTimeOffset? ContinueAfter = null,
        bool StateChanged = false,
        DispatchProcessStartFailure? Failure = null);

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

    private sealed record AutomaticFailoverEvidence(
        string Reason,
        int FailureCount);

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
        IModelProviderRegistry? providers = null,
        AdvanceStepFunc? advanceStep = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        var sleepImpl = sleep ?? ((delay, ct) => Task.Delay(delay, ct));
        var clockImpl = clock ?? new SystemClock();
        var advanceImpl = advanceStep ?? new GoalAdvancementOperations().AdvanceGoalWithSubscriptionsUntilBlocked;
        var completedTasks = new List<RunGoalTaskSummary>();
        var completedTaskIds = new HashSet<TaskId>();
        var failedAgentsByTask = new Dictionary<TaskId, HashSet<AgentId>>();
        var failoverAttemptsByTask = new Dictionary<TaskId, int>();
        var handledEvidenceCountsByTask = new Dictionary<TaskId, int>();
        var executed = false;
        var stateChanged = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var priorStatuses = goal.Tasks.ToDictionary(t => t.Id, t => t.Status);

            var result = advanceImpl(
                kernel, agents, profiles, workspace, goal, allowLargePaidSubscriptionStart, providers);

            if (result.StepCount > 0) executed = true;
            stateChanged |= result.StateChanged;

            if (result.Failure is { } startFailure)
            {
                AddNewTerminalTaskSummaries(goal, completedTasks, completedTaskIds, priorStatuses);
                return new RunGoalResult(
                    executed,
                    result.StopReason,
                    result.BlockingAction,
                    completedTasks,
                    BuildStopEvidence(goal, result, clockImpl),
                    StateChanged: stateChanged,
                    Failure: startFailure);
            }

            if (TryApplyAutomaticFailover(
                kernel,
                agents,
                goal,
                result,
                clockImpl,
                failedAgentsByTask,
                failoverAttemptsByTask,
                handledEvidenceCountsByTask,
                out var failoverStopReason))
            {
                executed = true;
                continue;
            }

            AddNewTerminalTaskSummaries(goal, completedTasks, completedTaskIds, priorStatuses);

            if (failoverStopReason is not null)
            {
                return new RunGoalResult(
                    executed,
                    failoverStopReason,
                    result.BlockingAction,
                    completedTasks,
                    BuildStopEvidence(goal, result with { StopReason = failoverStopReason }, clockImpl),
                    StateChanged: stateChanged);
            }

            if (result.ContinueAfter.HasValue)
            {
                return new RunGoalResult(
                    executed,
                    result.StopReason,
                    result.BlockingAction,
                    completedTasks,
                    BuildStopEvidence(goal, result, clockImpl),
                    result.ContinueAfter,
                    StateChanged: stateChanged);
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
                BuildStopEvidence(goal, result, clockImpl),
                StateChanged: stateChanged);
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

    private static RunGoalStopEvidence? BuildStopEvidence(Goal goal, GoalAdvanceLoopOutcome result, IClock clock)
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

    private static TaskSpec? ResolveStopTask(Goal goal, NextActionItem? action)
    {
        if (action?.TaskId is null)
        {
            return null;
        }

        return goal.Tasks.FirstOrDefault(task =>
            task.Id.Value.StartsWith(action.TaskId.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static string? BuildOutputTail(TaskSpec task)
    {
        var verification = task.LastVerification ?? task.VerificationHistory.LastOrDefault();
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

    private static bool TryApplyAutomaticFailover(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        Goal goal,
        GoalAdvanceLoopOutcome result,
        IClock clock,
        Dictionary<TaskId, HashSet<AgentId>> failedAgentsByTask,
        Dictionary<TaskId, int> failoverAttemptsByTask,
        Dictionary<TaskId, int> handledEvidenceCountsByTask,
        out string? stopReason)
    {
        stopReason = null;
        if (!TryResolveAutomaticFailoverTask(goal, result, clock, out var task, out var evidence))
        {
            return false;
        }

        if (handledEvidenceCountsByTask.GetValueOrDefault(task.Id) >= evidence.FailureCount)
        {
            return false;
        }

        if (task.AssignedAgentId is not { } failedAgentId)
        {
            stopReason = $"Automatic failover stopped for task {task.Id.Value[..8]}: the {task.RequiredRole} task has no assigned agent. Add an available alternate {task.RequiredRole} agent to the active agent list, then re-run run-goal or re-delegate manually.";
            return false;
        }

        var failedAgents = GetFailedAgents(failedAgentsByTask, task.Id);
        failedAgents.Add(failedAgentId);

        var attempts = failoverAttemptsByTask.GetValueOrDefault(task.Id);
        if (attempts >= MaxAutomaticFailoverAttemptsPerTask)
        {
            stopReason = $"Automatic failover stopped for task {task.Id.Value[..8]} after {attempts} attempt(s) for {evidence.Reason}; cap is {MaxAutomaticFailoverAttemptsPerTask}. Add a fresh alternate {task.RequiredRole} agent or inspect the preserved failure evidence before continuing.";
            return false;
        }

        var alternate = agents.FirstOrDefault(agent =>
            agent.Status == AgentStatus.Available &&
            agent.Role == task.RequiredRole &&
            agent.Id != failedAgentId &&
            !failedAgents.Contains(agent.Id) &&
            AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy));

        if (alternate is null)
        {
            var failedList = string.Join(", ", failedAgents.Select(agentId => agentId.Value).Order(StringComparer.OrdinalIgnoreCase));
            stopReason = $"Automatic failover stopped for task {task.Id.Value[..8]} after {evidence.Reason}: no available unused alternate subscription-capable {task.RequiredRole} agent exists in the active agent list. Current failed agent: {failedAgentId.Value}. Previously failed agent(s): {failedList}. Add a different available {task.RequiredRole} agent, then re-run run-goal or use re-delegate.";
            if (evidence.Reason.Contains("provider connectivity", StringComparison.OrdinalIgnoreCase))
            {
                kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, stopReason);
            }

            return false;
        }

        kernel.RedelegateTask(goal.Id, task.Id, [alternate]);
        failoverAttemptsByTask[task.Id] = attempts + 1;
        handledEvidenceCountsByTask[task.Id] = evidence.FailureCount;
        kernel.RetryTask(
            goal.Id,
            task.Id,
            $"Automatic run-goal failover after {evidence.Reason} from agent '{failedAgentId.Value}'; retrying with alternate agent '{alternate.Id.Value}'.",
            retryCause: RetryCause.ProviderInterruption);
        return true;
    }

    private static bool TryResolveAutomaticFailoverTask(
        Goal goal,
        GoalAdvanceLoopOutcome result,
        IClock clock,
        out TaskSpec task,
        out AutomaticFailoverEvidence evidence)
    {
        var candidates = new List<TaskSpec>();
        if (ResolveStopTask(goal, result.BlockingAction) is { } stopTask)
        {
            candidates.Add(stopTask);
        }

        candidates.AddRange(goal.Tasks.Where(candidate => !candidates.Contains(candidate)));
        foreach (var candidate in candidates)
        {
            if (TryGetAutomaticFailoverEvidence(candidate, clock.UtcNow, out evidence))
            {
                task = candidate;
                return true;
            }
        }

        task = null!;
        evidence = null!;
        return false;
    }

    private static bool TryGetAutomaticFailoverEvidence(TaskSpec task, DateTimeOffset now, out AutomaticFailoverEvidence evidence)
    {
        if (DispatchFailureClassifier.HasRecoverableProviderConnectivityFailure(task))
        {
            var count = DispatchFailureClassifier.CountRecoverableProviderConnectivityFailures(task);
            evidence = new AutomaticFailoverEvidence(
                $"recoverable provider connectivity evidence ({count} failure(s))",
                count);
            return true;
        }

        if (DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, now, out var retryAfter))
        {
            evidence = new AutomaticFailoverEvidence(
                $"recoverable subscription usage limit retry deferral until {retryAfter:u}",
                DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task));
            return true;
        }

        if (DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task))
        {
            var count = DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task);
            evidence = new AutomaticFailoverEvidence(
                $"recoverable subscription usage limit evidence ({count} failure(s))",
                count);
            return true;
        }

        if (DispatchFailureClassifier.HasProviderNeutralProgressStallFailure(task))
        {
            evidence = new AutomaticFailoverEvidence(
                "provider-neutral heartbeat/progress stall evidence",
                task.VerificationHistory.Count(DispatchFailureClassifier.IsProviderNeutralProgressStallFailure));
            return true;
        }

        if (DispatchFailureClassifier.HasRecoverableProviderModelRejectionFailure(task))
        {
            var count = DispatchFailureClassifier.CountRecoverableProviderModelRejectionFailures(task);
            evidence = new AutomaticFailoverEvidence(
                $"recoverable provider model rejection evidence ({count} failure(s))",
                count);
            return true;
        }

        evidence = null!;
        return false;
    }

    private static HashSet<AgentId> GetFailedAgents(Dictionary<TaskId, HashSet<AgentId>> failedAgentsByTask, TaskId taskId)
    {
        if (!failedAgentsByTask.TryGetValue(taskId, out var failedAgents))
        {
            failedAgents = [];
            failedAgentsByTask.Add(taskId, failedAgents);
        }

        return failedAgents;
    }
}
