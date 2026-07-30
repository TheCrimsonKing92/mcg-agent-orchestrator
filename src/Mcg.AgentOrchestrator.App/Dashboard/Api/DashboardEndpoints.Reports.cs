using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private static async Task<IResult> GetMonitorAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        var lifecycle = GoalLifecycle.ResolveState(goal, GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(services.Workspace, goal));
        return Json(DashboardResponseMapper.ToMonitorDto(current.BuildMonitor(goal.Id), lifecycle));
    }

    private static async Task<IResult> GetAcceptanceAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        var lifecycle = GoalLifecycle.ResolveState(goal, GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(services.Workspace, goal));
        var summary = GoalAcceptanceStatusProjector.Build(current, goal, services.Workspace.ExecutionDirectory);
        return Json(DashboardResponseMapper.ToGoalAcceptanceSummaryDto(goal, summary, lifecycle));
    }

    private static async Task<IResult> GetEvidenceAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        return Json(DashboardResponseMapper.ToGoalEvidenceSummaryDto(goal, current.BuildGoalEvidenceSummary(goal.Id)));
    }

    private static async Task<IResult> GetStagesAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        var agents = services.LoadAgentCatalog().Agents;
        return Json(DashboardResponseMapper.ToGoalStageReadinessReportDto(goal, current.BuildStageReadinessReport(goal.Id), agents));
    }

    private static async Task<IResult> GetGatesAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        return Json(DashboardResponseMapper.ToVerificationGateDto(goal, current.BuildVerificationGate(goal.Id)));
    }

    private static async Task<IResult> GetVerificationWorklistAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        return Json(DashboardResponseMapper.ToVerificationWorklistDto(goal, current.BuildVerificationWorklist(goal.Id)));
    }

    private static async Task<IResult> GetHumanInputWorklistAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        return Json(DashboardResponseMapper.ToHumanInputWorklistDto(goal, current.BuildHumanInputWorklist(goal.Id)));
    }

    private static async Task<IResult> GetNextActionsAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        var agents = services.LoadAgentCatalog().Agents;
        var workerProfiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        var conductorDisposition = ConductorOperatorDispositionSnapshots.TryReadLatestForGoal(services.Workspace.RunEventStorePath, goal);
        return Json(DashboardResponseMapper.ToNextActionsDto(goal, current.BuildNextActions(goal.Id), workerProfiles, agents, conductorDisposition));
    }

    private static async Task<IResult> GetOperatorInboxAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var agents = services.LoadAgentCatalog().Agents;
        var workerProfiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        var goalPrefix = DashboardRequestParser.GetQueryValue(context.Request, "goal");
        var includeAcknowledged = IsTrue(DashboardRequestParser.GetQueryValue(context.Request, "includeAcknowledged"));
        var report = OperatorInbox.Build(current, agents, workerProfiles, services.Workspace, goalPrefix, includeAcknowledged);
        return Json(DashboardResponseMapper.ToOperatorInboxReportDto(report));
    }

    private static async Task<IResult> AcknowledgeOperatorInboxItemAsync(HttpContext context, DashboardEndpointServices services)
    {
        var itemId = DashboardRequestParser.GetQueryValue(context.Request, "itemId");
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new ArgumentException("operator inbox acknowledgement requires itemId.");
        }

        var current = await LoadAsync(services, context.RequestAborted);
        var agents = services.LoadAgentCatalog().Agents;
        var workerProfiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        var goalPrefix = DashboardRequestParser.GetQueryValue(context.Request, "goal");
        var note = DashboardRequestParser.GetQueryValue(context.Request, "note");
        var report = OperatorInbox.Acknowledge(current, agents, workerProfiles, services.Workspace, itemId, note, goalPrefix);
        return Json(DashboardResponseMapper.ToOperatorInboxReportDto(report));
    }

    private static IResult GetBacklogGoalPlan(HttpContext context, DashboardEndpointServices services)
    {
        var heading = DashboardRequestParser.GetQueryValue(context.Request, "heading");
        var maxValue = DashboardRequestParser.GetQueryValue(context.Request, "max");
        var maxItems = string.IsNullOrWhiteSpace(maxValue)
            ? 10
            : int.TryParse(maxValue, out var parsedMax) && parsedMax > 0
                ? parsedMax
                : throw new ArgumentException("Backlog goal-plan max must be a positive integer.");
        var intake = BacklogIntakePlanner.Build(
            services.Workspace.BacklogStorePath,
            string.IsNullOrWhiteSpace(heading) ? null : heading,
            maxItems);
        if (intake.Items.Count == 0)
        {
            throw new InvalidOperationException("No backlog items matched the requested filter.");
        }

        var plan = GoalDependencyPlanner.Build(intake);
        return Json(DashboardResponseMapper.ToBacklogGoalPlanDto(intake, plan));
    }

    private static async Task<IResult> GetScopeCollisionAdvisoryAsync(
        HttpContext context,
        DashboardEndpointServices services)
    {
        var heading = DashboardRequestParser.GetQueryValue(context.Request, "heading");
        var maxValue = DashboardRequestParser.GetQueryValue(context.Request, "max");
        var maxItems = string.IsNullOrWhiteSpace(maxValue)
            ? 10
            : int.TryParse(maxValue, out var parsedMax) && parsedMax > 0
                ? parsedMax
                : throw new ArgumentException("Scope collision advisory max must be a positive integer.");
        var intake = BacklogIntakePlanner.Build(
            services.Workspace.BacklogStorePath,
            string.IsNullOrWhiteSpace(heading) ? null : heading,
            maxItems);
        if (intake.Items.Count == 0)
        {
            throw new InvalidOperationException("No backlog items matched the requested filter.");
        }

        var current = await LoadAsync(services, context.RequestAborted);
        var reports = intake.Items
            .Select(item => (
                Item: item,
                Report: GoalScopeCollisionAdvisor.Build(
                    [item.SuggestedObjective],
                    current.Goals
                        .Where(goal => !string.Equals(
                            goal.SourceBacklogItemId,
                            item.Id,
                            StringComparison.Ordinal))
                        .ToArray())))
            .ToArray();
        return Json(DashboardResponseMapper.ToGoalScopeCollisionAdvisoryDto(reports));
    }

    private static async Task<IResult> GetCrossGoalStartPlanAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var agents = services.LoadAgentCatalog().Agents;
        var workerProfiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        var confirmed = IsTrue(DashboardRequestParser.GetQueryValue(context.Request, "confirmCostRisk"));
        var plan = CrossGoalSubscriptionStartPlanner.Build(current, agents, workerProfiles, confirmed);
        var drainPolicy = GoalDrainPolicyStore.LoadOrDefault(services.Workspace);
        return Json(DashboardResponseMapper.ToCrossGoalStartPlanDto(plan, drainPolicy));
    }

    private static IResult GetSourceSurvey(HttpContext context, DashboardEndpointServices services)
    {
        var maxFiles = ParseSourceSurveyMaxFiles(context.Request, services.HostArgs);
        var report = SourceSurvey.Build(services.Workspace.ExecutionDirectory, maxFiles);
        return Json(DashboardResponseMapper.ToSourceSurveyDto(report));
    }

    private static async Task<IResult> QueryTasksAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        var query = DashboardRequestParser.ParseTaskQueryFromRequest(context.Request);
        var result = current.QueryTasks(goal.Id, query);
        return Json(DashboardResponseMapper.ToTaskQueryDto(goal, result));
    }

    private static async Task<IResult> GetTaskAsync(HttpContext context, string taskId, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        var task = OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, taskId);
        return Json(DashboardResponseMapper.ToTaskDetailDto(goal, task));
    }

    private static async Task<IResult> GetTaskWorkSummaryAsync(HttpContext context, string taskId, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var (goal, task) = ResolveTaskForWorkSummary(context.Request, current, taskId);
        var agents = services.LoadAgentCatalog().Agents;
        var workerProfiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        return Json(DashboardResponseMapper.ToTaskWorkContextDto(
            current,
            goal,
            task,
            BuildHostInfo(services),
            workerProfiles,
            agents));
    }

    private static (Goal Goal, TaskSpec Task) ResolveTaskForWorkSummary(
        HttpRequest request,
        AgentOrchestratorKernel kernel,
        string taskId)
    {
        var goalQuery = DashboardRequestParser.GetQueryValue(request, "goal");
        if (!string.IsNullOrWhiteSpace(goalQuery))
        {
            var scopedGoal = ResolveGoal(kernel, goalQuery);
            return (scopedGoal, OrchestratorEntityResolver.GetTaskByDisplayNumber(scopedGoal, taskId));
        }

        if (int.TryParse(taskId, out _))
        {
            throw new ArgumentException("Task work summary by number requires a goal query, for example /api/tasks/1/work-summary?goal=<goalPrefix>.");
        }

        var matches = kernel.Goals
            .SelectMany(goal => goal.Tasks
                .Where(task => task.Id.Value.StartsWith(taskId, StringComparison.OrdinalIgnoreCase))
                .Select(task => (Goal: goal, Task: task)))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException($"Task '{taskId}' was not found."),
            _ => throw new InvalidOperationException($"Task id prefix '{taskId}' is ambiguous.")
        };
    }

    private static int ParseSourceSurveyMaxFiles(HttpRequest request, DashboardHostArgs hostArgs)
    {
        var value = DashboardRequestParser.GetQueryValue(request, "max");
        if (string.IsNullOrWhiteSpace(value))
        {
            return hostArgs.EnableOperatorControls
                ? SourceSurvey.DefaultMaxFiles
                : DashboardHost.DefaultHostedSourceSurveyMaxFiles;
        }

        if (!int.TryParse(value, out var maxFiles) || maxFiles < 1)
        {
            throw new ArgumentException("Source survey max must be a positive integer.");
        }

        return maxFiles;
    }

    private static bool IsTrue(string? value)
    {
        return value is not null &&
            (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
