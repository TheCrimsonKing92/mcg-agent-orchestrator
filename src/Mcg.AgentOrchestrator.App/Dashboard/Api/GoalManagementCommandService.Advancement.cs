using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class GoalManagementCommandService
{
private const int MaxAutomaticHandoffSteps = 20;

private sealed class DurableDispatchStartFailureException(DispatchProcessStartFailureDto failure)
    : InvalidOperationException(failure.Reason)
{
    public DispatchProcessStartFailureDto Failure { get; } = failure;
}

public static async Task<AdvanceResultDto> AdvanceGoalAsync(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog workerProfiles,
    IModelProviderRegistry providers,
    OrchestratorWorkspace workspace,
    Goal goal)
{
    var actions = kernel.BuildNextActions(goal.Id);
    var item = actions.Items.FirstOrDefault();
    if (item is null)
    {
        return new AdvanceResultDto(goal.Id.Value, false, null, NextActionAutomationKind.None, TimelineMessage("No next actions are available."), null);
    }

    var automation = NextActionAutomationPolicy.Build(item);
    var action = DashboardResponseMapper.ToNextActionDto(goal, item, 1, workerProfiles, agents);
    if (!automation.CanExecute || automation.TaskId is null)
    {
        return new AdvanceResultDto(goal.Id.Value, false, action, automation.Kind, TimelineMessage(automation.Message), null);
    }

    object? result;
    try
    {
        result = await ExecuteAutomationAsync(kernel, agents, providers, workspace, goal, automation, allowApiExecution: false, allowProcessStart: false);
    }
    catch (DurableDispatchStartFailureException ex)
    {
        return new AdvanceResultDto(
            goal.Id.Value,
            false,
            action,
            automation.Kind,
            TimelineMessage(ex.Message),
            ex.Failure,
            StateChanged: true);
    }
    catch (InvalidOperationException ex)
    {
        return new AdvanceResultDto(goal.Id.Value, false, action, automation.Kind, TimelineMessage(ex.Message), null);
    }
    catch (KeyNotFoundException ex)
    {
        return new AdvanceResultDto(goal.Id.Value, false, action, automation.Kind, TimelineMessage(ex.Message), null);
    }

    return new AdvanceResultDto(goal.Id.Value, true, action, automation.Kind, TimelineMessage(automation.Message), result);
}

public static async Task<AdvanceLoopResultDto> AdvanceGoalUntilBlockedAsync(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    IModelProviderRegistry providers,
    OrchestratorWorkspace workspace,
    Goal goal)
{
    var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
    return await AdvanceUntilBlockedAsync(
        kernel,
        goal,
        automation => ExecuteAutomationAsync(kernel, agents, providers, workspace, goal, automation, allowApiExecution: false, allowProcessStart: false),
        automation => automation.Message,
        profiles,
        agents);
}

private static async Task<object?> AdvanceRunAssignedTaskAsync(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    IModelProviderRegistry providers,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskId taskId,
    bool allowApiExecution = true,
    bool allowApiFallback = false)
{
    var task = goal.Tasks.Single(task => task.Id == taskId);
    if (goal.RefinedSpec is not null || task.RequiredRole != AgentRole.Researcher)
        EnsureRefinedForSpecConsumer(kernel, workspace, providers, goal);
    var agent = ResolveAssignedAgent(task, agents);
    if (agent.ExecutionPolicy is AgentExecutionPolicy.SubscriptionOnly or AgentExecutionPolicy.PreferSubscription)
    {
        return DashboardResponseMapper.ToProfileDispatchDto(
            goal,
            SubscriptionDispatchTask(kernel, workspace, goal, task, agents, WorkerProfileStore.Load(workspace.WorkerProfilePath), providers: providers));
    }

    if (agent.ExecutionPolicy == AgentExecutionPolicy.AnyAvailable)
    {
        try
        {
            return DashboardResponseMapper.ToProfileDispatchDto(
                goal,
                SubscriptionDispatchTask(kernel, workspace, goal, task, agents, WorkerProfileStore.Load(workspace.WorkerProfilePath), providers: providers));
        }
        catch (InvalidOperationException ex)
        {
            if (!allowApiExecution || !allowApiFallback || !CanFallBackToApiAfterSubscriptionFailure(task))
            {
                throw new InvalidOperationException(
                    $"Task run stopped before API fallback for task {task.Id.Value[..8]}; subscription handoff was unavailable: {ex.Message}",
                    ex);
            }

            // Fall back to API-backed execution below.
        }
        catch (KeyNotFoundException ex)
        {
            if (!allowApiExecution || !allowApiFallback || !CanFallBackToApiAfterSubscriptionFailure(task))
            {
                throw new InvalidOperationException(
                    $"Task run stopped before API fallback for task {task.Id.Value[..8]}; subscription handoff was unavailable: {ex.Message}",
                    ex);
            }

            // Fall back to API-backed execution below.
        }
    }

    if (!allowApiExecution)
    {
        throw new InvalidOperationException(
            $"Automatic continuation stopped before API-backed execution for task {task.Id.Value[..8]}; use the task Run control for an explicit model call.");
    }

    var diffProvider = GoalWorktreeDiffProvider.Create(workspace.ExecutionDirectory);
    var result = await new AgentTaskRunner(kernel, agents, providers, goalDiffProvider: diffProvider).RunAsync(goal.Id, taskId);
    return DashboardResponseMapper.ToTaskDetailDto(result.Goal, goal.Tasks.Single(task => task.Id == taskId));
}

private static async Task<object?> AdvanceApiRunAssignedTaskAsync(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    IModelProviderRegistry providers,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskId taskId)
{
    var task = goal.Tasks.Single(task => task.Id == taskId);
    if (goal.RefinedSpec is not null || task.RequiredRole != AgentRole.Researcher)
        EnsureRefinedForSpecConsumer(kernel, workspace, providers, goal);
    var agent = ResolveAssignedAgent(task, agents);
    if (!AgentExecutionPolicies.AllowsApi(agent.ExecutionPolicy))
    {
        throw new InvalidOperationException($"Agent '{agent.Name}' is configured for subscription execution only.");
    }

    if (agent.ExecutionPolicy != AgentExecutionPolicy.ApiOnly && !CanFallBackToApiAfterSubscriptionFailure(task))
    {
        throw new InvalidOperationException(
            $"Explicit API execution for task {task.Id.Value[..8]} is only available before subscription work, model output, or verification evidence exists.");
    }

    var diffProvider = GoalWorktreeDiffProvider.Create(workspace.ExecutionDirectory);
    var result = await new AgentTaskRunner(kernel, agents, providers, goalDiffProvider: diffProvider).RunAsync(goal.Id, taskId);
    return DashboardResponseMapper.ToTaskDetailDto(result.Goal, goal.Tasks.Single(task => task.Id == taskId));
}

private static AgentDefinition ResolveAssignedAgent(TaskSpec task, IReadOnlyList<AgentDefinition> agents)
{
    if (task.AssignedAgentId is null)
    {
        throw new InvalidOperationException($"Task '{task.Id}' is not assigned to an agent.");
    }

    return agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId)
        ?? throw new KeyNotFoundException($"Assigned agent '{task.AssignedAgentId}' was not found.");
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
    var actions = kernel.BuildNextActions(goal.Id);
    var item = actions.Items.FirstOrDefault();
    if (item is null)
    {
        return new AdvanceResultDto(goal.Id.Value, false, null, NextActionAutomationKind.None, TimelineMessage("No next actions are available."), null);
    }

    var automation = NextActionAutomationPolicy.Build(item);
    var action = DashboardResponseMapper.ToNextActionDto(goal, item, 1, profiles, agents);
    if (!automation.CanExecute || automation.TaskId is null)
    {
        return new AdvanceResultDto(goal.Id.Value, false, action, automation.Kind, TimelineMessage(automation.Message), null);
    }

    var message = GetSubscriptionAutomationMessage(automation);
    object? result;
    try
    {
        result = ExecuteSubscriptionAutomation(kernel, agents, profiles, workspace, goal, automation, allowLargePaidSubscriptionStart, providers);
    }
    catch (DurableDispatchStartFailureException ex)
    {
        return new AdvanceResultDto(
            goal.Id.Value,
            false,
            action,
            automation.Kind,
            TimelineMessage(ex.Message),
            ex.Failure,
            StateChanged: true);
    }
    catch (InvalidOperationException ex)
    {
        return new AdvanceResultDto(goal.Id.Value, false, action, automation.Kind, TimelineMessage(ex.Message), null);
    }
    catch (KeyNotFoundException ex)
    {
        return new AdvanceResultDto(goal.Id.Value, false, action, automation.Kind, TimelineMessage(ex.Message), null);
    }

    return new AdvanceResultDto(goal.Id.Value, true, action, automation.Kind, TimelineMessage(message), result);
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
    return AdvanceUntilBlockedAsync(
        kernel,
        goal,
        automation => Task.FromResult(ExecuteSubscriptionAutomation(kernel, agents, profiles, workspace, goal, automation, allowLargePaidSubscriptionStart, providers)),
        GetSubscriptionAutomationMessage,
        profiles,
        agents).GetAwaiter().GetResult();
}

private static async Task<AdvanceLoopResultDto> AdvanceUntilBlockedAsync(
    AgentOrchestratorKernel kernel,
    Goal goal,
    Func<NextActionAutomationPlan, Task<object?>> execute,
    Func<NextActionAutomationPlan, string> messageFor,
    WorkerProfileCatalog workerProfiles,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    var steps = new List<AdvanceResultDto>();
    NextActionDto? blockingAction = null;
    DateTimeOffset? continueAfter = null;
    var stopReason = "No next actions are available.";
    var stateChanged = false;
    DispatchProcessStartFailureDto? failure = null;

    for (var index = 0; index < MaxAutomaticHandoffSteps; index++)
    {
        var actions = kernel.BuildNextActions(goal.Id);
        var item = actions.Items.FirstOrDefault();
        if (item is null)
        {
            stopReason = "No next actions are available.";
            break;
        }

        var automation = NextActionAutomationPolicy.Build(item);
        var action = DashboardResponseMapper.ToNextActionDto(goal, item, 1, workerProfiles, agents);
        if (!automation.CanExecute || automation.TaskId is null)
        {
            blockingAction = action;
            stopReason = automation.Message;
            break;
        }

        object? result;
        try
        {
            result = await execute(automation);
        }
        catch (DurableDispatchStartFailureException ex)
        {
            blockingAction = action;
            stopReason = ex.Message;
            stateChanged = true;
            failure = ex.Failure;
            break;
        }
        catch (InvalidOperationException ex)
        {
            blockingAction = action;
            if (TryGetSubscriptionRetryWindow(goal, automation, out var retryAfter, out var retryReason))
            {
                continueAfter = retryAfter;
                stopReason = retryReason;
            }
            else
            {
                stopReason = ex.Message;
            }

            break;
        }
        catch (KeyNotFoundException ex)
        {
            blockingAction = action;
            stopReason = ex.Message;
            break;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            blockingAction = action;
            stopReason = $"Automatic handoff stopped: {ex.Message}";
            break;
        }

        var step = new AdvanceResultDto(goal.Id.Value, true, action, automation.Kind, TimelineMessage(messageFor(automation)), result);
        steps.Add(step);

        var pauseReason = GetAutomaticHandoffPauseReason(kernel.GetTask(goal.Id, automation.TaskId), automation.Kind);
        if (pauseReason is not null)
        {
            stopReason = pauseReason;
            break;
        }
    }

    if (steps.Count == MaxAutomaticHandoffSteps)
    {
        var actions = kernel.BuildNextActions(goal.Id);
        var item = actions.Items.FirstOrDefault();
        blockingAction = item is null ? null : DashboardResponseMapper.ToNextActionDto(goal, item, 1, workerProfiles, agents);
        stopReason = $"Stopped after {MaxAutomaticHandoffSteps} automated step(s); run continuation again if more safe actions remain.";
    }

    return new AdvanceLoopResultDto(
        goal.Id.Value,
        steps.Count > 0,
        steps.Count,
        TimelineMessage(stopReason),
        blockingAction,
        steps,
        ContinueAfter: continueAfter,
        StateChanged: stateChanged,
        Failure: failure);
}

private static string TimelineMessage(string message) =>
    OutputTextPreview.CreateTimeline(message).Text;

private static bool TryGetSubscriptionRetryWindow(
    Goal goal,
    NextActionAutomationPlan automation,
    out DateTimeOffset retryAfter,
    out string reason)
{
    retryAfter = default;
    reason = string.Empty;
    if (automation.Kind != NextActionAutomationKind.RunAssignedTask || automation.TaskId is null)
    {
        return false;
    }

    var task = goal.Tasks.FirstOrDefault(task => task.Id == automation.TaskId);
    if (task is null || !DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, DateTimeOffset.UtcNow, out retryAfter))
    {
        return false;
    }

    reason = $"Subscription retry window is deferred for task {task.Id.Value[..8]}; retry after {retryAfter:u}.";
    return true;
}

private static async Task<object?> ExecuteAutomationAsync(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    IModelProviderRegistry providers,
    OrchestratorWorkspace workspace,
    Goal goal,
    NextActionAutomationPlan automation,
    bool allowApiExecution = true,
    bool allowProcessStart = true)
{
    return automation.Kind switch
    {
        NextActionAutomationKind.RunAssignedTask =>
            await AdvanceRunAssignedTaskAsync(kernel, agents, providers, workspace, goal, automation.TaskId!, allowApiExecution),
        NextActionAutomationKind.RefreshRunningProcess =>
            AdvanceRefreshRunningProcess(kernel, goal, automation.TaskId!),
        NextActionAutomationKind.StartRecordedDispatch =>
            allowProcessStart
                ? AdvanceStartRecordedDispatch(kernel, workspace, goal, automation.TaskId!)
                : throw new InvalidOperationException(
                    $"Automatic continuation stopped before starting recorded dispatch for task {automation.TaskId!.Value[..8]}; use Start prepared work or subscription advance for an explicit worker process start."),
        NextActionAutomationKind.DelegatePendingTask =>
            DashboardResponseMapper.ToDelegationPlanDto(RefineAndActivateGoal(kernel, agents, providers, workspace, goal)),
        _ => null
    };
}

private static object? ExecuteSubscriptionAutomation(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    OrchestratorWorkspace workspace,
    Goal goal,
    NextActionAutomationPlan automation,
    bool allowLargePaidSubscriptionStart,
    IModelProviderRegistry? providers = null)
{
    return automation.Kind switch
    {
        NextActionAutomationKind.RunAssignedTask =>
            ExecuteSubscriptionRunAssignedTask(kernel, agents, profiles, workspace, goal, automation.TaskId!, allowLargePaidSubscriptionStart, providers),
        NextActionAutomationKind.RefreshRunningProcess =>
            AdvanceRefreshRunningProcess(kernel, goal, automation.TaskId!),
        NextActionAutomationKind.StartRecordedDispatch =>
            ExecuteSubscriptionStartRecordedDispatch(
                kernel,
                agents,
                profiles,
                workspace,
                goal,
                automation.TaskId!,
                allowLargePaidSubscriptionStart,
                providers),
        NextActionAutomationKind.DelegatePendingTask =>
            DashboardResponseMapper.ToDelegationPlanDto(RefineAndActivateGoal(
                kernel,
                agents,
                providers ?? new InMemoryModelProviderRegistry([]),
                workspace,
                goal)),
        _ => null
    };
}

private static DelegationPlan RefineAndActivateGoal(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    IModelProviderRegistry providers,
    OrchestratorWorkspace workspace,
    Goal goal)
{
    return kernel.ActivateGoal(goal.Id, agents);
}

private static ProfileDispatchDto ExecuteSubscriptionRunAssignedTask(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskId taskId,
    bool allowLargePaidSubscriptionStart,
    IModelProviderRegistry? providers = null)
{
    var task = goal.Tasks.Single(task => task.Id == taskId);
    SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
        SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
            goal,
            agents,
            profiles,
            item => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(kernel, goal, item, agents),
            task),
        allowLargePaidSubscriptionStart);

    return DashboardResponseMapper.ToProfileDispatchDto(
        goal,
        SubscriptionDispatchTask(
            kernel,
            workspace,
            goal,
            task,
            agents,
            profiles,
            providers: providers));
}

private static TaskDetailDto ExecuteSubscriptionStartRecordedDispatch(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskId taskId,
    bool allowLargePaidSubscriptionStart,
    IModelProviderRegistry? providers = null)
{
    var task = goal.Tasks.Single(task => task.Id == taskId);
    SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
        SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(kernel, goal, task),
        allowLargePaidSubscriptionStart);
    RefreshPreparedDispatchBeforeStart(kernel, workspace, goal, task, agents, profiles, providers);
    SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
        SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(kernel, goal, task),
        allowLargePaidSubscriptionStart);

    return AdvanceStartRecordedDispatch(kernel, workspace, goal, taskId, refreshBeforeStart: false);
}

private static bool CanFallBackToApiAfterSubscriptionFailure(TaskSpec task)
{
    return task.Status == WorkTaskStatus.Assigned &&
        task.LastDispatch is null &&
        task.LastProcess is null &&
        task.LastExecution is null &&
        task.LastVerification is null &&
        task.SubscriptionRetryAfter is null;
}

private static string GetSubscriptionAutomationMessage(NextActionAutomationPlan automation)
{
    return automation.Kind == NextActionAutomationKind.RunAssignedTask
        ? "Prepared subscription-backed dispatch for the assigned task."
        : automation.Message;
}

private static string? GetAutomaticHandoffPauseReason(TaskSpec task, NextActionAutomationKind kind)
{
    if ((kind is NextActionAutomationKind.StartRecordedDispatch or NextActionAutomationKind.RefreshRunningProcess) &&
        task.LastProcess is { IsRunning: true })
    {
        return $"Background work is still running for task {task.Id.Value[..8]}; continue after it exits.";
    }

    return null;
}

public static TaskDetailDto AdvanceRefreshRunningProcess(AgentOrchestratorKernel kernel, Goal goal, TaskId taskId)
{
    new BackgroundDispatchRunner().RefreshLatestProcess(kernel, goal.Id, taskId);
    return DashboardResponseMapper.ToTaskDetailDto(goal, goal.Tasks.Single(task => task.Id == taskId));
}

public static TaskDetailDto AdvanceStartRecordedDispatch(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskId taskId,
    bool refreshBeforeStart = true)
{
    if (refreshBeforeStart)
    {
        RefreshPreparedDispatchBeforeStart(kernel, workspace, goal, goal.Tasks.Single(task => task.Id == taskId));
    }

    var startResult = new BackgroundDispatchRunner().TryStartLatestDispatch(kernel, goal.Id, taskId, workspace.LogDirectory);
    if (startResult.RecoveryAction is { } recoveryAction)
    {
        throw new InvalidOperationException(recoveryAction.Reason);
    }

    if (startResult.RequeueSkipped)
    {
        throw new InvalidOperationException("Dispatch start was skipped after interrupted-dispatch state changed.");
    }

    if (startResult.FailureReason is { } failureReason)
    {
        throw new DurableDispatchStartFailureException(
            new DispatchProcessStartFailureDto(goal.Id.Value, taskId.Value, failureReason));
    }

    return DashboardResponseMapper.ToTaskDetailDto(goal, goal.Tasks.Single(task => task.Id == taskId));
}
}
