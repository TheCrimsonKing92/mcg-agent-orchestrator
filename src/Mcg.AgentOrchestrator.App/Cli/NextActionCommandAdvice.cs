using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class NextActionCommandAdvice
{
public static string BuildSuggestedCommand(NextActionItem item, int? taskNumber)
{
    return item.Kind switch
    {
        NextActionKind.AnswerHumanInput => item.HumanInputRequestId is null
            ? "pending"
            : item.ResumeCommand ?? $"answer {item.HumanInputRequestId.Value[..8]} <answer>",
        NextActionKind.InspectFailedTask => taskNumber is null ? "monitor" : $"task {taskNumber} | retry {taskNumber} <note>",
        NextActionKind.FixFailedVerification => taskNumber is null ? "monitor" : $"verifications {taskNumber} | retry {taskNumber} <note>",
        NextActionKind.RefreshRunningProcess => taskNumber is null ? "monitor" : $"refresh-dispatch {taskNumber}",
        NextActionKind.ExecuteRecordedDispatch => taskNumber is null ? "monitor" : $"execute-dispatch {taskNumber} --confirm-dispatch-start",
        NextActionKind.VerifyCompletedTask => taskNumber is null ? "monitor" : $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
        NextActionKind.RunAssignedTask => taskNumber is null ? "monitor" : $"run {taskNumber}",
        NextActionKind.DelegatePendingTask => "delegate",
        NextActionKind.MonitorGoal => "monitor",
        _ => "monitor"
    };
}

public static string BuildSuggestedCommand(
    Goal goal,
    NextActionItem item,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    int? taskNumber = null;
    if (item.TaskId is not null)
    {
        taskNumber = TaskDisplayNumber.Resolve(goal, item.TaskId);
    }

    var command = BuildSuggestedCommand(item, taskNumber);
    if (item.TaskId is null || taskNumber is null)
    {
        return command;
    }

    var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == item.TaskId);
    if (task is null)
    {
        return command;
    }

    return item.Kind switch
    {
        NextActionKind.ExecuteRecordedDispatch =>
            SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, task) is { IsAnomalous: true }
                ? $"{command} {SubscriptionPromptCostGuard.CliConfirmationFlag}"
                : command,
        NextActionKind.FixFailedVerification when DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery) =>
            DirtyDispatchRecoveryTextView.BuildDirtyDispatchRecoverySuggestedCommand(taskNumber.Value, recovery),
        NextActionKind.RunAssignedTask => BuildRunAssignedTaskCommand(goal, task, taskNumber.Value, agents),
        _ => command
    };
}

private static string BuildRunAssignedTaskCommand(
    Goal goal,
    TaskSpec task,
    int taskNumber,
    IReadOnlyList<AgentDefinition>? agents)
{
    var agent = ResolveAssignedAgent(task, agents);
    if (agent is null)
    {
        return $"run {taskNumber}";
    }

    if (AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy))
    {
        return $"subscription-dispatch {taskNumber}";
    }

    var command = $"run {taskNumber}";
    var preview = AgentTaskRunner.PreviewRun(goal, task, [agent]);
    if (!ProviderSmokeRunner.IsPaidProviderName(preview.ProviderName))
    {
        return command;
    }

    command += " --confirm-paid-api-run";
    return ApiPromptCostGuard.Evaluate(preview, goal) is null
        ? command
        : $"{command} {ApiPromptCostGuard.CliConfirmationFlag}";
}

private static AgentDefinition? ResolveAssignedAgent(TaskSpec task, IReadOnlyList<AgentDefinition>? agents)
{
    if (agents is null)
    {
        return null;
    }

    return task.AssignedAgentId is null
        ? agents.FirstOrDefault(agent => agent.Status == AgentStatus.Available && agent.Role == task.RequiredRole)
        : agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId);
}

public static string BuildStageSuggestedCommand(
    Goal goal,
    TaskStageReadiness stage,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    var taskNumber = TaskDisplayNumber.Resolve(goal, stage.TaskId);
    return stage.StageStatus switch
    {
        StageReadinessStatus.ReadyToRun => BuildSuggestedCommand(
            goal,
            new NextActionItem(NextActionKind.RunAssignedTask, stage.TaskId, null, stage.SuggestedAction),
            agents),
        StageReadinessStatus.InProgress when stage.LatestEvidence == TaskEvidenceKind.Dispatch => BuildSuggestedCommand(
            goal,
            new NextActionItem(NextActionKind.ExecuteRecordedDispatch, stage.TaskId, null, stage.SuggestedAction),
            agents),
        _ => VerificationCommandAdvice.BuildStageSuggestedCommand(taskNumber, stage)
    };
}

}
