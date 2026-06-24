using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.App.Orchestration;
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
                        ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(current, agents, submission.Objective, services.Workspace, services.Providers)
                        : GoalLifecycleCommands.CreateAndActivateGoal(current, agents, submission.Objective, services.Workspace, services.Providers);
                    AdvanceLoopResultDto? autoHandoff = null;

                    if (submission.AutoHandoff)
                    {
                        try
                        {
                            await saveCheckpoint();
                            autoHandoff = GoalManagementCommandService.AdvanceGoalWithSubscriptionsUntilBlocked(
                                current,
                                agents,
                                profiles,
                                services.Workspace,
                                goal,
                                providers: services.Providers);
                            if (DashboardContinuationService.ShouldContinueWatching(autoHandoff))
                            {
                                services.Continuations.StartSubscriptionWatch(services, autoHandoff.GoalId);
                            }

                            await saveCheckpoint();
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Console.WriteLine($"Automatic subscription handoff did not start for goal {goal.Id.Value}: {ex.Message}");
                        }
                    }

                    var responseGoal = current.Goals.Single(currentGoal => currentGoal.Id == goal.Id);
                    return Json(BuildCreatedGoalResponse(current, responseGoal, autoHandoff), StatusCodes.Status201Created);
                },
                context.RequestAborted);
        }

        var current = await LoadAsync(services, context.RequestAborted);
        return Json(current.Goals.Select(DashboardResponseMapper.ToGoalSummary).ToList());
    }

    private static GoalDetailDto BuildCreatedGoalResponse(
        AgentOrchestratorKernel current,
        Goal goal,
        AdvanceLoopResultDto? autoHandoff = null)
    {
        try
        {
            return DashboardResponseMapper.ToGoalDetailDto(current, goal) with { AutoHandoff = autoHandoff };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"Created goal {goal.Id.Value} but could not build verification summary: {ex.Message}");
            return new GoalDetailDto(
                DashboardResponseMapper.ToGoalSummary(goal),
                goal.Tasks.Select(task => DashboardResponseMapper.ToTaskSummaryDto(goal, task)).ToList(),
                VerificationSatisfied: false,
                AutoHandoff: autoHandoff);
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
        var agents = services.LoadAgentCatalog().Agents;
        return Text(GoalTranscriptRenderer.Render(current, goal, agents), "text/markdown; charset=utf-8");
    }

    private static async Task<IResult> GetGoalWorkSummaryAsync(string goalId, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services);
        var goal = ResolveGoal(current, goalId);
        var agents = services.LoadAgentCatalog().Agents;
        return Json(DashboardResponseMapper.ToGoalWorkSummaryDto(current, goal, agents, BuildHostInfo(services)));
    }

    private static async Task<IResult> GetFailureTriageAsync(
        HttpContext context,
        string goalId,
        DashboardEndpointServices services)
    {
        var policy = ResolveDashboardAutonomyPolicy(context);
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(current, goalId);
        var agents = services.LoadAgentCatalog().Agents;
        return Json(ToFailureTriageReportDto(FailureTriagePlanner.Build(
            current,
            goal,
            agents,
            services.Workspace.ExecutionDirectory,
            policy)));
    }

    private static async Task<IResult> GetActionRecommendationsAsync(
        HttpContext context,
        string goalId,
        DashboardEndpointServices services)
    {
        var policy = ResolveDashboardAutonomyPolicy(context);
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(current, goalId);
        var agents = services.LoadAgentCatalog().Agents;
        var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        return Json(DashboardResponseMapper.ToDashboardActionRecommendationReportDto(
            DashboardActionRecommendationPlanner.Build(
                current,
                goal,
                agents,
                profiles,
                services.Workspace.ExecutionDirectory,
                policy)));
    }

    private static async Task<IResult> HandleGoalSupervisorAsync(
        HttpContext context,
        string goalId,
        DashboardEndpointServices services)
    {
        var policy = ResolveDashboardAutonomyPolicy(context);
        var applySafe = HasQueryConfirmation(context, "applySafe");
        var agents = services.LoadAgentCatalog().Agents;
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && applySafe)
        {
            return await MutateIfChangedAsync(
                services,
                current =>
                {
                    var goal = ResolveGoal(current, goalId);
                    var result = GoalSupervisor.ApplySafe(current, goal, agents, services.Workspace, policy);
                    return Task.FromResult((
                        result.AppliedActions.Count > 0,
                        Json(ToGoalSupervisorPlanDto(result.Plan, result.AppliedActions))));
                },
                context.RequestAborted);
        }

        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(current, goalId);
        var plan = GoalSupervisor.Build(
            current,
            goal,
            agents,
            services.Workspace.ExecutionDirectory,
            policy,
            applySafe: false);
        return Json(ToGoalSupervisorPlanDto(plan, []));
    }

    private static async Task<IResult> GetSubscriptionPlanAsync(string goalId, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services);
        var goal = ResolveGoal(current, goalId);
        var agents = services.LoadAgentCatalog().Agents;
        var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        var plan = SubscriptionPlanBuilder.Build(
            goal,
            agents,
            profiles,
            task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(current, goal, task, agents));
        return Json(DashboardResponseMapper.ToSubscriptionPlanDto(plan));
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
        var policy = ResolveDashboardAutonomyPolicy(context);
        var confirmation = RequireTaskRunConfirmation(context, operation);
        confirmation ??= RequireDispatchStartConfirmation(context, operation);
        if (confirmation is not null)
        {
            return confirmation;
        }

        if (operation.Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            return await RefreshTaskOutsideTransactionAsync(context, goalId, taskId, services, agents, policy);
        }

        return await MutateAsync(
            services,
            async current =>
            {
                var goal = ResolveGoal(current, goalId);
                var task = OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, taskId);
                var policyConfirmation = RequirePolicyForTaskOperation(current, goal, operation, policy);
                if (policyConfirmation is not null)
                {
                    return policyConfirmation;
                }

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
                GoalRefinementGate.EnsureRefined(current, services.Workspace, services.Providers, goal);
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
        var policy = ResolveDashboardAutonomyPolicy(context);
        var policyConfirmation = RequirePolicy(
            null,
            null,
            policy,
            AutonomyAction.DispatchStart,
            "advance-subscription");
        if (policyConfirmation is not null)
        {
            return policyConfirmation;
        }

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
                AutonomyPolicyEvidence.Record(current, goal, policy, AutonomyAction.DispatchStart, "advance-subscription", allowed: true);
                var result = GoalManagementCommandService.AdvanceGoalWithSubscriptions(
                    current,
                    agents,
                    profiles,
                    services.Workspace,
                    goal,
                    HasLargePaidSubscriptionStartConfirmation(context),
                    services.Providers);
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
        var policy = ResolveDashboardAutonomyPolicy(context);
        var policyConfirmation = RequirePolicy(
            null,
            null,
            policy,
            AutonomyAction.DispatchStart,
            "advance-subscription-until-blocked");
        if (policyConfirmation is not null)
        {
            return policyConfirmation;
        }

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
                AutonomyPolicyEvidence.Record(current, goal, policy, AutonomyAction.DispatchStart, "advance-subscription-until-blocked", allowed: true);
                var advance = GoalManagementCommandService.AdvanceGoalWithSubscriptionsUntilBlocked(
                    current,
                    agents,
                    profiles,
                    services.Workspace,
                    goal,
                    HasLargePaidSubscriptionStartConfirmation(context),
                    services.Providers);
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
        var policy = ResolveDashboardAutonomyPolicy(context);
        if (operation.Equals("refresh-dispatches", StringComparison.OrdinalIgnoreCase))
        {
            return await RefreshDispatchesOutsideTransactionAsync(context, goalId, services, agents, policy);
        }

        return await MutateAsync(
            services,
            current =>
            {
                var goal = ResolveGoal(current, goalId);
                var policyConfirmation = RequirePolicyForGoalBatchOperation(current, goal, operation, policy);
                if (policyConfirmation is not null)
                {
                    return Task.FromResult(policyConfirmation);
                }

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

                var readinessConfirmation = RequireGoalReadinessStartConfirmation(context, goal, agents, operation, services.Workspace);
                if (readinessConfirmation is not null)
                {
                    return Task.FromResult(readinessConfirmation);
                }

                var result = GoalManagementCommandService.ApplyGoalBatchAction(
                    current,
                    agents,
                    goal,
                    operation,
                    body,
                    services.Workspace,
                    services.Providers);
                return Task.FromResult(Json(result));
            },
            context.RequestAborted);
    }

    private static async Task<IResult> RefreshTaskOutsideTransactionAsync(
        HttpContext context,
        string goalId,
        string taskId,
        DashboardEndpointServices services,
        IReadOnlyList<AgentDefinition> agents,
        AutonomyPolicy policy)
    {
        var snapshot = await LoadAsync(services, context.RequestAborted);
        var snapshotGoal = ResolveGoal(snapshot, goalId);
        var snapshotTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(snapshotGoal, taskId);
        var policyConfirmation = RequirePolicyForTaskOperation(snapshot, snapshotGoal, "refresh", policy);
        if (policyConfirmation is not null)
        {
            return policyConfirmation;
        }

        var runner = new BackgroundDispatchRunner();
        var outcome = runner.ReconcileLatestProcess(snapshot, snapshotGoal.Id, snapshotTask.Id);
        return await MutateAsync(
            services,
            current =>
            {
                var goal = ResolveGoal(current, goalId);
                var task = OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, taskId);
                BackgroundDispatchRunner.ApplyRefreshOutcome(current, goal.Id, task.Id, outcome);
                AutonomyPolicyEvidence.Record(current, goal, policy, AutonomyAction.Refresh, "refresh", allowed: true);
                var updatedGoal = ResolveGoal(current, goalId);
                var updatedTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(updatedGoal, taskId);
                return Task.FromResult(Json(DashboardResponseMapper.ToTaskDetailDto(updatedGoal, updatedTask)));
            },
            context.RequestAborted);
    }

    private static async Task<IResult> RefreshDispatchesOutsideTransactionAsync(
        HttpContext context,
        string goalId,
        DashboardEndpointServices services,
        IReadOnlyList<AgentDefinition> agents,
        AutonomyPolicy policy)
    {
        var snapshot = await LoadAsync(services, context.RequestAborted);
        var snapshotGoal = ResolveGoal(snapshot, goalId);
        var policyConfirmation = RequirePolicyForGoalBatchOperation(snapshot, snapshotGoal, "refresh-dispatches", policy);
        if (policyConfirmation is not null)
        {
            return policyConfirmation;
        }

        var plan = snapshot.BuildProcessBatchPlan(snapshotGoal.Id, ProcessBatchActionKind.RefreshDispatches);
        var runner = new BackgroundDispatchRunner();
        var outcomes = new List<(TaskId TaskId, DispatchRefreshOutcome Outcome)>();
        foreach (var item in plan.Items.Where(item => item.Status == ProcessBatchItemStatus.Ready))
        {
            outcomes.Add((item.TaskId, runner.ReconcileLatestProcess(snapshot, snapshotGoal.Id, item.TaskId)));
        }

        return await MutateAsync(
            services,
            current =>
            {
                var goal = ResolveGoal(current, goalId);
                var refreshed = new List<TaskSpec>();
                foreach (var (refreshTaskId, outcome) in outcomes)
                {
                    BackgroundDispatchRunner.ApplyRefreshOutcome(current, goal.Id, refreshTaskId, outcome);
                    refreshed.Add(current.GetTask(goal.Id, refreshTaskId));
                }

                AutonomyPolicyEvidence.Record(current, goal, policy, AutonomyAction.Refresh, "refresh-dispatches", allowed: true);
                var updatedGoal = ResolveGoal(current, goalId);
                var result = DashboardResponseMapper.ToProcessBatchActionResultDto(
                    updatedGoal,
                    "refresh-dispatches",
                    new ProcessBatchExecutionResult(plan, refreshed));
                return Task.FromResult(Json(result));
            },
            context.RequestAborted);
    }

    private static bool RequiresBatchStartConfirmation(string operation)
    {
        return operation.Equals("start-subscription-ready", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("start-dispatches", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult? RequireGoalReadinessStartConfirmation(
        HttpContext context,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        string operation,
        OrchestratorWorkspace workspace)
    {
        if (!RequiresBatchStartConfirmation(operation))
        {
            return null;
        }

        var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
        var report = GoalReadinessPreflight.Build(goal, agents, workspace.ExecutionDirectory, profiles);
        if (report.AllowsStart(HasQueryConfirmation(context, "confirmReadinessRisk")))
        {
            return null;
        }

        var blockers = string.Join("; ", report.Findings
            .Where(finding => finding.Severity == GoalReadinessSeverity.Blocker)
            .Select(finding => $"{finding.Kind}: {finding.Message}"));
        var confirm = report.RequiresOperatorConfirmation
            ? " Add confirmReadinessRisk=true after reviewing readiness."
            : string.Empty;
        return Text(
            $"dashboard invalid request: goal readiness preflight blocked {operation}. {blockers}.{confirm}",
            "text/plain; charset=utf-8",
            StatusCodes.Status409Conflict);
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
                task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(current, goal, task, agents)),
            "start-dispatches" => SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(current, goal),
            _ => null
        };

        return RequireLargePaidSubscriptionStartConfirmation(context, risk);
    }

    private static IResult? RequireLargePaidSubscriptionStartConfirmation(HttpContext context, PaidSubscriptionPromptRisk? risk)
    {
        if (risk is null || !risk.IsAnomalous)
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

    private static AutonomyPolicy ResolveDashboardAutonomyPolicy(HttpContext context)
    {
        return AutonomyPolicy.Parse(
            context.Request.Query.TryGetValue("autonomyPolicy", out var value)
                ? value.FirstOrDefault()
                : null);
    }

    private static IResult? RequirePolicyForTaskOperation(
        AgentOrchestratorKernel current,
        Goal goal,
        string operation,
        AutonomyPolicy policy)
    {
        var action = operation.ToLowerInvariant() switch
        {
            "run" or "api-run" => AutonomyAction.ModelRun,
            "start" => AutonomyAction.DispatchStart,
            "refresh" => AutonomyAction.Refresh,
            "verify" => AutonomyAction.BuildTest,
            _ => (AutonomyAction?)null
        };

        return action is null
            ? null
            : RequirePolicy(current, goal, policy, action.Value, operation);
    }

    private static IResult? RequirePolicyForGoalBatchOperation(
        AgentOrchestratorKernel current,
        Goal goal,
        string operation,
        AutonomyPolicy policy)
    {
        var action = operation.ToLowerInvariant() switch
        {
            "start-subscription-ready" or "start-dispatches" => AutonomyAction.DispatchStart,
            "refresh-dispatches" => AutonomyAction.Refresh,
            _ => (AutonomyAction?)null
        };

        return action is null
            ? null
            : RequirePolicy(current, goal, policy, action.Value, operation);
    }

    private static IResult? RequirePolicy(
        AgentOrchestratorKernel? current,
        Goal? goal,
        AutonomyPolicy policy,
        AutonomyAction action,
        string operation)
    {
        var allowed = policy.Allows(action);
        if (current is not null && goal is not null)
        {
            AutonomyPolicyEvidence.Record(current, goal, policy, action, operation, allowed);
        }

        return allowed
            ? null
            : Text(
                $"dashboard invalid request: autonomy policy '{policy.Name}' blocks {operation}.",
                "text/plain; charset=utf-8",
                StatusCodes.Status409Conflict);
    }

    private static GoalSupervisorPlanDto ToGoalSupervisorPlanDto(
        GoalSupervisorPlan plan,
        IReadOnlyList<string> appliedActions)
    {
        return new GoalSupervisorPlanDto(
            plan.GoalId.Value,
            plan.GoalPrefix,
            plan.PolicyName,
            plan.ApplySafe,
            appliedActions,
            plan.Proposals.Select(proposal => new GoalSupervisorProposalDto(
                proposal.Kind,
                proposal.TaskNumber,
                proposal.TaskId?.Value,
                proposal.PolicyAction,
                proposal.PolicyAllows,
                proposal.CanApply,
                proposal.RequiresOperatorGate,
                proposal.Reason,
                proposal.SuggestedCommand)).ToList());
    }

    private static FailureTriageReportDto ToFailureTriageReportDto(FailureTriageReport report)
    {
        return new FailureTriageReportDto(
            report.GoalId.Value,
            report.GoalPrefix,
            report.PolicyName,
            report.Items.Select(item => new FailureTriageItemDto(
                item.TaskNumber,
                item.TaskId?.Value,
                item.Cause,
                item.Action,
                item.PolicyAction,
                item.PolicyAllows,
                item.CanAutoApply,
                item.RequiresOperatorGate,
                item.Explanation,
                item.SuggestedCommand)).ToList());
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

        var preview = AgentTaskRunner.PreviewRun(goal, task, [agent]);
        if (!ProviderSmokeRunner.IsPaidProviderName(preview.ProviderName))
        {
            return null;
        }

        if (!HasQueryConfirmation(context, "confirmPaidApiRun"))
        {
            return Text(
                $"dashboard invalid request: paid API execution for provider '{preview.ProviderName}' requires confirmPaidApiRun=true because it can make a live billable request.",
                "text/plain; charset=utf-8",
                StatusCodes.Status400BadRequest);
        }

        var largePromptRisk = ApiPromptCostGuard.Evaluate(preview, goal);
        return largePromptRisk is null || HasQueryConfirmation(context, ApiPromptCostGuard.DashboardConfirmationQueryName)
            ? null
            : Text(
                "dashboard invalid request: " + ApiPromptCostGuard.BuildDashboardMessage(largePromptRisk),
                "text/plain; charset=utf-8",
                StatusCodes.Status400BadRequest);
    }

    private static bool HasQueryConfirmation(HttpContext context, string name)
    {
        return context.Request.Query.TryGetValue(name, out var value) &&
            value.Any(item => string.Equals(item, "true", StringComparison.OrdinalIgnoreCase));
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
