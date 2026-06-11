using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private static async Task<IResult> GetMonitorAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        return Json(DashboardResponseMapper.ToMonitorDto(current.BuildMonitor(goal.Id)));
    }

    private static async Task<IResult> GetAcceptanceAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(context.Request, current);
        return Json(DashboardResponseMapper.ToGoalAcceptanceSummaryDto(goal, current.BuildGoalAcceptanceSummary(goal.Id)));
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
        return Json(DashboardResponseMapper.ToNextActionsDto(goal, current.BuildNextActions(goal.Id), agents));
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
        return Json(DashboardResponseMapper.ToTaskWorkContextDto(
            current,
            goal,
            task,
            BuildHostInfo(services),
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
}
