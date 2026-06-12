using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardResponseMapper
{
public static MonitorDto ToMonitorDto(GoalMonitor monitor)
{
    return new MonitorDto(
        monitor.GoalId.Value,
        SummaryText(monitor.Objective),
        monitor.Status,
        monitor.TotalTasks,
        monitor.TaskStatusCounts.Select(count => new StatusCountDto(count.Status, count.Count)).ToList(),
        monitor.PendingHumanInputCount,
        monitor.AttentionItems.Select(item => new AttentionDto(item.Kind, item.TaskId?.Value, TimelineText(item.Message))).ToList(),
        monitor.LastTimelineEventAt);
}

public static GoalAcceptanceSummaryDto ToGoalAcceptanceSummaryDto(Goal goal, GoalAcceptanceSummary summary)
{
    return new GoalAcceptanceSummaryDto(
        summary.GoalId.Value,
        SummaryText(summary.Objective),
        summary.Status,
        summary.IsAccepted,
        summary.TotalTasks,
        summary.PassedTasks,
        summary.OpenVerificationCount,
        summary.PendingHumanInputCount,
        summary.Blockers.Select(blocker => ToGoalAcceptanceBlockerDto(goal, blocker)).ToList());
}

public static GoalAcceptanceBlockerDto ToGoalAcceptanceBlockerDto(Goal goal, GoalAcceptanceBlocker blocker)
{
    int? taskNumber = blocker.TaskId is null ? null : ConsoleViews.GetTaskDisplayNumber(goal, blocker.TaskId);
    return new GoalAcceptanceBlockerDto(
        blocker.Kind,
        blocker.TaskId?.Value,
        taskNumber,
        blocker.HumanInputRequestId?.Value,
        TimelineText(blocker.Message),
        TimelineText(blocker.SuggestedAction),
        ConsoleViews.BuildAcceptanceSuggestedCommand(blocker, taskNumber));
}

public static GoalEvidenceSummaryDto ToGoalEvidenceSummaryDto(Goal goal, GoalEvidenceSummary summary)
{
    return new GoalEvidenceSummaryDto(
        summary.GoalId.Value,
        SummaryText(summary.Objective),
        summary.Status,
        summary.TotalTasks,
        summary.TasksWithExecution,
        summary.TasksWithDispatch,
        summary.TasksWithProcess,
        summary.RunningProcesses,
        summary.TasksWithVerification,
        summary.PassedVerifications,
        summary.FailedVerifications,
        summary.PendingHumanInputCount,
        summary.InputTokens,
        summary.OutputTokens,
        summary.PotentiallyPaidInputTokens,
        summary.PotentiallyPaidOutputTokens,
        summary.ModelUsage.Select(item => new ModelUsageSummaryDto(
            item.ProviderName,
            item.ModelName,
            item.ExecutionCount,
            item.InputTokens,
            item.OutputTokens,
            item.OutputTokenLimitHitCount,
            item.MaxOutputTokens,
            item.TaskComplexity,
            item.IsPotentiallyPaidProvider,
            item.PromptCharacterCount)).ToList(),
        summary.DispatchModelUsage.Select(item => new DispatchModelSummaryDto(
            item.ProviderName,
            item.ModelName,
            item.DispatchCount,
            item.TaskComplexity,
            item.ReasoningEffort,
            item.IsPotentiallyPaidProvider,
            item.PromptCharacterCount,
            item.UsesComplexModel)).ToList(),
        summary.ModelFit.Select(item => new ModelFitSummaryDto(
            item.ProviderName,
            item.ModelName,
            item.NoteCount,
            item.AdequateCount,
            item.OverkillCount,
            item.UnderpoweredCount,
            item.UnknownCount,
            item.TaskShapes ?? [])).ToList(),
        summary.Tasks.Select(item => ToTaskEvidenceSummaryDto(goal, item)).ToList());
}

public static TaskEvidenceSummaryDto ToTaskEvidenceSummaryDto(Goal goal, TaskEvidenceSummary item)
{
    return new TaskEvidenceSummaryDto(
        ConsoleViews.GetTaskDisplayNumber(goal, item.TaskId),
        item.TaskId.Value,
        item.Role,
        SummaryText(item.Description),
        item.TaskStatus,
        item.LatestEvidence,
        item.HasExecution,
        item.HasDispatch,
        item.HasProcess,
        item.IsProcessRunning,
        item.HasVerification,
        item.LatestVerificationSucceeded,
        item.VerificationHistoryCount,
        item.PendingHumanInputCount,
        TimelineText(item.Message),
        item.ModelFitNote is null ? null : TimelineText(item.ModelFitNote));
}

public static GoalWorkSummaryDto ToGoalWorkSummaryDto(
    AgentOrchestratorKernel kernel,
    Goal goal,
    IReadOnlyList<AgentDefinition>? agents = null,
    DashboardHostInfoDto? host = null)
{
    var monitor = kernel.BuildMonitor(goal.Id);
    var gate = kernel.BuildVerificationGate(goal.Id);
    var nextAction = kernel.BuildNextActions(goal.Id).Items.FirstOrDefault();

    return new GoalWorkSummaryDto(
        goal.Id.Value,
        SummaryText(goal.Objective),
        goal.Status,
        goal.Tasks.Count,
        monitor.PendingHumanInputCount,
        gate.IsSatisfied,
        nextAction is null ? null : ToNextActionDto(goal, nextAction, 1, agents),
        host,
        goal.Tasks.Select(task => ToTaskWorkSummaryDto(goal, task)).ToList());
}

public static TaskWorkContextDto ToTaskWorkContextDto(
    AgentOrchestratorKernel kernel,
    Goal goal,
    TaskSpec task,
    DashboardHostInfoDto host,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    var monitor = kernel.BuildMonitor(goal.Id);
    var gate = kernel.BuildVerificationGate(goal.Id);
    var nextAction = kernel.BuildNextActions(goal.Id).Items.FirstOrDefault();

    return new TaskWorkContextDto(
        goal.Id.Value,
        SummaryText(goal.Objective),
        goal.Status,
        goal.Tasks.Count,
        monitor.PendingHumanInputCount,
        gate.IsSatisfied,
        nextAction is null ? null : ToNextActionDto(goal, nextAction, 1, agents),
        ToTaskWorkSummaryDto(goal, task),
        host);
}

public static TaskWorkSummaryDto ToTaskWorkSummaryDto(Goal goal, TaskSpec task)
{
    return new TaskWorkSummaryDto(
        ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
        task.Id.Value,
        task.RequiredRole,
        task.Status,
        ConsoleViews.FormatTaskEvidence(task),
        task.AssignedAgentId?.Value,
        task.LastDispatch is null
            ? null
            : new DispatchSummaryDto(
                task.LastDispatch.WorkerName,
                task.LastDispatch.DispatchedAt,
                task.LastDispatch.ProviderName,
                task.LastDispatch.ModelName,
                task.LastDispatch.ReasoningEffort,
                task.LastDispatch.TaskComplexity,
                task.LastDispatch.PromptCharacterCount,
                task.LastDispatch.UsesComplexModel),
        task.LastProcess is null
            ? null
            : new ProcessSummaryDto(
                task.LastProcess.ProcessId,
                task.LastProcess.IsRunning,
                task.LastProcess.ExitCode,
                task.LastProcess.WasCancelled,
                task.LastProcess.StandardOutputPath,
                task.LastProcess.StandardErrorPath,
                task.LastProcess.ExitCodePath,
                ToDispatchHeartbeatDto(ProcessLogReader.ReadHeartbeat(task.LastProcess))),
        task.LastVerification is null
            ? null
            : new VerificationSummaryDto(
                task.LastVerification.ExitCode,
                task.LastVerification.Succeeded,
                task.LastVerification.CompletedAt,
                task.VerificationHistory.Count),
        task.SubscriptionRetryAfter);
}

public static GoalStageReadinessReportDto ToGoalStageReadinessReportDto(
    Goal goal,
    GoalStageReadinessReport report,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    return new GoalStageReadinessReportDto(
        report.GoalId.Value,
        SummaryText(report.Objective),
        report.Status,
        report.TotalStages,
        report.VerifiedStages,
        report.OpenStages,
        report.BlockedStages,
        report.IsReadyForAcceptance,
        report.Stages.Select(stage => ToTaskStageReadinessDto(goal, stage, agents)).ToList());
}

public static TaskStageReadinessDto ToTaskStageReadinessDto(
    Goal goal,
    TaskStageReadiness stage,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    var taskNumber = ConsoleViews.GetTaskDisplayNumber(goal, stage.TaskId);
    return new TaskStageReadinessDto(
        taskNumber,
        stage.TaskId.Value,
        stage.Stage,
        SummaryText(stage.Description),
        stage.TaskStatus,
        stage.IsAssigned,
        stage.StageStatus,
        stage.LatestEvidence,
        stage.VerificationStatus,
        stage.PendingHumanInputCount,
        TimelineText(stage.Message),
        TimelineText(stage.SuggestedAction),
        ConsoleViews.BuildStageSuggestedCommand(goal, stage, agents));
}

public static VerificationGateDto ToVerificationGateDto(Goal goal, GoalVerificationGate gate)
{
    return new VerificationGateDto(
        gate.GoalId.Value,
        SummaryText(gate.Objective),
        gate.Status,
        gate.IsSatisfied,
        gate.Tasks.Select(task => ToTaskVerificationGateDto(goal, task)).ToList());
}

public static TaskVerificationGateDto ToSelectedTaskVerificationGateDto(Goal goal, GoalVerificationGate gate, TaskSpec task)
{
    var taskGate = gate.Tasks.Single(item => item.TaskId == task.Id);
    return ToTaskVerificationGateDto(goal, taskGate);
}

public static TaskVerificationGateDto ToTaskVerificationGateDto(Goal goal, TaskVerificationGate taskGate)
{
    return new TaskVerificationGateDto(
        ConsoleViews.GetTaskDisplayNumber(goal, taskGate.TaskId),
        taskGate.TaskId.Value,
        taskGate.Role,
        SummaryText(taskGate.Description),
        taskGate.TaskStatus,
        taskGate.GateStatus,
        TimelineText(taskGate.Message));
}

public static VerificationWorklistDto ToVerificationWorklistDto(Goal goal, GoalVerificationWorklist worklist)
{
    return new VerificationWorklistDto(
        worklist.GoalId.Value,
        SummaryText(worklist.Objective),
        worklist.Status,
        worklist.IsSatisfied,
        worklist.OpenCount,
        worklist.Items.Select(item => ToVerificationWorkItemDto(goal, item)).ToList());
}

public static VerificationWorkItemDto ToVerificationWorkItemDto(Goal goal, TaskVerificationWorkItem item)
{
    var taskNumber = ConsoleViews.GetTaskDisplayNumber(goal, item.TaskId);
    return new VerificationWorkItemDto(
        taskNumber,
        item.TaskId.Value,
        item.Role,
        SummaryText(item.Description),
        item.TaskStatus,
        item.GateStatus,
        TimelineText(item.Message),
        TimelineText(item.SuggestedAction),
        ConsoleViews.BuildVerificationSuggestedCommand(taskNumber, item.GateStatus));
}

public static HumanInputWorklistDto ToHumanInputWorklistDto(Goal goal, GoalHumanInputWorklist worklist)
{
    return new HumanInputWorklistDto(
        worklist.GoalId.Value,
        SummaryText(worklist.Objective),
        worklist.Status,
        worklist.OpenCount,
        worklist.Items.Select(item => ToHumanInputWorkItemDto(goal, item)).ToList());
}

public static HumanInputWorkItemDto ToHumanInputWorkItemDto(Goal goal, HumanInputWorkItem item)
{
    int? taskNumber = item.TaskId is null ? null : ConsoleViews.GetTaskDisplayNumber(goal, item.TaskId);
    return new HumanInputWorkItemDto(
        item.RequestId.Value,
        item.TaskId?.Value,
        taskNumber,
        item.Role,
        SummaryText(item.Description),
        item.TaskStatus,
        SummaryText(item.Question),
        item.RequestedAt,
        TimelineText(item.SuggestedAction),
        ConsoleViews.BuildHumanInputSuggestedCommand(item.RequestId));
}

public static NextActionsDto ToNextActionsDto(
    Goal goal,
    GoalNextActions actions,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    return new NextActionsDto(
        actions.GoalId.Value,
        SummaryText(actions.Objective),
        actions.Status,
        actions.Items.Select((item, index) => ToNextActionDto(goal, item, index + 1, agents)).ToList());
}

public static NextActionDto ToNextActionDto(
    Goal goal,
    NextActionItem item,
    int priority,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    int? taskNumber = item.TaskId is null ? null : ConsoleViews.GetTaskDisplayNumber(goal, item.TaskId);
    return new NextActionDto(
        priority,
        item.Kind,
        item.TaskId?.Value,
        taskNumber,
        item.HumanInputRequestId?.Value,
        TimelineText(item.Message),
        ConsoleViews.BuildSuggestedCommand(goal, item, agents),
        ToNextActionControlDto(goal, item, agents));
}

public static NextActionControlDto? ToNextActionControlDto(
    Goal goal,
    NextActionItem item,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    var control = DashboardNextActionControls.Build(goal, item, agentDefinitions: agents);
    return control is null
        ? null
        : new NextActionControlDto(
            control.Label,
            control.Method,
            control.Url,
            control.CostRisk,
            control.CostRecommendation);
}

public static HumanInputDto ToHumanInputDto(AgentOrchestratorKernel kernel, HumanInputRequest request)
{
    var goal = kernel.GetGoal(request.GoalId);
    int? taskNumber = null;
    if (request.TaskId is not null)
    {
        taskNumber = ConsoleViews.GetTaskDisplayNumber(goal, request.TaskId);
    }

    return new HumanInputDto(
        request.Id.Value,
        request.GoalId.Value,
        request.TaskId?.Value,
        taskNumber,
        SummaryText(request.Question),
        request.RequestedAt,
        request.IsCompleted,
        request.Answer is null ? null : SummaryText(request.Answer),
        request.AnsweredAt);
}

private static string SummaryText(string? text) =>
    OutputTextPreview.CreateSummary(text ?? string.Empty).Text;

private static string TimelineText(string text) =>
    OutputTextPreview.CreateTimeline(text).Text;

}
