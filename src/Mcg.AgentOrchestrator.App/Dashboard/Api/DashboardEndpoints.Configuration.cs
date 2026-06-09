using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private static async Task<IResult> HandleProviderSmokeAsync(HttpContext context)
    {
        var isPost = context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        var target = isPost
            ? DashboardRequestParser.ParseProviderSmokeSubmission(await ReadRequestBodyAsync(context.Request))
            : DashboardRequestParser.GetQueryValue(context.Request, "target") ?? ProviderSmokeRunner.DefaultTarget;
        if (!isPost && target.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Smoking all providers requires POST so broad paid smoke tests are deliberate.");
        }

        if (!isPost && ProviderSmokeRunner.RequiresPaidConfirmation(target))
        {
            throw new ArgumentException("Paid provider smoke requires POST with confirmPaidSmoke=true because it can make a live billable request.");
        }

        return Json(await ProviderSmokeRunner.RunProviderSmokeReportAsync(target));
    }

    private static async Task<IResult> HandleAgentsAsync(HttpContext context, DashboardEndpointServices services)
    {
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var catalog = services.LoadAgentCatalog();
            var submission = DashboardRequestParser.ParseAgentSubmission(await ReadRequestBodyAsync(context.Request));
            var updatedAgent = DashboardRequestParser.CreateAgentDefinition(submission);
            AgentCatalogStore.Save(services.AgentCatalogPath, catalog.UpsertRole(updatedAgent));
            return Json(DashboardResponseMapper.ToAgentDto(updatedAgent));
        }

        var agents = services.LoadAgentCatalog().Agents;
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
            var profile = DashboardResponseMapper.ToWorkerProfileDtos(services.LoadAgentCatalog(), updatedCatalog)
                .Single(profile => profile.Name.Equals(submission.Name, StringComparison.OrdinalIgnoreCase));
            return Json(profile);
        }

        var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        return Json(DashboardResponseMapper.ToWorkerProfileDtos(services.LoadAgentCatalog(), profiles));
    }
}
