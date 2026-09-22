using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

/// <summary>
/// Dashboard transport adapter for goal advancement. Advancement itself is owned by
/// <see cref="GoalAdvancementOperations"/>; this file only converts its typed outcomes into
/// dashboard DTOs and applies the dashboard's timeline-text policy.
/// </summary>
internal static partial class GoalManagementCommandService
{
public static async Task<AdvanceResultDto> AdvanceGoalAsync(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog workerProfiles,
    IModelProviderRegistry providers,
    OrchestratorWorkspace workspace,
    Goal goal)
{
    var outcome = await new GoalAdvancementOperations()
        .AdvanceGoalAsync(kernel, agents, providers, workspace, goal);
    return ToAdvanceResultDto(goal, outcome, workerProfiles, agents);
}

public static async Task<AdvanceLoopResultDto> AdvanceGoalUntilBlockedAsync(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    IModelProviderRegistry providers,
    OrchestratorWorkspace workspace,
    Goal goal)
{
    var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
    var outcome = await new GoalAdvancementOperations()
        .AdvanceGoalUntilBlockedAsync(kernel, agents, providers, workspace, goal);
    return ToAdvanceLoopResultDto(goal, outcome, profiles, agents);
}

public static AdvanceResultDto AdvanceGoalWithSubscriptions(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    OrchestratorWorkspace workspace,
    Goal goal,
    bool allowLargePaidSubscriptionStart = false,
    IModelProviderRegistry? providers = null)
{
    var outcome = new GoalAdvancementOperations().AdvanceGoalWithSubscriptions(
        kernel, agents, profiles, workspace, goal, allowLargePaidSubscriptionStart, providers);
    return ToAdvanceResultDto(goal, outcome, profiles, agents);
}

public static AdvanceLoopResultDto AdvanceGoalWithSubscriptionsUntilBlocked(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    OrchestratorWorkspace workspace,
    Goal goal,
    bool allowLargePaidSubscriptionStart = false,
    IModelProviderRegistry? providers = null)
{
    var outcome = new GoalAdvancementOperations().AdvanceGoalWithSubscriptionsUntilBlocked(
        kernel, agents, profiles, workspace, goal, allowLargePaidSubscriptionStart, providers);
    return ToAdvanceLoopResultDto(goal, outcome, profiles, agents);
}

internal static AdvanceResultDto ToAdvanceResultDto(
    Goal goal,
    GoalAdvanceOutcome outcome,
    WorkerProfileCatalog workerProfiles,
    IReadOnlyList<AgentDefinition>? agents)
{
    // The action is rendered against the dispatch state the operation captured before it executed,
    // not the live goal, because execution mutates the task this DTO describes.
    return new AdvanceResultDto(
        outcome.GoalId.Value,
        outcome.Executed,
        outcome.Action is null
            ? null
            : DashboardResponseMapper.ToNextActionDtoWithEvaluatedState(
                goal, outcome.Action, 1, workerProfiles, agents, outcome.ActionDispatchState),
        outcome.AutomationKind,
        TimelineMessage(outcome.Message),
        ToStepResult(goal, outcome.Payload),
        StateChanged: outcome.StateChanged);
}

internal static AdvanceLoopResultDto ToAdvanceLoopResultDto(
    Goal goal,
    GoalAdvanceLoopOutcome outcome,
    WorkerProfileCatalog workerProfiles,
    IReadOnlyList<AgentDefinition>? agents)
{
    return new AdvanceLoopResultDto(
        outcome.GoalId.Value,
        outcome.Executed,
        outcome.StepCount,
        TimelineMessage(outcome.StopReason),
        outcome.BlockingAction is null
            ? null
            : DashboardResponseMapper.ToNextActionDtoWithEvaluatedState(
                goal, outcome.BlockingAction, 1, workerProfiles, agents, outcome.BlockingActionDispatchState),
        outcome.Steps.Select(step => ToAdvanceResultDto(goal, step, workerProfiles, agents)).ToList(),
        ContinueAfter: outcome.ContinueAfter,
        StateChanged: outcome.StateChanged,
        Failure: ToDispatchProcessStartFailureDto(goal, outcome.Failure));
}

private static object? ToStepResult(Goal goal, GoalAdvanceStepPayload payload) => payload switch
{
    GoalAdvanceDispatchPrepared prepared => DashboardResponseMapper.ToProfileDispatchDto(goal, prepared.Dispatch),
    GoalAdvanceTaskUpdated updated => DashboardResponseMapper.ToTaskDetailDto(goal, updated.Task),
    GoalAdvanceDelegationPlanned planned => DashboardResponseMapper.ToDelegationPlanDto(planned.Plan),
    GoalAdvanceDispatchStartFailed failed => ToDispatchProcessStartFailureDto(goal, failed.Failure),
    _ => null
};

internal static DispatchProcessStartFailureDto? ToDispatchProcessStartFailureDto(Goal goal, DispatchProcessStartFailure? failure) =>
    failure is null ? null : new DispatchProcessStartFailureDto(goal.Id.Value, failure.TaskId.Value, failure.Reason);

private static string TimelineMessage(string message) =>
    OutputTextPreview.CreateTimeline(message).Text;
}
