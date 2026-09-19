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
            : DashboardHttpRequestParser.GetQueryValue(context.Request, "target") ?? ProviderSmokeRunner.DefaultTarget;
        if (!isPost && target.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(ProviderSmokeRunner.BuildBroadSmokeConfirmationMessage("POST with confirmAll=true"));
        }

        if (!isPost && ProviderSmokeRunner.RequiresPaidConfirmation(target))
        {
            throw new ArgumentException(ProviderSmokeRunner.BuildPaidSmokeConfirmationMessage("POST with confirmPaidSmoke=true"));
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
            var submittedProfile = new WorkerProfile(submission.Name, submission.CommandTemplate);
            var updatedCatalog = catalog.Upsert(submittedProfile);
            WorkerProfileStore.Save(services.WorkerProfilePath, updatedCatalog);
            return Json(DashboardResponseMapper.ToWorkerProfileDto(submittedProfile, validation: null));
        }

        var profiles = WorkerProfileStore.Load(services.WorkerProfilePath);
        return Json(DashboardResponseMapper.ToWorkerProfileDtos(services.LoadAgentCatalog(), profiles));
    }
}
