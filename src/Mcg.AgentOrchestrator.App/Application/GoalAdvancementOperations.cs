using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

/// <summary>
/// Owns automated goal advancement: it decides the next safe action, executes it through the
/// dispatch operations, and reports typed outcomes. It builds no presentation payload and applies
/// no presentation text policy; adapters map <see cref="GoalAdvanceOutcome"/> for their transport.
/// </summary>
public sealed class GoalAdvancementOperations
{
    private const int MaxAutomaticHandoffSteps = 20;

    private readonly GoalDispatchOperations _dispatch;

    public GoalAdvancementOperations()
        : this(new GoalDispatchOperations())
    {
    }

    internal GoalAdvancementOperations(GoalDispatchOperations dispatch)
    {
        _dispatch = dispatch;
    }

    private sealed class DurableDispatchStartFailureException(DispatchProcessStartFailure failure)
        : InvalidOperationException(failure.Reason)
    {
        public DispatchProcessStartFailure Failure { get; } = failure;
    }

    public async Task<GoalAdvanceOutcome> AdvanceGoalAsync(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        OrchestratorWorkspace workspace,
        Goal goal)
    {
        var actions = kernel.BuildNextActions(goal.Id);
        var item = actions.Items.FirstOrDefault();
        if (item is null)
        {
            return new GoalAdvanceOutcome(goal.Id, false, null, NextActionAutomationKind.None, "No next actions are available.", new GoalAdvanceNoPayload());
        }

        var automation = NextActionAutomationPolicy.Build(item);
        var actionDispatchState = EvaluateActionDispatchState(goal, item);
        if (!automation.CanExecute || automation.TaskId is null)
        {
            return new GoalAdvanceOutcome(goal.Id, false, item, automation.Kind, automation.Message, new GoalAdvanceNoPayload(), ActionDispatchState: actionDispatchState);
        }

        GoalAdvanceStepPayload result;
        try
        {
            result = await ExecuteAutomationAsync(kernel, agents, providers, workspace, goal, automation, allowApiExecution: false, allowProcessStart: false);
        }
        catch (DurableDispatchStartFailureException ex)
        {
            return new GoalAdvanceOutcome(
                goal.Id,
                false,
                item,
                automation.Kind,
                ex.Message,
                new GoalAdvanceDispatchStartFailed(ex.Failure),
                StateChanged: true,
                ActionDispatchState: actionDispatchState);
        }
        catch (InvalidOperationException ex)
        {
            return new GoalAdvanceOutcome(goal.Id, false, item, automation.Kind, ex.Message, new GoalAdvanceNoPayload(), ActionDispatchState: actionDispatchState);
        }
        catch (KeyNotFoundException ex)
        {
            return new GoalAdvanceOutcome(goal.Id, false, item, automation.Kind, ex.Message, new GoalAdvanceNoPayload(), ActionDispatchState: actionDispatchState);
        }

        return new GoalAdvanceOutcome(goal.Id, true, item, automation.Kind, automation.Message, result, ActionDispatchState: actionDispatchState);
    }

    /// <summary>
    /// Captures the authoritative dispatch state of the chosen action before the step runs. The goal
    /// and its tasks are mutable and shared with the caller, so an adapter that evaluated this after
    /// execution would observe post-mutation state - a refreshed process sets <c>CompletedAt</c> and
    /// the evaluation then returns null - and would describe an action the operation never saw.
    /// </summary>
    private static DispatchAuthoritativeState? EvaluateActionDispatchState(Goal goal, NextActionItem item) =>
        DispatchRecoveryView.EvaluateState(goal, item, commandLineSnapshot: null);

    public async Task<GoalAdvanceLoopOutcome> AdvanceGoalUntilBlockedAsync(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        OrchestratorWorkspace workspace,
        Goal goal)
    {
        return await AdvanceUntilBlockedAsync(
            kernel,
            goal,
            automation => ExecuteAutomationAsync(kernel, agents, providers, workspace, goal, automation, allowApiExecution: false, allowProcessStart: false),
            automation => automation.Message);
    }

    public GoalAdvanceOutcome AdvanceGoalWithSubscriptions(
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
            return new GoalAdvanceOutcome(goal.Id, false, null, NextActionAutomationKind.None, "No next actions are available.", new GoalAdvanceNoPayload());
        }

        var automation = NextActionAutomationPolicy.Build(item);
        var actionDispatchState = EvaluateActionDispatchState(goal, item);
        if (!automation.CanExecute || automation.TaskId is null)
        {
            return new GoalAdvanceOutcome(goal.Id, false, item, automation.Kind, automation.Message, new GoalAdvanceNoPayload(), ActionDispatchState: actionDispatchState);
        }

        var message = GetSubscriptionAutomationMessage(automation);
        GoalAdvanceStepPayload result;
        try
        {
            result = ExecuteSubscriptionAutomation(kernel, agents, profiles, workspace, goal, automation, allowLargePaidSubscriptionStart, providers);
        }
        catch (DurableDispatchStartFailureException ex)
        {
            return new GoalAdvanceOutcome(
                goal.Id,
                false,
                item,
                automation.Kind,
                ex.Message,
                new GoalAdvanceDispatchStartFailed(ex.Failure),
                StateChanged: true,
                ActionDispatchState: actionDispatchState);
        }
        catch (InvalidOperationException ex)
        {
            return new GoalAdvanceOutcome(goal.Id, false, item, automation.Kind, ex.Message, new GoalAdvanceNoPayload(), ActionDispatchState: actionDispatchState);
        }
        catch (KeyNotFoundException ex)
        {
            return new GoalAdvanceOutcome(goal.Id, false, item, automation.Kind, ex.Message, new GoalAdvanceNoPayload(), ActionDispatchState: actionDispatchState);
        }

        return new GoalAdvanceOutcome(goal.Id, true, item, automation.Kind, message, result, ActionDispatchState: actionDispatchState);
    }

    public GoalAdvanceLoopOutcome AdvanceGoalWithSubscriptionsUntilBlocked(
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
            GetSubscriptionAutomationMessage).GetAwaiter().GetResult();
    }

    private async Task<GoalAdvanceLoopOutcome> AdvanceUntilBlockedAsync(
        AgentOrchestratorKernel kernel,
        Goal goal,
        Func<NextActionAutomationPlan, Task<GoalAdvanceStepPayload>> execute,
        Func<NextActionAutomationPlan, string> messageFor)
    {
        var steps = new List<GoalAdvanceOutcome>();
        NextActionItem? blockingAction = null;
        DispatchAuthoritativeState? blockingActionDispatchState = null;
        DateTimeOffset? continueAfter = null;
        var stopReason = "No next actions are available.";
        var stateChanged = false;
        DispatchProcessStartFailure? failure = null;

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
            var actionDispatchState = EvaluateActionDispatchState(goal, item);
            if (!automation.CanExecute || automation.TaskId is null)
            {
                blockingAction = item;
                blockingActionDispatchState = actionDispatchState;
                stopReason = automation.Message;
                break;
            }

            GoalAdvanceStepPayload result;
            try
            {
                result = await execute(automation);
            }
            catch (DurableDispatchStartFailureException ex)
            {
                blockingAction = item;
                blockingActionDispatchState = actionDispatchState;
                stopReason = ex.Message;
                stateChanged = true;
                failure = ex.Failure;
                break;
            }
            catch (InvalidOperationException ex)
            {
                blockingAction = item;
                blockingActionDispatchState = actionDispatchState;
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
                blockingAction = item;
                blockingActionDispatchState = actionDispatchState;
                stopReason = ex.Message;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                blockingAction = item;
                blockingActionDispatchState = actionDispatchState;
                stopReason = $"Automatic handoff stopped: {ex.Message}";
                break;
            }

            var step = new GoalAdvanceOutcome(goal.Id, true, item, automation.Kind, messageFor(automation), result, ActionDispatchState: actionDispatchState);
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
            blockingAction = actions.Items.FirstOrDefault();
            blockingActionDispatchState = blockingAction is null ? null : EvaluateActionDispatchState(goal, blockingAction);
            stopReason = $"Stopped after {MaxAutomaticHandoffSteps} automated step(s); run continuation again if more safe actions remain.";
        }

        return new GoalAdvanceLoopOutcome(
            goal.Id,
            steps.Count > 0,
            steps.Count,
            stopReason,
            blockingAction,
            steps,
            ContinueAfter: continueAfter,
            StateChanged: stateChanged,
            Failure: failure,
            BlockingActionDispatchState: blockingActionDispatchState);
    }

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

    private async Task<GoalAdvanceStepPayload> ExecuteAutomationAsync(
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
                new GoalAdvanceTaskUpdated(AdvanceRefreshRunningProcess(kernel, goal, automation.TaskId!)),
            NextActionAutomationKind.StartRecordedDispatch =>
                allowProcessStart
                    ? new GoalAdvanceTaskUpdated(AdvanceStartRecordedDispatch(kernel, workspace, goal, automation.TaskId!))
                    : throw new InvalidOperationException(
                        $"Automatic continuation stopped before starting recorded dispatch for task {automation.TaskId!.Value[..8]}; use Start prepared work or subscription advance for an explicit worker process start."),
            NextActionAutomationKind.DelegatePendingTask =>
                new GoalAdvanceDelegationPlanned(RefineAndActivateGoal(kernel, agents, goal)),
            _ => new GoalAdvanceNoPayload()
        };
    }

    private GoalAdvanceStepPayload ExecuteSubscriptionAutomation(
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
                new GoalAdvanceDispatchPrepared(ExecuteSubscriptionRunAssignedTask(
                    kernel, agents, profiles, workspace, goal, automation.TaskId!, allowLargePaidSubscriptionStart, providers)),
            NextActionAutomationKind.RefreshRunningProcess =>
                new GoalAdvanceTaskUpdated(AdvanceRefreshRunningProcess(kernel, goal, automation.TaskId!)),
            NextActionAutomationKind.StartRecordedDispatch =>
                new GoalAdvanceTaskUpdated(ExecuteSubscriptionStartRecordedDispatch(
                    kernel,
                    agents,
                    profiles,
                    workspace,
                    goal,
                    automation.TaskId!,
                    allowLargePaidSubscriptionStart,
                    providers)),
            NextActionAutomationKind.DelegatePendingTask =>
                new GoalAdvanceDelegationPlanned(RefineAndActivateGoal(kernel, agents, goal)),
            _ => new GoalAdvanceNoPayload()
        };
    }

    private async Task<GoalAdvanceStepPayload> AdvanceRunAssignedTaskAsync(
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
            GoalDispatchOperations.EnsureRefinedForSpecConsumer(kernel, workspace, providers, goal);
        var agent = AssignedAgentResolver.Resolve(task, agents);
        if (agent.ExecutionPolicy is AgentExecutionPolicy.SubscriptionOnly or AgentExecutionPolicy.PreferSubscription)
        {
            return new GoalAdvanceDispatchPrepared(_dispatch.SubscriptionDispatchTask(
                kernel, workspace, goal, task, agents, WorkerProfileStore.Load(workspace.WorkerProfilePath), providers: providers));
        }

        if (agent.ExecutionPolicy == AgentExecutionPolicy.AnyAvailable)
        {
            try
            {
                return new GoalAdvanceDispatchPrepared(_dispatch.SubscriptionDispatchTask(
                    kernel, workspace, goal, task, agents, WorkerProfileStore.Load(workspace.WorkerProfilePath), providers: providers));
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
        _ = await new AgentTaskRunner(kernel, agents, providers, goalDiffProvider: diffProvider).RunAsync(goal.Id, taskId);
        return new GoalAdvanceTaskUpdated(goal.Tasks.Single(task => task.Id == taskId));
    }

    public async Task<GoalAdvanceStepPayload> RunAssignedTaskAsync(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskId taskId) =>
        await AdvanceRunAssignedTaskAsync(kernel, agents, providers, workspace, goal, taskId);

    public async Task<TaskSpec> ApiRunAssignedTaskAsync(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskId taskId)
    {
        var task = goal.Tasks.Single(task => task.Id == taskId);
        if (goal.RefinedSpec is not null || task.RequiredRole != AgentRole.Researcher)
            GoalDispatchOperations.EnsureRefinedForSpecConsumer(kernel, workspace, providers, goal);
        var agent = AssignedAgentResolver.Resolve(task, agents);
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
        _ = await new AgentTaskRunner(kernel, agents, providers, goalDiffProvider: diffProvider).RunAsync(goal.Id, taskId);
        return goal.Tasks.Single(task => task.Id == taskId);
    }

    private static DelegationPlan RefineAndActivateGoal(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        Goal goal)
    {
        return kernel.ActivateGoal(goal.Id, agents);
    }

    private WorkerProfileDispatchResult ExecuteSubscriptionRunAssignedTask(
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

        return _dispatch.SubscriptionDispatchTask(
            kernel,
            workspace,
            goal,
            task,
            agents,
            profiles,
            providers: providers);
    }

    private TaskSpec ExecuteSubscriptionStartRecordedDispatch(
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
        _dispatch.RefreshPreparedDispatchBeforeStart(kernel, workspace, goal, task, agents, profiles, providers);
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

    public TaskSpec AdvanceRefreshRunningProcess(AgentOrchestratorKernel kernel, Goal goal, TaskId taskId)
    {
        new BackgroundDispatchRunner().RefreshLatestProcess(kernel, goal.Id, taskId);
        return goal.Tasks.Single(task => task.Id == taskId);
    }

    public TaskSpec AdvanceStartRecordedDispatch(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskId taskId,
        bool refreshBeforeStart = true)
    {
        if (refreshBeforeStart)
        {
            _dispatch.RefreshPreparedDispatchBeforeStart(kernel, workspace, goal, goal.Tasks.Single(task => task.Id == taskId));
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
            throw new DurableDispatchStartFailureException(new DispatchProcessStartFailure(taskId, failureReason));
        }

        return goal.Tasks.Single(task => task.Id == taskId);
    }
}
