using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.Dashboard.Rendering;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private static readonly string[] GetAndPost = ["GET", "POST"];

    public static void MapDashboardEndpoints(
        this WebApplication app,
        IOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        DashboardHostArgs args,
        AgentCatalog? agentCatalogFallback = null)
    {
        var services = new DashboardEndpointServices(new DashboardStateService(repository), workspace, providers, args, app.Lifetime, new DashboardContinuationService(), agentCatalogFallback);
        if (args.EnableOperatorControls)
        {
            services.Continuations.RestoreSubscriptionWatches(services);
        }

        app.MapGet("/health", () => Text("ok", "text/plain; charset=utf-8"));
        app.MapGet("/assets/dashboard.css", (HttpContext context) => NoStoreText(context, DashboardAssets.Styles, "text/css; charset=utf-8"));
        if (args.EnableOperatorControls)
        {
            app.MapGet("/assets/dashboard.js", (HttpContext context) => NoStoreText(context, DashboardAssets.OperatorControlsScript, "text/javascript; charset=utf-8"));
        }

        app.MapGet("/", async Task<IResult> (HttpContext context) => await Safe(() => RenderDashboardAsync(context, services, DashboardView.Ops)));
        app.MapGet("/dashboard", async Task<IResult> (HttpContext context) => await Safe(() => RenderDashboardAsync(context, services, DashboardView.Ops)));
        app.MapGet("/config", async Task<IResult> (HttpContext context) => await Safe(() => RenderDashboardAsync(context, services, DashboardView.Config)));
        app.MapGet("/system", async Task<IResult> (HttpContext context) => await Safe(() => RenderDashboardAsync(context, services, DashboardView.System)));
        app.MapGet("/goal/{goalPrefix}", async Task<IResult> (HttpContext context, string goalPrefix) => await Safe(() => RenderDashboardAsync(context, services, DashboardView.Goal, goalPrefix)));

        var api = app.MapGroup("/api");
        api.MapGet("/health", () => Json(BuildHealthReport(services)));
        api.MapGet("/doctor", () => Json(BuildHealthReport(services)));
        api.MapGet("/continuations", () => Json(services.Continuations.GetStatuses()));
        api.MapGet("/continuations/summary", () => Json(services.Continuations.GetSummary()));
        api.MapGet("/backlog/goal-plan", async Task<IResult> (HttpContext context) => await Safe(() => GetBacklogGoalPlan(context, services)));
        api.MapGet("/goals/scope-collision-advisory", async Task<IResult> (HttpContext context) => await Safe(() => GetScopeCollisionAdvisoryAsync(context, services)));
        api.MapGet("/goals/cross-start-plan", async Task<IResult> (HttpContext context) => await Safe(() => GetCrossGoalStartPlanAsync(context, services)));
        api.MapGet("/system/dashboard-host", () => Json(BuildHostInfo(services)));
        api.MapGet("/system/architecture", () => Json(BuildArchitectureReport(services)));
        api.MapGet("/system/task-durations", async Task<IResult> (HttpContext context) => await Safe(() => GetTaskDurationsAsync(context, services)));
        if (args.EnableOperatorControls)
        {
            api.MapGet("/system/processes", () => Json(DashboardProcessInspector.InspectCurrent()));
            api.MapGet("/system/build-test-cleanup", () => Json(BuildTestCleanup(services)));
            api.MapGet("/system/build-test-runs", () => Json(BuildTestRuns(services)));
            api.MapGet("/system/build-test-runs/log", async Task<IResult> (HttpContext context) => await Safe(() => BuildTestRunLog(context, services)));
            api.MapPost("/system/run-build-test-cycle", () => Safe(() => RunBuildTestCycle(services)));
            api.MapPost("/system/stop-dashboard", () => Safe(() => StopDashboard(services)));
            api.MapMethods("/provider-smoke", GetAndPost, async Task<IResult> (HttpContext context) => await Safe(() => HandleProviderSmokeAsync(context)));
            api.MapMethods("/agents", GetAndPost, async Task<IResult> (HttpContext context) => await Safe(() => HandleAgentsAsync(context, services)));
            api.MapMethods("/worker-profiles", GetAndPost, async Task<IResult> (HttpContext context) => await Safe(() => HandleWorkerProfilesAsync(context, services)));
            api.MapMethods("/goals", GetAndPost, async Task<IResult> (HttpContext context) => await Safe(() => HandleGoalsAsync(context, services)));
        }
        else
        {
            api.MapGet("/goals", async Task<IResult> (HttpContext context) => await Safe(() => HandleGoalsAsync(context, services)));
            api.MapPost("/goals", () => ReadOnly());
            api.MapPost("/system/run-build-test-cycle", () => ReadOnly());
            api.MapPost("/system/stop-dashboard", () => ReadOnly());
        }

        api.MapGet("/goals/{goalId}", (string goalId) => Safe(() => GetGoalAsync(goalId, services)));

        var goals = api.MapGroup("/goals/{goalId}");
        goals.MapGet("/events", async Task<IResult> (HttpContext context, string goalId) => await Safe(() => GetGoalEventsAsync(context, goalId, services)));
        goals.MapGet("/events/stream", async Task (HttpContext context, string goalId) => await StreamGoalEventsAsync(context, goalId, services));
        goals.MapGet("/transcript", (string goalId) => Safe(() => GetGoalTranscriptAsync(goalId, services)));
        goals.MapGet("/subscription-plan", (string goalId) => Safe(() => GetSubscriptionPlanAsync(goalId, services)));
        goals.MapGet("/work-summary", (string goalId) => Safe(() => GetGoalWorkSummaryAsync(goalId, services)));
        goals.MapGet("/operator-intents", (string goalId) => Safe(() => GetGoalOperatorIntentsAsync(goalId, services)));
        goals.MapGet("/failure-triage", (HttpContext context, string goalId) => Safe(() => GetFailureTriageAsync(context, goalId, services)));
        goals.MapGet("/action-recommendations", (HttpContext context, string goalId) => Safe(() => GetActionRecommendationsAsync(context, goalId, services)));
        if (args.EnableOperatorControls)
        {
            goals.MapPost("/tasks", async Task<IResult> (HttpContext context, string goalId) => await Safe(() => AddTaskAsync(context, goalId, services)));
            goals.MapPost("/ask", async Task<IResult> (HttpContext context, string goalId) => await Safe(() => AskGoalAsync(context, goalId, services)));
            goals.MapMethods("/tasks/{taskId}/{operation}", GetAndPost, async Task<IResult> (HttpContext context, string goalId, string taskId, string operation) =>
                await Safe(() => HandleTaskOperationAsync(context, goalId, taskId, operation, services)));
            goals.MapPost("/delegate", (string goalId) => Safe(() => DelegateGoalAsync(goalId, services)));
            goals.MapPost("/advance", (string goalId) => Safe(() => AdvanceGoalAsync(goalId, services)));
            goals.MapPost("/advance-subscription", (HttpContext context, string goalId) => Safe(() => AdvanceGoalWithSubscriptionsAsync(context, goalId, services)));
            goals.MapPost("/advance-until-blocked", (string goalId) => Safe(() => AdvanceGoalUntilBlockedAsync(goalId, services)));
            goals.MapPost("/advance-subscription-until-blocked", (HttpContext context, string goalId) => Safe(() => AdvanceGoalWithSubscriptionsUntilBlockedAsync(context, goalId, services)));
            goals.MapMethods("/supervisor", GetAndPost, async Task<IResult> (HttpContext context, string goalId) =>
                await Safe(() => HandleGoalSupervisorAsync(context, goalId, services)));
            goals.MapPost("/{operation}", async Task<IResult> (HttpContext context, string goalId, string operation) =>
                await Safe(() => HandleGoalBatchOperationAsync(context, goalId, operation, services)));
        }
        else
        {
            goals.MapPost("/tasks", () => ReadOnly());
            goals.MapPost("/ask", () => ReadOnly());
            goals.MapGet("/tasks/{taskId}/{operation}", () => ReadOnly());
            goals.MapPost("/tasks/{taskId}/{operation}", () => ReadOnly());
            goals.MapPost("/delegate", () => ReadOnly());
            goals.MapPost("/advance", () => ReadOnly());
            goals.MapPost("/advance-subscription", () => ReadOnly());
            goals.MapPost("/advance-until-blocked", () => ReadOnly());
            goals.MapPost("/advance-subscription-until-blocked", () => ReadOnly());
            goals.MapGet("/supervisor", () => ReadOnly());
            goals.MapPost("/supervisor", () => ReadOnly());
            goals.MapPost("/{operation}", () => ReadOnly());
        }

        api.MapGet("/monitor", async Task<IResult> (HttpContext context) => await Safe(() => GetMonitorAsync(context, services)));
        api.MapGet("/acceptance", async Task<IResult> (HttpContext context) => await Safe(() => GetAcceptanceAsync(context, services)));
        api.MapGet("/evidence", async Task<IResult> (HttpContext context) => await Safe(() => GetEvidenceAsync(context, services)));
        api.MapGet("/stages", async Task<IResult> (HttpContext context) => await Safe(() => GetStagesAsync(context, services)));
        api.MapGet("/gates", async Task<IResult> (HttpContext context) => await Safe(() => GetGatesAsync(context, services)));
        api.MapGet("/verification-worklist", async Task<IResult> (HttpContext context) => await Safe(() => GetVerificationWorklistAsync(context, services)));
        api.MapGet("/human-input-worklist", async Task<IResult> (HttpContext context) => await Safe(() => GetHumanInputWorklistAsync(context, services)));
        api.MapGet("/next", async Task<IResult> (HttpContext context) => await Safe(() => GetNextActionsAsync(context, services)));
        api.MapGet("/operator-inbox", async Task<IResult> (HttpContext context) => await Safe(() => GetOperatorInboxAsync(context, services)));
        api.MapGet("/operator-intents/{intentId}", (string intentId) => Safe(() => GetOperatorIntentAsync(intentId, services)));
        api.MapGet("/source-survey", async Task<IResult> (HttpContext context) => await Safe(() => GetSourceSurvey(context, services)));
        api.MapGet("/pending-input", async Task<IResult> (HttpContext context) => await Safe(() => GetPendingInputAsync(context, services)));
        if (args.EnableOperatorControls)
        {
            api.MapPost("/operator-inbox/ack", async Task<IResult> (HttpContext context) =>
                await Safe(() => AcknowledgeOperatorInboxItemAsync(context, services)));
            api.MapPost("/input/{inputId}/answer", async Task<IResult> (HttpContext context, string inputId) =>
                await Safe(() => AnswerHumanInputAsync(context, inputId, services)));
            api.MapPost("/input/{inputId}/dismiss", async Task<IResult> (HttpContext context, string inputId) =>
                await Safe(() => DismissHumanInputAsync(context, inputId, services)));
        }
        else
        {
            api.MapPost("/operator-inbox/ack", () => ReadOnly());
            api.MapPost("/input/{inputId}/answer", () => ReadOnly());
        }

        api.MapGet("/tasks", async Task<IResult> (HttpContext context) => await Safe(() => QueryTasksAsync(context, services)));
        api.MapGet("/tasks/{taskId}/work-summary", async Task<IResult> (HttpContext context, string taskId) => await Safe(() => GetTaskWorkSummaryAsync(context, taskId, services)));
        api.MapGet("/task/{taskId}", async Task<IResult> (HttpContext context, string taskId) => await Safe(() => GetTaskAsync(context, taskId, services)));

        // Conductor tick push endpoint: CLI conduct --loop --watch POSTs tick events here for SSE broadcast.
        api.MapPost("/conductor/tick", async Task<IResult> (HttpContext context) => await HandleConductorTickAsync(context, services));

        app.MapFallback(() => Text("not found", "text/plain; charset=utf-8", StatusCodes.Status404NotFound));
    }

    public static OrchestratorHealthReport BuildHealthReport(DashboardEndpointServices services)
    {
        return OrchestratorHealthInspector.InspectCurrentEnvironment(
            services.LoadAgentCatalog(),
            WorkerProfileStore.Load(services.WorkerProfilePath));
    }

    private static async Task<IResult> GetTaskDurationsAsync(HttpContext context, DashboardEndpointServices services)
    {
        var includeModel = context.Request.Query.ContainsKey("byModel") ||
            context.Request.Query.ContainsKey("by-model");
        var current = await LoadAsync(services, context.RequestAborted);
        return Json(BuildTaskDurationStatsReport(current, includeModel));
    }

    private static Task<AgentOrchestratorKernel> LoadAsync(DashboardEndpointServices services, CancellationToken cancellationToken = default) =>
        services.State.LoadAsync(cancellationToken);

    private static Task<IResult> MutateAsync(
        DashboardEndpointServices services,
        Func<AgentOrchestratorKernel, Task<IResult>> mutation,
        CancellationToken cancellationToken = default) =>
        services.State.MutateAsync(mutation, cancellationToken);

    private static Task<IResult> MutateAsync(
        DashboardEndpointServices services,
        Func<AgentOrchestratorKernel, Func<Task>, Task<IResult>> mutation,
        CancellationToken cancellationToken = default) =>
        services.State.MutateAsync(mutation, cancellationToken);

    private static Task<IResult> MutateIfChangedAsync(
        DashboardEndpointServices services,
        Func<AgentOrchestratorKernel, Task<(bool Changed, IResult Result)>> mutation,
        CancellationToken cancellationToken = default) =>
        services.State.MutateIfChangedAsync(mutation, cancellationToken);

    private static Goal ResolveGoal(HttpRequest request, AgentOrchestratorKernel kernel)
    {
        return OrchestratorEntityResolver.ResolveGoal(
            kernel,
            OrchestratorEntityResolver.GetLatestGoal(kernel),
            DashboardRequestParser.GetQueryValue(request, "goal"));
    }

    private static Goal ResolveGoal(AgentOrchestratorKernel kernel, string goalId)
    {
        return OrchestratorEntityResolver.ResolveGoal(kernel, OrchestratorEntityResolver.GetLatestGoal(kernel), goalId);
    }

    private static IResult Json(object payload, int statusCode = StatusCodes.Status200OK)
    {
        return Results.Json(payload, DashboardJson.Options(), statusCode: statusCode);
    }

    private static IResult Text(string content, string contentType, int statusCode = StatusCodes.Status200OK)
    {
        return Results.Text(content, contentType, statusCode: statusCode);
    }

    private static IResult ReadOnly()
    {
        return Text(
            "dashboard read-only: simple hosted dashboard does not allow operator actions",
            "text/plain; charset=utf-8",
            StatusCodes.Status403Forbidden);
    }

    private static IResult NoStoreText(HttpContext context, string content, string contentType)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Text(content, contentType);
    }

    private static async Task<IResult> Safe(Func<Task<IResult>> handler)
    {
        try
        {
            return await handler();
        }
        catch (KeyNotFoundException ex)
        {
            return Text(ErrorText("dashboard not found", ex.Message), "text/plain; charset=utf-8", StatusCodes.Status404NotFound);
        }
        catch (ArgumentException ex)
        {
            return Text(ErrorText("dashboard invalid request", ex.Message), "text/plain; charset=utf-8", StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            return Text(ErrorText("dashboard error", ex.Message), "text/plain; charset=utf-8", StatusCodes.Status500InternalServerError);
        }
    }

    private static Task<IResult> Safe(Func<IResult> handler) => Safe(() => Task.FromResult(handler()));

    private static string ErrorText(string prefix, string message) =>
        $"{prefix}: {OutputTextPreview.CreateTimeline(message).Text}";

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
