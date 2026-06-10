using Microsoft.AspNetCore.Http;

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
        return Json(DashboardResponseMapper.ToGoalStageReadinessReportDto(goal, current.BuildStageReadinessReport(goal.Id)));
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
        var maxFiles = ParseSourceSurveyMaxFiles(context.Request);
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

    private static int ParseSourceSurveyMaxFiles(HttpRequest request)
    {
        var value = DashboardRequestParser.GetQueryValue(request, "max");
        if (string.IsNullOrWhiteSpace(value))
        {
            return SourceSurvey.DefaultMaxFiles;
        }

        if (!int.TryParse(value, out var maxFiles) || maxFiles < 1)
        {
            throw new ArgumentException("Source survey max must be a positive integer.");
        }

        return maxFiles;
    }
}
