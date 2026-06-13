using System.Globalization;
using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
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
        AgentOrchestratorKernel current;
        Goal goal;
        try
        {
            current = await LoadAsync(services, context.RequestAborted);
            goal = ResolveGoal(current, goalId);
        }
        catch (KeyNotFoundException ex)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(ErrorText("dashboard not found", ex.Message), context.RequestAborted);
            return;
        }
        catch (ArgumentException ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(ErrorText("dashboard invalid request", ex.Message), context.RequestAborted);
            return;
        }

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.ContentType = "text/event-stream; charset=utf-8";

        var sinceEventId = ParseMonitoringSinceEventId(context.Request);
        var nextSnapshotAt = DateTimeOffset.MinValue;
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var batch = BuildMonitoringBatch(services, current, goal, sinceEventId);
            var now = DateTimeOffset.UtcNow;
            if (now >= nextSnapshotAt)
            {
                await DashboardMonitoringEvents.WriteServerSentEventAsync(
                    context.Response.Body,
                    DashboardMonitoringEvents.SnapshotEventName,
                    batch.Snapshot,
                    id: null,
                    context.RequestAborted);
                nextSnapshotAt = now.AddSeconds(5);
            }

            foreach (var evt in batch.Events)
            {
                await DashboardMonitoringEvents.WriteServerSentEventAsync(
                    context.Response.Body,
                    evt.Event,
                    evt,
                    DashboardMonitoringEvents.FormatEventId(evt.Id),
                    context.RequestAborted);
                sinceEventId = evt.Id;
            }

            await DashboardMonitoringEvents.WriteKeepAliveAsync(context.Response.Body, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(TimeSpan.FromSeconds(1), context.RequestAborted);
            current = await LoadAsync(services, context.RequestAborted);
            goal = ResolveGoal(current, goalId);
        }
    }

    private static GoalMonitoringBatchDto BuildMonitoringBatch(
        DashboardEndpointServices services,
        AgentOrchestratorKernel kernel,
        Goal goal,
        long sinceEventId)
    {
        var agents = services.LoadAgentCatalog().Agents;
        var workerProfiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        var inbox = OperatorInbox.Build(kernel, agents, workerProfiles, services.Workspace, goal.Id.Value[..8], includeAcknowledged: false);
        var subscriptionPlan = SubscriptionPlanBuilder.Build(goal, agents, workerProfiles);
        return DashboardMonitoringEvents.BuildBatch(
            kernel,
            goal,
            sinceEventId,
            DashboardResponseMapper.ToOperatorInboxReportDto(inbox),
            DashboardResponseMapper.ToSubscriptionPlanDto(subscriptionPlan).CapacitySchedule);
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
