namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
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
            items);
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
            BuildEvidenceSummaryMessage(task, latestEvidence, pendingHumanInputCount));
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
            TaskEvidenceKind.FailedVerification => $"Latest verification failed with exit {task.LastVerification!.ExitCode}: {task.LastVerification.Command}",
            TaskEvidenceKind.PassedVerification => $"Latest verification passed: {task.LastVerification!.Command}",
            _ when DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task) => "Recoverable subscription usage limit; task is ready to retry later.",
            TaskEvidenceKind.RunningProcess => $"Process running pid={task.LastProcess!.ProcessId}: {task.LastProcess.Command}",
            TaskEvidenceKind.CompletedProcess => $"Process completed exit={task.LastProcess!.ExitCode?.ToString() ?? "n/a"}: {task.LastProcess.Command}",
            TaskEvidenceKind.Process => $"Process recorded: {task.LastProcess!.Command}",
            TaskEvidenceKind.Dispatch => $"Dispatch recorded for {task.LastDispatch!.WorkerName}: {task.LastDispatch.Command}",
            TaskEvidenceKind.Execution => $"Model output recorded by {task.LastExecution!.AgentName}.",
            _ => "No execution, dispatch, process, or verification evidence recorded."
        };
    }
}
