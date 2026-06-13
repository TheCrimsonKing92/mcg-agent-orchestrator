namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const int SimplePaidApiPromptWarningChars = 4000;
    private const int ComplexPaidApiPromptWarningChars = 6000;
    private const int SimplePaidSubscriptionPromptWarningChars = 6000;
    private const int ComplexPaidSubscriptionPromptWarningChars = 9000;

    public ProcessBatchPlan BuildProcessBatchPlan(GoalId goalId, ProcessBatchActionKind action)
    {
        var goal = GetGoal(goalId);
        var items = goal.Tasks
            .Select(task => BuildProcessBatchPlanItem(task, action))
            .ToList();

        return new ProcessBatchPlan(
            goal.Id,
            goal.Objective,
            goal.Status,
            action,
            items.Count(item => item.Status == ProcessBatchItemStatus.Ready),
            items.Count(item => item.Status == ProcessBatchItemStatus.Skipped),
            items);
    }

    public TaskQueryResult QueryTasks(GoalId goalId, TaskQuery query)
    {
        var goal = GetGoal(goalId);
        IEnumerable<TaskSpec> tasks = goal.Tasks;

        if (query.Status is { } status)
        {
            tasks = tasks.Where(task => task.Status == status);
        }

        if (query.Role is { } role)
        {
            tasks = tasks.Where(task => task.RequiredRole == role);
        }

        if (!string.IsNullOrWhiteSpace(query.IdPrefix))
        {
            tasks = tasks.Where(task => task.Id.Value.StartsWith(query.IdPrefix, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Evidence is { } evidence)
        {
            tasks = tasks.Where(task => MatchesEvidence(task, evidence));
        }

        if (query.EventKind is { } eventKind)
        {
            tasks = tasks.Where(task => goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == eventKind));
        }

        return new TaskQueryResult(goal.Id, goal.Objective, goal.Status, query, tasks.ToList());
    }

    public GoalEvidenceSummary BuildGoalEvidenceSummary(GoalId goalId)
    {
        var goal = GetGoal(goalId);
        var pendingInput = GetPendingHumanInput(goalId);
        var items = goal.Tasks
            .Select(task => BuildTaskEvidenceSummary(task, pendingInput.Count(request => request.TaskId == task.Id)))
            .ToList();

        return new GoalEvidenceSummary(
            goal.Id,
            goal.Objective,
            goal.Status,
            goal.Tasks.Count,
            items.Count(item => item.HasExecution),
            items.Count(item => item.HasDispatch),
            items.Count(item => item.HasProcess),
            items.Count(item => item.IsProcessRunning),
            items.Count(item => item.HasVerification),
            items.Count(item => item.LatestVerificationSucceeded is true),
            items.Count(item => item.LatestVerificationSucceeded is false),
            pendingInput.Count,
            SumKnownUsage(goal.Tasks.Select(task => task.LastExecution?.Usage?.InputTokens)),
            SumKnownUsage(goal.Tasks.Select(task => task.LastExecution?.Usage?.OutputTokens)),
            SumKnownUsage(goal.Tasks.Where(HasPotentiallyPaidExecution).Select(task => task.LastExecution?.Usage?.InputTokens)),
            SumKnownUsage(goal.Tasks.Where(HasPotentiallyPaidExecution).Select(task => task.LastExecution?.Usage?.OutputTokens)),
            BuildModelUsageSummary(goal.Tasks),
            BuildDispatchModelSummary(goal.Tasks),
            ModelFitEvidence.BuildSummary(goal.Tasks.SelectMany(ModelFitEvidence.FindNotes)),
            items);
    }

    private static List<ModelUsageSummary> BuildModelUsageSummary(IReadOnlyList<TaskSpec> tasks)
    {
        return tasks
            .Where(task => task.LastExecution is not null)
            .GroupBy(
                task => new
                {
                    task.LastExecution!.ProviderName,
                    task.LastExecution.ModelName,
                    task.LastExecution.TaskComplexity
                })
            .OrderBy(group => group.Key.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.ModelName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.TaskComplexity?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModelUsageSummary(
                group.Key.ProviderName,
                group.Key.ModelName,
                group.Count(),
                SumKnownUsage(group.Select(task => task.LastExecution!.Usage?.InputTokens)),
                SumKnownUsage(group.Select(task => task.LastExecution!.Usage?.OutputTokens)),
                group.Count(HasOutputTokenLimitHit),
                ResolveSharedMaxOutputTokens(group.Select(task => task.LastExecution!.MaxOutputTokens)),
                group.Key.TaskComplexity,
                IsPotentiallyPaidProvider(group.Key.ProviderName),
                SumKnownUsage(group.Select(task => task.LastExecution!.PromptCharacterCount))))
            .ToList();
    }

    private static List<DispatchModelSummary> BuildDispatchModelSummary(IReadOnlyList<TaskSpec> tasks)
    {
        return tasks
            .Where(HasDispatchModelSelection)
            .GroupBy(
                task => new
                {
                    task.LastDispatch!.ProviderName,
                    task.LastDispatch.ModelName,
                    task.LastDispatch.TaskComplexity,
                    task.LastDispatch.ReasoningEffort,
                    task.LastDispatch.UsesComplexModel
                })
            .OrderBy(group => group.Key.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.ModelName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.TaskComplexity?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.ReasoningEffort ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DispatchModelSummary(
                group.Key.ProviderName!,
                group.Key.ModelName!,
                group.Count(),
                group.Key.TaskComplexity,
                group.Key.ReasoningEffort,
                IsPotentiallyPaidProvider(group.Key.ProviderName!),
                SumKnownUsage(group.Select(task => task.LastDispatch!.PromptCharacterCount)),
                group.Key.UsesComplexModel))
            .ToList();
    }

    private static bool HasDispatchModelSelection(TaskSpec task)
    {
        return !string.IsNullOrWhiteSpace(task.LastDispatch?.ProviderName) &&
            !string.IsNullOrWhiteSpace(task.LastDispatch.ModelName);
    }

    private static bool HasPotentiallyPaidExecution(TaskSpec task)
    {
        return task.LastExecution is not null && IsPotentiallyPaidProvider(task.LastExecution.ProviderName);
    }

    private static bool IsPotentiallyPaidProvider(string providerName)
    {
        return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
    }

    private static int? SumKnownUsage(IEnumerable<int?> values)
    {
        var total = 0;
        var hasValue = false;
        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            total += value.Value;
            hasValue = true;
        }

        return hasValue ? total : null;
    }

    private static int? ResolveSharedMaxOutputTokens(IEnumerable<int?> values)
    {
        int? shared = null;
        var hasValue = false;
        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            if (!hasValue)
            {
                shared = value;
                hasValue = true;
                continue;
            }

            if (shared != value)
            {
                return null;
            }
        }

        return shared;
    }

    private static bool HasOutputTokenLimitHit(TaskSpec task)
    {
        return OutputTokenLimit.IsHit(task.LastExecution);
    }

    public IReadOnlyList<ModelOutcomeRecord> BuildModelOutcomeScorecard(int windowSize = ModelOutcomeScorecard.DefaultWindowSize)
    {
        var allTasks = Goals.SelectMany(goal => goal.Tasks);
        return ModelOutcomeScorecard.Build(allTasks, windowSize);
    }

    public GoalStageReadinessReport BuildStageReadinessReport(GoalId goalId)
    {
        var goal = GetGoal(goalId);
        var pendingInput = GetPendingHumanInput(goalId);
        var gateByTask = BuildVerificationGate(goalId)
            .Tasks
            .ToDictionary(task => task.TaskId);

        var stages = goal.Tasks
            .Select(task => BuildTaskStageReadiness(
                task,
                gateByTask[task.Id],
                pendingInput.Count(request => request.TaskId == task.Id)))
            .ToList();

        return new GoalStageReadinessReport(
            goal.Id,
            goal.Objective,
            goal.Status,
            stages.Count,
            stages.Count(stage => stage.StageStatus == StageReadinessStatus.Verified),
            stages.Count(stage => stage.StageStatus != StageReadinessStatus.Verified),
            stages.Count(IsBlockedStage),
            stages.Count > 0 && stages.All(stage => stage.StageStatus == StageReadinessStatus.Verified) && pendingInput.Count == 0,
            stages);
    }

    private static bool MatchesEvidence(TaskSpec task, TaskEvidenceKind evidence)
    {
        return evidence switch
        {
            TaskEvidenceKind.None => task.LastExecution is null &&
                task.LastDispatch is null &&
                task.LastProcess is null &&
                task.LastVerification is null,
            TaskEvidenceKind.Execution => task.LastExecution is not null,
            TaskEvidenceKind.Dispatch => task.LastDispatch is not null,
            TaskEvidenceKind.Process => task.LastProcess is not null,
            TaskEvidenceKind.RunningProcess => task.LastProcess is { IsRunning: true },
            TaskEvidenceKind.CompletedProcess => task.LastProcess is { IsRunning: false, WasCancelled: false },
            TaskEvidenceKind.Verification => task.LastVerification is not null,
            TaskEvidenceKind.PassedVerification => task.LastVerification is { Succeeded: true },
            TaskEvidenceKind.FailedVerification => task.LastVerification is { Succeeded: false },
            _ => false
        };
    }

    private static TaskEvidenceSummary BuildTaskEvidenceSummary(TaskSpec task, int pendingHumanInputCount)
    {
        var latestEvidence = GetLatestEvidenceKind(task);

        return new TaskEvidenceSummary(
            task.Id,
            task.RequiredRole,
            task.Description,
            task.Status,
            latestEvidence,
            task.LastExecution is not null,
            task.LastDispatch is not null,
            task.LastProcess is not null,
            task.LastProcess is { IsRunning: true },
            task.LastVerification is not null,
            task.LastVerification?.Succeeded,
            task.VerificationHistory.Count,
            pendingHumanInputCount,
            BuildEvidenceSummaryMessage(task, latestEvidence, pendingHumanInputCount),
            ModelFitEvidence.FindLatestNote(task));
    }

    private static TaskStageReadiness BuildTaskStageReadiness(TaskSpec task, TaskVerificationGate gate, int pendingHumanInputCount)
    {
        var latestEvidence = GetLatestEvidenceKind(task);
        var status = GetStageReadinessStatus(task, gate, pendingHumanInputCount);

        return new TaskStageReadiness(
            task.Id,
            task.RequiredRole,
            task.Description,
            task.Status,
            task.AssignedAgentId is not null,
            status,
            latestEvidence,
            gate.GateStatus,
            pendingHumanInputCount,
            BuildStageReadinessMessage(task, gate, status, pendingHumanInputCount),
            BuildStageSuggestedAction(status));
    }

    private static StageReadinessStatus GetStageReadinessStatus(TaskSpec task, TaskVerificationGate gate, int pendingHumanInputCount)
    {
        if (pendingHumanInputCount > 0 || task.Status == WorkTaskStatus.WaitingForHuman)
        {
            return StageReadinessStatus.WaitingForHuman;
        }

        if (gate.GateStatus == VerificationGateStatus.Passed)
        {
            return StageReadinessStatus.Verified;
        }

        if (gate.GateStatus == VerificationGateStatus.FailedVerification)
        {
            return StageReadinessStatus.VerificationFailed;
        }

        if (task.Status is WorkTaskStatus.Failed or WorkTaskStatus.Cancelled)
        {
            return StageReadinessStatus.FailedOrCancelled;
        }

        if (task.LastProcess is { IsRunning: true } || task.Status == WorkTaskStatus.Running)
        {
            return StageReadinessStatus.InProgress;
        }

        if (gate.GateStatus == VerificationGateStatus.MissingVerification)
        {
            return StageReadinessStatus.NeedsVerification;
        }

        if (task.Status == WorkTaskStatus.Assigned)
        {
            return StageReadinessStatus.ReadyToRun;
        }

        return StageReadinessStatus.NeedsDelegation;
    }

    private static string BuildStageReadinessMessage(
        TaskSpec task,
        TaskVerificationGate gate,
        StageReadinessStatus status,
        int pendingHumanInputCount)
    {
        return status switch
        {
            StageReadinessStatus.WaitingForHuman => $"{pendingHumanInputCount} pending human input request(s) are blocking this stage.",
            StageReadinessStatus.Verified => gate.Message,
            StageReadinessStatus.VerificationFailed => gate.Message,
            StageReadinessStatus.FailedOrCancelled => $"Task status is {task.Status}; inspect task output before continuing.",
            StageReadinessStatus.InProgress when task.LastProcess is { IsRunning: true } => $"Background process is running pid={task.LastProcess.ProcessId}: {task.LastProcess.Command}",
            StageReadinessStatus.InProgress when task.LastDispatch is not null => $"Task is running with recorded dispatch: {task.LastDispatch.Command}",
            StageReadinessStatus.InProgress => "Task is in progress.",
            StageReadinessStatus.NeedsVerification => gate.Message,
            StageReadinessStatus.ReadyToRun => "Task is assigned and ready for model execution or worker dispatch.",
            StageReadinessStatus.NeedsDelegation => "Task is not assigned to an available role agent.",
            _ => gate.Message
        };
    }

    private static string BuildStageSuggestedAction(StageReadinessStatus status)
    {
        return status switch
        {
            StageReadinessStatus.NeedsDelegation => "Delegate this stage to a role-matched agent.",
            StageReadinessStatus.ReadyToRun => "Run the assigned model task or dispatch it to a worker profile.",
            StageReadinessStatus.InProgress => "Refresh or inspect the running stage until it completes.",
            StageReadinessStatus.WaitingForHuman => "Answer the pending human input request.",
            StageReadinessStatus.NeedsVerification => "Record verification evidence for this completed stage.",
            StageReadinessStatus.VerificationFailed => "Inspect verification history, retry the stage after fixes, then rerun verification.",
            StageReadinessStatus.Verified => "No stage work remains.",
            StageReadinessStatus.FailedOrCancelled => "Inspect task output, then retry the task or update task progress.",
            _ => "Inspect this stage before continuing."
        };
    }

    private static bool IsBlockedStage(TaskStageReadiness stage)
    {
        return stage.StageStatus is StageReadinessStatus.WaitingForHuman
            or StageReadinessStatus.VerificationFailed
            or StageReadinessStatus.FailedOrCancelled;
    }

    private static TaskEvidenceKind GetLatestEvidenceKind(TaskSpec task)
    {
        if (task.LastVerification is { Succeeded: false })
        {
            return TaskEvidenceKind.FailedVerification;
        }

        if (task.LastVerification is { Succeeded: true })
        {
            return TaskEvidenceKind.PassedVerification;
        }

        if (task.LastProcess is { IsRunning: true })
        {
            return TaskEvidenceKind.RunningProcess;
        }

        if (task.LastProcess is { IsRunning: false, WasCancelled: false })
        {
            return TaskEvidenceKind.CompletedProcess;
        }

        if (task.LastProcess is not null)
        {
            return TaskEvidenceKind.Process;
        }

        if (task.LastDispatch is not null)
        {
            return TaskEvidenceKind.Dispatch;
        }

        if (task.LastExecution is not null)
        {
            return TaskEvidenceKind.Execution;
        }

        return TaskEvidenceKind.None;
    }

    private static string BuildEvidenceSummaryMessage(TaskSpec task, TaskEvidenceKind latestEvidence, int pendingHumanInputCount)
    {
        if (pendingHumanInputCount > 0)
        {
            return $"{pendingHumanInputCount} pending human input request(s).";
        }

        return latestEvidence switch
        {
            TaskEvidenceKind.FailedVerification when DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery) =>
                BuildDirtyDispatchRecoveryMessage(recovery),
            TaskEvidenceKind.FailedVerification => $"Latest verification failed with exit {task.LastVerification!.ExitCode}: {task.LastVerification.Command}",
            TaskEvidenceKind.PassedVerification => $"Latest verification passed: {task.LastVerification!.Command}",
            _ when DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task) => "Recoverable subscription usage limit; task is ready to retry later.",
            TaskEvidenceKind.RunningProcess => $"Process running pid={task.LastProcess!.ProcessId}: {task.LastProcess.Command}",
            TaskEvidenceKind.CompletedProcess => $"Process completed exit={task.LastProcess!.ExitCode?.ToString() ?? "n/a"}: {task.LastProcess.Command}",
            TaskEvidenceKind.Process => $"Process recorded: {task.LastProcess!.Command}",
            TaskEvidenceKind.Dispatch => BuildDispatchEvidenceMessage(task.LastDispatch!),
            TaskEvidenceKind.Execution when HasOutputTokenLimitHit(task) =>
                BuildExecutionEvidenceMessage(task.LastExecution!, possibleOutputCapHit: true),
            TaskEvidenceKind.Execution => BuildExecutionEvidenceMessage(task.LastExecution!, possibleOutputCapHit: false),
            _ => "No execution, dispatch, process, or verification evidence recorded."
        };
    }

    private static string BuildDispatchEvidenceMessage(TaskDispatchRecord dispatch)
    {
        return $"Dispatch recorded for {dispatch.WorkerName}{FormatDispatchModelSelection(dispatch)}{FormatPaidCostWarning(dispatch.ProviderName, dispatch.TaskComplexity, dispatch.PromptCharacterCount, PaidCostWarningSource.Subscription, dispatch.UsesComplexModel)}: {dispatch.Command}";
    }

    private static string BuildExecutionEvidenceMessage(TaskExecutionRecord execution, bool possibleOutputCapHit)
    {
        var cap = possibleOutputCapHit
            ? $"; possible output cap hit at {execution.MaxOutputTokens} tokens"
            : string.Empty;
        return $"Model output recorded by {execution.AgentName}{cap}{FormatPaidCostWarning(execution.ProviderName, execution.TaskComplexity, execution.PromptCharacterCount, PaidCostWarningSource.Api)}.";
    }

    private static string FormatPaidCostWarning(
        string? providerName,
        TaskComplexity? complexity,
        int? promptCharacterCount,
        PaidCostWarningSource source,
        bool usesComplexModel = false)
    {
        if (string.IsNullOrWhiteSpace(providerName) ||
            !IsPotentiallyPaidProvider(providerName) ||
            promptCharacterCount is null)
        {
            return string.Empty;
        }

        var sourceLabel = source == PaidCostWarningSource.Subscription ? "subscription" : "API";
        var threshold = (source, complexity == TaskComplexity.Complex || usesComplexModel) switch
        {
            (PaidCostWarningSource.Subscription, true) => ComplexPaidSubscriptionPromptWarningChars,
            (PaidCostWarningSource.Subscription, false) => SimplePaidSubscriptionPromptWarningChars,
            (PaidCostWarningSource.Api, true) => ComplexPaidApiPromptWarningChars,
            _ => SimplePaidApiPromptWarningChars
        };
        var warnings = new List<string>();
        if (promptCharacterCount.Value > threshold)
        {
            warnings.Add($"large paid {sourceLabel} prompt {promptCharacterCount.Value} chars (>{threshold})");
        }

        if (complexity == TaskComplexity.Complex || usesComplexModel)
        {
            warnings.Add($"complex paid {sourceLabel} model");
        }

        if (warnings.Count == 0)
        {
            return string.Empty;
        }

        warnings.Add($"try a local or routine model before repeating paid {sourceLabel} work");
        return "; " + string.Join("; ", warnings);
    }

    private static string FormatDispatchModelSelection(TaskDispatchRecord dispatch)
    {
        if (string.IsNullOrWhiteSpace(dispatch.ProviderName) || string.IsNullOrWhiteSpace(dispatch.ModelName))
        {
            return string.Empty;
        }

        var complexity = dispatch.TaskComplexity is null ? string.Empty : $" {dispatch.TaskComplexity.Value}";
        var reasoning = string.IsNullOrWhiteSpace(dispatch.ReasoningEffort)
            ? string.Empty
            : $" reasoning {dispatch.ReasoningEffort}";
        return $" using {dispatch.ProviderName}/{dispatch.ModelName}{complexity}{reasoning}";
    }

    private enum PaidCostWarningSource
    {
        Api,
        Subscription
    }
}
