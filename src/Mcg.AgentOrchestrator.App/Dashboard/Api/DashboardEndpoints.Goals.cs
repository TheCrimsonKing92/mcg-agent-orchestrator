using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private static async Task<IResult> HandleGoalsAsync(HttpContext context, DashboardEndpointServices services)
    {
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var agents = AgentCatalogStore.Load(services.AgentCatalogPath).Agents;
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
        var agents = AgentCatalogStore.Load(services.AgentCatalogPath).Agents;
        var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        return Json(DashboardResponseMapper.BuildSubscriptionPlan(goal, agents, profiles));
    }

    private static async Task<IResult> AddTaskAsync(HttpContext context, string goalId, DashboardEndpointServices services)
    {
        var agents = AgentCatalogStore.Load(services.AgentCatalogPath).Agents;
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
        var agents = AgentCatalogStore.Load(services.AgentCatalogPath).Agents;
        return await MutateAsync(
            services,
            async current =>
            {
                var goal = ResolveGoal(current, goalId);
                var task = OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, taskId);
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
        var agents = AgentCatalogStore.Load(services.AgentCatalogPath).Agents;
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
        var agents = AgentCatalogStore.Load(services.AgentCatalogPath).Agents;
        return await MutateIfChangedAsync(
            services,
            async current =>
            {
                var goal = ResolveGoal(current, goalId);
                var result = await GoalManagementCommandService.AdvanceGoalAsync(current, agents, services.Providers, services.Workspace, goal);
                return (result.Executed, Json(result, result.Executed ? StatusCodes.Status200OK : StatusCodes.Status409Conflict));
            });
    }

    private static async Task<IResult> AdvanceGoalWithSubscriptionsAsync(string goalId, DashboardEndpointServices services)
    {
        var agents = AgentCatalogStore.Load(services.AgentCatalogPath).Agents;
        var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        return await MutateIfChangedAsync(
            services,
            current =>
            {
                var goal = ResolveGoal(current, goalId);
                var result = GoalManagementCommandService.AdvanceGoalWithSubscriptions(current, agents, profiles, services.Workspace, goal);
                return Task.FromResult((result.Executed, Json(result, result.Executed ? StatusCodes.Status200OK : StatusCodes.Status409Conflict)));
            });
    }

    private static async Task<IResult> AdvanceGoalUntilBlockedAsync(string goalId, DashboardEndpointServices services)
    {
        var agents = AgentCatalogStore.Load(services.AgentCatalogPath).Agents;
        return await MutateIfChangedAsync(
            services,
            async current =>
            {
                var goal = ResolveGoal(current, goalId);
                var result = await GoalManagementCommandService.AdvanceGoalUntilBlockedAsync(current, agents, services.Providers, services.Workspace, goal);
                return (result.Executed, Json(result, result.Executed ? StatusCodes.Status200OK : StatusCodes.Status409Conflict));
            });
    }

    private static async Task<IResult> AdvanceGoalWithSubscriptionsUntilBlockedAsync(string goalId, DashboardEndpointServices services)
    {
        var agents = AgentCatalogStore.Load(services.AgentCatalogPath).Agents;
        var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        var result = await services.State.MutateValueIfChangedAsync(
            current =>
            {
                var goal = ResolveGoal(current, goalId);
                var advance = GoalManagementCommandService.AdvanceGoalWithSubscriptionsUntilBlocked(current, agents, profiles, services.Workspace, goal);
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

        var body = await ReadRequestBodyAsync(context.Request);
        var agents = AgentCatalogStore.Load(services.AgentCatalogPath).Agents;
        return await MutateAsync(
            services,
            current =>
            {
                var goal = ResolveGoal(current, goalId);
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
}
