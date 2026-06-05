using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private static async Task<IResult> GetPendingInputAsync(HttpContext context, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var pendingGoalFilter = DashboardRequestParser.GetQueryValue(context.Request, "goal");
        Goal? goal = string.IsNullOrWhiteSpace(pendingGoalFilter)
            ? null
            : ResolveGoal(current, pendingGoalFilter);
        var pending = current.HumanInputRequests
            .Where(request => !request.IsCompleted && (goal is null || request.GoalId == goal.Id))
            .OrderBy(request => request.RequestedAt)
            .Select(request => DashboardResponseMapper.ToHumanInputDto(current, request))
            .ToList();
        return Json(pending);
    }

    private static async Task<IResult> AnswerHumanInputAsync(
        HttpContext context,
        string inputId,
        DashboardEndpointServices services)
    {
        var answer = DashboardRequestParser.ParseAnswerSubmission(await ReadRequestBodyAsync(context.Request));
        return await MutateAsync(
            services,
            current =>
            {
                var request = OrchestratorEntityResolver.ResolveHumanInputRequest(current, inputId);
                current.SubmitHumanInput(request.Id, answer);
                return Task.FromResult(Json(DashboardResponseMapper.ToHumanInputDto(current, current.GetHumanInputRequest(request.Id))));
            },
            context.RequestAborted);
    }
}
