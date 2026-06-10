using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private static async Task<IResult> HandleGoalsAsync(HttpContext context, DashboardEndpointServices services)
    {
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var agents = services.LoadAgentCatalog().Agents;
            var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
            var submission = DashboardRequestParser.ParseCreateGoalSubmission(await ReadRequestBodyAsync(context.Request));
            return await MutateAsync(
                services,
                async (current, saveCheckpoint) =>
                {
                    var isSimple = submission.Workflow?.Equals("simple", StringComparison.OrdinalIgnoreCase) is true;
                    var goal = isSimple
                        ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(current, agents, submission.Objective)
                        : GoalLifecycleCommands.CreateAndActivateGoal(current, agents, submission.Objective);

                    if (submission.AutoHandoff)
                    {
                        try
                        {
                            await saveCheckpoint();
                            var handoff = GoalManagementCommandService.AdvanceGoalWithSubscriptionsUntilBlocked(
                                current,
                                agents,
                                profiles,
                                services.Workspace,
                                goal);
                            if (DashboardContinuationService.ShouldContinueWatching(handoff))
                            {
                                services.Continuations.StartSubscriptionWatch(services, handoff.GoalId);
                            }

                            await saveCheckpoint();
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Console.WriteLine($"Automatic subscription handoff did not start for goal {goal.Id.Value}: {ex.Message}");
                        }
                    }

                    var responseGoal = current.Goals.Single(currentGoal => currentGoal.Id == goal.Id);
                    return Json(BuildCreatedGoalResponse(current, responseGoal), StatusCodes.Status201Created);
                },
                context.RequestAborted);
        }

        var current = await LoadAsync(services, context.RequestAborted);
        return Json(current.Goals.Select(DashboardResponseMapper.ToGoalSummary).ToList());
    }

    private static GoalDetailDto BuildCreatedGoalResponse(AgentOrchestratorKernel current, Goal goal)
    {
        try
        {
            return DashboardResponseMapper.ToGoalDetailDto(current, goal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"Created goal {goal.Id.Value} but could not build verification summary: {ex.Message}");
            return new GoalDetailDto(
                DashboardResponseMapper.ToGoalSummary(goal),
                goal.Tasks.Select(task => DashboardResponseMapper.ToTaskSummaryDto(goal, task)).ToList(),
                VerificationSatisfied: false);
        }
    }

    private static async Task<IResult> QueryGoalsAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        return Json(current.Goals.Select(DashboardResponseMapper.ToGoalSummary).ToList());
    }

    private static async Task<IResult> GetGoalAsync(string goalId, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services);
        var goal = ResolveGoal(current, goalId);
        return Json(DashboardResponseMapper.ToGoalDetailDto(current, goal));
    }

    private static async Task<IResult> GetGoalTranscriptAsync(string goalId, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services);
        var goal = ResolveGoal(current, goalId);
        return Text(GoalTranscriptRenderer.Render(current, goal), "text/markdown; charset=utf-8");
    }

    private static async Task<IResult> GetGoalWorkSummaryAsync(string goalId, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services);
        var goal = ResolveGoal(current, goalId);
        return Json(DashboardResponseMapper.ToGoalWorkSummaryDto(current, goal));
    }

    private static async Task<IResult> GetSubscriptionPlanAsync(string goalId, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services);
        var goal = ResolveGoal(current, goalId);
        var agents = services.LoadAgentCatalog().Agents;
        var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        return Json(DashboardResponseMapper.BuildSubscriptionPlan(
            goal,
            agents,
            profiles,
            task => current.BuildTaskBrief(goal.Id, task.Id).Content.Length));
    }

    private static async Task<IResult> AddTaskAsync(HttpContext context, string goalId, DashboardEndpointServices services)
    {
        var agents = services.LoadAgentCatalog().Agents;
        var submission = DashboardRequestParser.ParseAddTaskSubmission(await ReadRequestBodyAsync(context.Request));
        return await MutateAsync(
            services,
            current =>
            {
                var goal = ResolveGoal(current, goalId);
                var task = current.AddTask(
                    goal.Id,
                    submission.Role,
                    submission.Description,
                    submission.Delegate is false ? null : agents,
                    submission.VerificationPlan);
                return Task.FromResult(Json(DashboardResponseMapper.ToTaskDetailDto(goal, task), StatusCodes.Status201Created));
            },
            context.RequestAborted);
    }

    private static async Task<IResult> AskGoalAsync(HttpContext context, string goalId, DashboardEndpointServices services)
    {
        var ask = DashboardRequestParser.ParseAskSubmission(await ReadRequestBodyAsync(context.Request));
        return await MutateAsync(
            services,
            current =>
            {
                var goal = ResolveGoal(current, goalId);
                var request = current.RequestHumanInput(goal.Id, null, ask.Question);
                return Task.FromResult(Json(DashboardResponseMapper.ToHumanInputDto(current, request)));
            },
            context.RequestAborted);
    }

    private static async Task<IResult> HandleTaskOperationAsync(
        HttpContext context,
        string goalId,
        string taskId,
        string operation,
        DashboardEndpointServices services)
    {
        if (context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var current = await LoadAsync(services, context.RequestAborted);
            var goal = ResolveGoal(current, goalId);
            var task = OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, taskId);
            return operation.ToLowerInvariant() switch
            {
                "brief" => Text(current.BuildTaskBrief(goal.Id, task.Id).Content, "text/markdown; charset=utf-8"),
                "verifications" => Json(DashboardResponseMapper.ToVerificationHistoryDto(goal, task)),
                "verification-plan" => Json(DashboardResponseMapper.ToTaskVerificationPlanDto(goal, task)),
                "gate" => Json(DashboardResponseMapper.ToSelectedTaskVerificationGateDto(goal, current.BuildVerificationGate(goal.Id), task)),
                "timeline" => Json(DashboardResponseMapper.ToTaskTimelineDto(goal, task)),
                "logs" => Json(DashboardResponseMapper.ToProcessLogDto(goal, task)),
                _ => Text("not found", "text/plain; charset=utf-8", StatusCodes.Status404NotFound)
            };
        }

        var body = await ReadRequestBodyAsync(context.Request);
        var agents = services.LoadAgentCatalog().Agents;
        var confirmation = RequireTaskRunConfirmation(context, operation);
        confirmation ??= RequireDispatchStartConfirmation(context, operation);
        if (confirmation is not null)
        {
            return confirmation;
        }

        return await MutateAsync(
            services,
            async current =>
            {
                var goal = ResolveGoal(current, goalId);
                var task = OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, taskId);
                var paidConfirmation = RequirePaidApiRunConfirmation(context, goal, task, agents, operation);
                if (paidConfirmation is not null)
                {
                    return paidConfirmation;
                }

                var largePaidSubscriptionStartConfirmation = RequireLargePaidSubscriptionStartConfirmation(context, current, goal, task, operation);
                if (largePaidSubscriptionStartConfirmation is not null)
                {
                    return largePaidSubscriptionStartConfirmation;
                }

                var actionResult = await GoalManagementCommandService.ApplyTaskActionAsync(
                    current,
                    agents,
                    services.Providers,
                    services.Workspace,
                    goal,
                    task,
                    operation,
                    body);
                return Json(actionResult ?? DashboardResponseMapper.ToTaskDetailDto(goal, task));
            },
            context.RequestAborted);
    }

    private static async Task<IResult> DelegateGoalAsync(string goalId, DashboardEndpointServices services)
    {
        var agents = services.LoadAgentCatalog().Agents;
        return await MutateAsync(
            services,
            current =>
            {
                var goal = ResolveGoal(current, goalId);
                var plan = current.ActivateGoal(goal.Id, agents);
                return Task.FromResult(Json(DashboardResponseMapper.ToDelegationPlanDto(plan)));
            });
    }

    private static async Task<IResult> AdvanceGoalAsync(string goalId, DashboardEndpointServices services)
    {
        var agents = services.LoadAgentCatalog().Agents;
        return await MutateIfChangedAsync(
            services,
            async current =>
            {
                var goal = ResolveGoal(current, goalId);
                var result = await GoalManagementCommandService.AdvanceGoalAsync(current, agents, services.Providers, services.Workspace, goal);
                return (result.Executed, Json(result, result.Executed ? StatusCodes.Status200OK : StatusCodes.Status409Conflict));
            });
    }

    private static async Task<IResult> AdvanceGoalWithSubscriptionsAsync(HttpContext context, string goalId, DashboardEndpointServices services)
    {
        var confirmation = RequireSubscriptionAdvanceConfirmation(context);
        if (confirmation is not null)
        {
            return confirmation;
        }

        var agents = services.LoadAgentCatalog().Agents;
        var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        return await MutateIfChangedAsync(
            services,
            current =>
            {
                var goal = ResolveGoal(current, goalId);
                var result = GoalManagementCommandService.AdvanceGoalWithSubscriptions(
                    current,
                    agents,
                    profiles,
                    services.Workspace,
                    goal,
                    HasLargePaidSubscriptionStartConfirmation(context));
                return Task.FromResult((result.Executed, Json(result, result.Executed ? StatusCodes.Status200OK : StatusCodes.Status409Conflict)));
            });
    }

    private static async Task<IResult> AdvanceGoalUntilBlockedAsync(string goalId, DashboardEndpointServices services)
    {
        var agents = services.LoadAgentCatalog().Agents;
        return await MutateIfChangedAsync(
            services,
            async current =>
            {
                var goal = ResolveGoal(current, goalId);
                var result = await GoalManagementCommandService.AdvanceGoalUntilBlockedAsync(current, agents, services.Providers, services.Workspace, goal);
                return (result.Executed, Json(result, result.Executed ? StatusCodes.Status200OK : StatusCodes.Status409Conflict));
            });
    }

    private static async Task<IResult> AdvanceGoalWithSubscriptionsUntilBlockedAsync(HttpContext context, string goalId, DashboardEndpointServices services)
    {
        var confirmation = RequireSubscriptionAdvanceConfirmation(context);
        if (confirmation is not null)
        {
            return confirmation;
        }

        var agents = services.LoadAgentCatalog().Agents;
        var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        var result = await services.State.MutateValueIfChangedAsync(
            current =>
            {
                var goal = ResolveGoal(current, goalId);
                var advance = GoalManagementCommandService.AdvanceGoalWithSubscriptionsUntilBlocked(
                    current,
                    agents,
                    profiles,
                    services.Workspace,
                    goal,
                    HasLargePaidSubscriptionStartConfirmation(context));
                return Task.FromResult((advance.Executed, advance));
            });
        if (DashboardContinuationService.ShouldContinueWatching(result))
        {
            result = result with { Continuation = services.Continuations.StartSubscriptionWatch(services, result.GoalId) };
        }

        return Json(result, result.Executed ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> HandleGoalBatchOperationAsync(
        HttpContext context,
        string goalId,
        string operation,
        DashboardEndpointServices services)
    {
        if (!GoalManagementCommandService.IsGoalBatchOperation(operation))
        {
            return Text("not found", "text/plain; charset=utf-8", StatusCodes.Status404NotFound);
        }

        var requiresBatchStartConfirmation = RequiresBatchStartConfirmation(operation);
        var batchStartConfirmed = context.Request.Query.TryGetValue("confirmBatchStart", out var confirmBatchStart) &&
            confirmBatchStart.Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        if (requiresBatchStartConfirmation && !batchStartConfirmed)
        {
            return Text(
                "dashboard invalid request: batch start operations require confirmBatchStart=true because they can start multiple worker processes.",
                "text/plain; charset=utf-8",
                StatusCodes.Status400BadRequest);
        }

        var body = await ReadRequestBodyAsync(context.Request);
        var agents = services.LoadAgentCatalog().Agents;
        return await MutateAsync(
            services,
            current =>
            {
                var goal = ResolveGoal(current, goalId);
                var largePaidSubscriptionStartConfirmation = RequireLargePaidSubscriptionStartConfirmation(
                    context,
                    current,
                    goal,
                    agents,
                    operation,
                    services.Workspace);
                if (largePaidSubscriptionStartConfirmation is not null)
                {
                    return Task.FromResult(largePaidSubscriptionStartConfirmation);
                }

                var result = GoalManagementCommandService.ApplyGoalBatchAction(
                    current,
                    agents,
                    goal,
                    operation,
                    body,
                    services.Workspace);
                return Task.FromResult(Json(result));
            },
            context.RequestAborted);
    }

    private static bool RequiresBatchStartConfirmation(string operation)
    {
        return operation.Equals("start-subscription-ready", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("start-dispatches", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult? RequireSubscriptionAdvanceConfirmation(HttpContext context)
    {
        var confirmed = context.Request.Query.TryGetValue("confirmSubscriptionAdvance", out var value) &&
            value.Any(item => string.Equals(item, "true", StringComparison.OrdinalIgnoreCase));
        return confirmed
            ? null
            : Text(
                "dashboard invalid request: subscription advance requires confirmSubscriptionAdvance=true because it can prepare or start worker processes.",
                "text/plain; charset=utf-8",
                StatusCodes.Status400BadRequest);
    }

    private static IResult? RequireTaskRunConfirmation(HttpContext context, string operation)
    {
        if (!operation.Equals("run", StringComparison.OrdinalIgnoreCase) &&
            !operation.Equals("api-run", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var confirmed = context.Request.Query.TryGetValue("confirmTaskRun", out var value) &&
            value.Any(item => string.Equals(item, "true", StringComparison.OrdinalIgnoreCase));
        return confirmed
            ? null
            : Text(
                "dashboard invalid request: task run operations require confirmTaskRun=true because they can invoke model-backed work.",
                "text/plain; charset=utf-8",
                StatusCodes.Status400BadRequest);
    }

    private static IResult? RequireDispatchStartConfirmation(HttpContext context, string operation)
    {
        if (!operation.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var confirmed = context.Request.Query.TryGetValue("confirmDispatchStart", out var value) &&
            value.Any(item => string.Equals(item, "true", StringComparison.OrdinalIgnoreCase));
        return confirmed
            ? null
            : Text(
                "dashboard invalid request: prepared work start requires confirmDispatchStart=true because it can start a worker process.",
                "text/plain; charset=utf-8",
                StatusCodes.Status400BadRequest);
    }

    private static IResult? RequireLargePaidSubscriptionStartConfirmation(
        HttpContext context,
        AgentOrchestratorKernel current,
        Goal goal,
        TaskSpec task,
        string operation)
    {
        if (!operation.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return RequireLargePaidSubscriptionStartConfirmation(
            context,
            SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(current, goal, task));
    }

    private static IResult? RequireLargePaidSubscriptionStartConfirmation(
        HttpContext context,
        AgentOrchestratorKernel current,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        string operation,
        OrchestratorWorkspace workspace)
    {
        var risk = operation.ToLowerInvariant() switch
        {
            "start-subscription-ready" => SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
                goal,
                agents,
                WorkerProfileStore.Load(workspace.WorkerProfilePath),
                task => current.BuildTaskBrief(goal.Id, task.Id).Content.Length),
            "start-dispatches" => SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(current, goal),
            _ => null
        };

        return RequireLargePaidSubscriptionStartConfirmation(context, risk);
    }

    private static IResult? RequireLargePaidSubscriptionStartConfirmation(HttpContext context, PaidSubscriptionPromptRisk? risk)
    {
        if (risk is null)
        {
            return null;
        }

        var confirmed = HasLargePaidSubscriptionStartConfirmation(context);
        return confirmed
            ? null
            : Text(
                "dashboard invalid request: " + SubscriptionPromptCostGuard.BuildDashboardMessage(risk),
                "text/plain; charset=utf-8",
                StatusCodes.Status400BadRequest);
    }

    private static bool HasLargePaidSubscriptionStartConfirmation(HttpContext context)
    {
        return context.Request.Query.TryGetValue(SubscriptionPromptCostGuard.DashboardConfirmationQueryName, out var value) &&
            value.Any(item => string.Equals(item, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static IResult? RequirePaidApiRunConfirmation(
        HttpContext context,
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition> agents,
        string operation)
    {
        var agent = agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId);
        if (agent is null || !TaskOperationCanInvokeApi(operation, task, agent))
        {
            return null;
        }

        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, agent.Role);
        var resolvedModel = TaskComplexityEstimator.ResolveModel(agent, complexity, task.Description, goal.Objective);
        if (!ProviderSmokeRunner.IsPaidProviderName(resolvedModel.ProviderName))
        {
            return null;
        }

        var confirmed = context.Request.Query.TryGetValue("confirmPaidApiRun", out var value) &&
            value.Any(item => string.Equals(item, "true", StringComparison.OrdinalIgnoreCase));
        return confirmed
            ? null
            : Text(
                $"dashboard invalid request: paid API execution for provider '{resolvedModel.ProviderName}' requires confirmPaidApiRun=true because it can make a live billable request.",
                "text/plain; charset=utf-8",
                StatusCodes.Status400BadRequest);
    }

    private static bool TaskOperationCanInvokeApi(string operation, TaskSpec task, AgentDefinition agent)
    {
        if (operation.Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            return agent.ExecutionPolicy == AgentExecutionPolicy.ApiOnly;
        }

        if (!operation.Equals("api-run", StringComparison.OrdinalIgnoreCase) ||
            !AgentExecutionPolicies.AllowsApi(agent.ExecutionPolicy))
        {
            return false;
        }

        return agent.ExecutionPolicy == AgentExecutionPolicy.ApiOnly ||
            IsCleanExplicitApiRun(task);
    }

    private static bool IsCleanExplicitApiRun(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Assigned &&
            task.LastDispatch is null &&
            task.LastProcess is null &&
            task.LastExecution is null &&
            task.LastVerification is null &&
            task.SubscriptionRetryAfter is null;
    }
}
