using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.App.Dashboard.Hosting;
using Mcg.AgentOrchestrator.App.Dashboard.Rendering;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private static async Task<IResult> RenderDashboardAsync(
        HttpContext context,
        DashboardEndpointServices services,
        DashboardView view,
        string? goalPrefix = null)
    {
        var current = await LoadAsync(services, context.RequestAborted);
        var agentCatalog = services.LoadAgentCatalog();
        var workerProfiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        var health = BuildHealthReport(services);
        var browserUrl = DashboardHost.GetBrowserUrl(services.HostArgs);
        var hostedUrlPrefixes = DashboardHost.GetHostedUrlPrefixes(services.HostArgs);
        var focusGoalPrefix = goalPrefix
            ?? (context.Request.Query.TryGetValue("goal", out var focusGoalValues)
                ? focusGoalValues.FirstOrDefault()
                : null);
        var operatorInbox = OperatorInbox.Build(
            current,
            agentCatalog.Agents,
            workerProfiles,
            services.Workspace,
            focusGoalPrefix,
            includeAcknowledged: false);
        var html = DashboardRenderer.Render(
            current,
            new DashboardRenderOptions(
                services.HostArgs.AutoRefreshSeconds,
                EnableOperatorControls: services.HostArgs.EnableOperatorControls,
                health,
                new DashboardWorkspaceContext(
                    services.Workspace.RootDirectory,
                    services.Workspace.SqliteStatePath,
                    services.Workspace.ExecutionDirectory,
                    services.Workspace.PromptDirectory,
                    services.Workspace.LogDirectory,
                    services.Workspace.WorkerProfilePath,
                    services.Workspace.AgentCatalogPath,
                    Environment.ProcessId,
                    BuildDashboardRestartCommand(services),
                    DashboardProcessInspector.InspectCurrent(),
                    services.HostArgs.UrlPrefix,
                    DashboardHost.GetDashboardPageUrl(browserUrl),
                    hostedUrlPrefixes
                        .Select(DashboardHost.GetDashboardPageUrl)
                        .ToList(),
                    DashboardHost.GetSourceSurveyUrl(browserUrl),
                    hostedUrlPrefixes
                        .Select(DashboardHost.GetSourceSurveyUrl)
                        .ToList(),
                    DashboardHost.GetHostedAccessNote(services.HostArgs.UrlPrefix, hostedUrlPrefixes),
                    BuildTestRuns(services)),
                services.Continuations.GetStatuses(),
                focusGoalPrefix,
                view,
                agentCatalog.Agents,
                workerProfiles,
                DashboardResponseMapper.ToOperatorInboxReportDto(operatorInbox),
                BuildTaskDurationStatsReport(current)));
        return Text(html, "text/html; charset=utf-8");
    }
}
