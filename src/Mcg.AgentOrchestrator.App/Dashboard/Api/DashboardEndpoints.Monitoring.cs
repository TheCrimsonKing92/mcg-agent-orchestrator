using System.Globalization;
using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private static async Task<IResult> GetGoalEventsAsync(HttpContext context, string goalId, DashboardEndpointServices services)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var goal = ResolveGoal(current, goalId);
        return Json(BuildMonitoringBatch(services, current, goal, ParseMonitoringSinceEventId(context.Request)));
    }

    private static async Task StreamGoalEventsAsync(HttpContext context, string goalId, DashboardEndpointServices services)
    {
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.ContentType = "text/event-stream; charset=utf-8";

        var runEvents = new SqliteRunEventStore(services.Workspace.RunEventStorePath);
        await GoalMonitoringStream.StreamAsync(
            context.Response.Body,
            goalId,
            ct => LoadAsync(services, ct),
            (kernel, goal, since) => BuildMonitoringBatch(services, kernel, goal, since),
            runEvents,
            ParseMonitoringSinceEventId(context.Request),
            once: false,
            TimeSpan.FromSeconds(1),
            context.RequestAborted);
    }

    private static GoalMonitoringBatchDto BuildMonitoringBatch(
        DashboardEndpointServices services,
        AgentOrchestratorKernel kernel,
        Goal goal,
        long sinceEventId)
    {
        return GoalMonitoringStream.BuildBatch(
            kernel,
            goal,
            sinceEventId,
            services.LoadAgentCatalog().Agents,
            WorkerProfileStore.Load(services.WorkerProfilePath),
            services.Workspace);
    }

    private static long ParseMonitoringSinceEventId(HttpRequest request)
    {
        var raw = DashboardRequestParser.GetQueryValue(request, "since")
            ?? request.Headers["Last-Event-ID"].FirstOrDefault();
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;
    }

}
