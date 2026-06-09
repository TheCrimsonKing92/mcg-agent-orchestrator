using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private static async Task<IResult> HandleProviderSmokeAsync(HttpContext context)
    {
        var target = context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            ? DashboardRequestParser.ParseProviderSmokeSubmission(await ReadRequestBodyAsync(context.Request))
            : DashboardRequestParser.GetQueryValue(context.Request, "target") ?? ProviderSmokeRunner.DefaultTarget;
        return Json(await ProviderSmokeRunner.RunProviderSmokeReportAsync(target));
    }

    private static async Task<IResult> HandleAgentsAsync(HttpContext context, DashboardEndpointServices services)
    {
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var catalog = AgentCatalogStore.Load(services.AgentCatalogPath);
            var submission = DashboardRequestParser.ParseAgentSubmission(await ReadRequestBodyAsync(context.Request));
            var updatedAgent = DashboardRequestParser.CreateAgentDefinition(submission);
            AgentCatalogStore.Save(services.AgentCatalogPath, catalog.UpsertRole(updatedAgent));
            return Json(DashboardResponseMapper.ToAgentDto(updatedAgent));
        }

        var agents = AgentCatalogStore.Load(services.AgentCatalogPath).Agents;
        return Json(agents.Select(DashboardResponseMapper.ToAgentDto).ToList());
    }

    private static async Task<IResult> HandleWorkerProfilesAsync(HttpContext context, DashboardEndpointServices services)
    {
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var catalog = WorkerProfileStore.Load(services.WorkerProfilePath);
            var submission = DashboardRequestParser.ParseWorkerProfileSubmission(await ReadRequestBodyAsync(context.Request));
            var updatedCatalog = catalog.Upsert(new WorkerProfile(submission.Name, submission.CommandTemplate));
            WorkerProfileStore.Save(services.WorkerProfilePath, updatedCatalog);
            var profile = DashboardResponseMapper.ToWorkerProfileDtos(services.AgentCatalogPath, updatedCatalog)
                .Single(profile => profile.Name.Equals(submission.Name, StringComparison.OrdinalIgnoreCase));
            return Json(profile);
        }

        var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        return Json(DashboardResponseMapper.ToWorkerProfileDtos(services.AgentCatalogPath, profiles));
    }
}
