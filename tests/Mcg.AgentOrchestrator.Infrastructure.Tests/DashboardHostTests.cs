using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Dashboard.Hosting;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Prototype;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

// Host-integration tests: these spawn a real Kestrel dashboard server (dotnet App.dll
// serve-dashboard) which binds a port and needs an interactive firewall allow. They cannot run in
// the hands-off acceptance gate (unattended + relocated build outputs), so they are tagged and
// excluded there (see GoalAcceptanceVerifier) and run locally / in a dedicated lane instead.
[Xunit.Trait("Category", "HostIntegration")]
public sealed class DashboardHostTests
{
    [Xunit.Fact(DisplayName = "Simple_hosted_dashboard_serves_read_only_metadata_and_survey")]
    public async Task SimpleHostedDashboardServesReadOnlyMetadataAndSurvey()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var providers = new InMemoryModelProviderRegistry([]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "Simple hosted dashboard goal"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        var goal = currentGoal ?? throw new InvalidOperationException("Expected simple-goal to create a current goal.");
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        var task = goal.Tasks.Single();
        var port = GetAvailablePort();
        var url = $"http://localhost:{port}/";
        var appProject = Path.Combine(FindRepositoryRoot(), "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        using var process = StartDashboardProcess(appProject, root, "simple-hosted-dashboard", url);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            await WaitForHealthAsync(client, url, process);

            var dashboardHostMetadata = await client.GetStringAsync(new Uri(new Uri(url), "api/system/dashboard-host"));
            var architecture = await client.GetStringAsync(new Uri(new Uri(url), "api/system/architecture"));
            var goalWorkSummary = await GetRequiredStringAsync(client, new Uri(new Uri(url), $"api/goals/{goal.Id.Value}/work-summary"));
            var taskWorkSummary = await GetRequiredStringAsync(client, new Uri(new Uri(url), $"api/tasks/{task.Id.Value}/work-summary"));
            var sourceSurvey = await client.GetStringAsync(new Uri(new Uri(url), "api/source-survey?max=8"));
            var defaultSourceSurvey = await client.GetStringAsync(new Uri(new Uri(url), "api/source-survey"));
            using var createGoalResponse = await client.PostAsync(
                new Uri(new Uri(url), "api/goals"),
                new StringContent(
                    "{\"objective\":\"should be read only\"}",
                    System.Text.Encoding.UTF8,
                    "application/json"));
            var createGoal = await createGoalResponse.Content.ReadAsStringAsync();
            using var getTaskOperationResponse = await client.GetAsync(
                new Uri(new Uri(url), $"api/goals/{goal.Id.Value}/tasks/1/refresh"));
            var getTaskOperation = await getTaskOperationResponse.Content.ReadAsStringAsync();
            using (var hostDocument = JsonDocument.Parse(dashboardHostMetadata))
            {
                var host = hostDocument.RootElement;
                Assert.Equal("simple-hosted-dashboard", host.GetProperty("CommandName").GetString());
                Assert.False(host.GetProperty("OperatorControlsEnabled").GetBoolean());
                Assert.True(host.GetProperty("SourceSurveyUrl").GetString()?.EndsWith("/api/source-survey?max=8", StringComparison.Ordinal) is true);
                Assert.True(host.GetProperty("RestartCommand").GetString()!.Contains("simple-hosted-dashboard", StringComparison.Ordinal));
                Assert.Equal("default", host.GetProperty("TenantName").GetString());
                Assert.False(host.GetProperty("TenantScoped").GetBoolean());
                Assert.Equal(workspace.SqliteStatePath, host.GetProperty("StatePath").GetString());
            }

            using (var architectureDocument = JsonDocument.Parse(architecture))
            {
                var report = architectureDocument.RootElement;
                Assert.Equal("default", report.GetProperty("TenantName").GetString());
                Assert.Equal(workspace.SqliteStatePath, report.GetProperty("StatePath").GetString());
                Assert.True(report.GetProperty("Persistence").GetString()?.Contains("SQLite state.db", StringComparison.Ordinal) is true);
                Assert.False(report.GetProperty("OperatorControlsEnabled").GetBoolean());
                AssertArchitectureArrayContains(report.GetProperty("ApiSurfaces"), "/api/system/architecture");
                AssertArchitectureArrayContains(report.GetProperty("DashboardModes"), "simple-hosted-dashboard");
                AssertArchitectureArrayContains(report.GetProperty("StateStores"), workspace.ContinuationStorePath);
                AssertArchitectureArrayContains(report.GetProperty("DistributedBoundaries"), "Subscription execution leaves process boundaries");
                AssertArchitectureArrayContains(report.GetProperty("SafetyGates"), "Tenant names are normalized");
            }

            using (var goalSummaryDocument = JsonDocument.Parse(goalWorkSummary))
            {
                var summary = goalSummaryDocument.RootElement;
                Assert.Equal(goal.Id.Value, summary.GetProperty("GoalId").GetString());
                Assert.Equal("simple-hosted-dashboard", summary.GetProperty("Host").GetProperty("CommandName").GetString());
                Assert.False(summary.GetProperty("Host").GetProperty("OperatorControlsEnabled").GetBoolean());
                Assert.True(summary.GetProperty("Tasks").EnumerateArray().Any(summaryTask =>
                    summaryTask.GetProperty("TaskId").GetString() == task.Id.Value));
            }

            using (var taskSummaryDocument = JsonDocument.Parse(taskWorkSummary))
            {
                var summary = taskSummaryDocument.RootElement;
                Assert.Equal(goal.Id.Value, summary.GetProperty("GoalId").GetString());
                Assert.Equal(task.Id.Value, summary.GetProperty("Task").GetProperty("TaskId").GetString());
                Assert.Equal("simple-hosted-dashboard", summary.GetProperty("Host").GetProperty("CommandName").GetString());
                Assert.False(summary.GetProperty("Host").GetProperty("OperatorControlsEnabled").GetBoolean());
                Assert.False(summary.TryGetProperty("Tasks", out _));
            }

            Assert.True(sourceSurvey.Contains("\"MaxFiles\": 8", StringComparison.Ordinal));
            Assert.True(defaultSourceSurvey.Contains("\"MaxFiles\": 8", StringComparison.Ordinal));
            Assert.Equal(HttpStatusCode.Forbidden, createGoalResponse.StatusCode);
            Assert.True(createGoal.Contains("dashboard read-only", StringComparison.Ordinal));
            Assert.Equal(HttpStatusCode.Forbidden, getTaskOperationResponse.StatusCode);
            Assert.True(getTaskOperation.Contains("dashboard read-only", StringComparison.Ordinal));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Xunit.Fact(DisplayName = "Prototype_dashboard_serves_health_and_goal_json_over_kestrel")]
    public async Task PrototypeDashboardServesHealthAndGoalJsonOverKestrel()
    {
        var root = CreateTempDirectory();
        SeedSpecRefinerBinding(OrchestratorWorkspace.ForDirectory(PrototypeWorkspaceSeeder.GetWorkspacePath(root), root));
        var port = GetAvailablePort();
        var url = $"http://localhost:{port}/";
        var appProject = Path.Combine(FindRepositoryRoot(), "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        using var process = StartPrototypeDashboardProcess(appProject, root, url);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            await WaitForHealthAsync(client, url, process);

            var goals = await client.GetStringAsync(new Uri(new Uri(url), "api/goals"));
            using var workerProfileResponse = await client.PostAsync(
                new Uri(new Uri(url), "api/worker-profiles"),
                new StringContent(
                "{\"name\":\"codex-cli\",\"commandTemplate\":\"Write-Output {promptPath}\"}",
                    System.Text.Encoding.UTF8,
                    "application/json"));
            using var manualGoalResponse = await client.PostAsync(
                new Uri(new Uri(url), "api/goals"),
                new StringContent(
                    "{\"objective\":\"Manual hosted dashboard goal\",\"workflow\":\"simple\",\"autoHandoff\":false}",
                    System.Text.Encoding.UTF8,
                    "application/json"));
            var manualGoal = await manualGoalResponse.Content.ReadAsStringAsync();
            var manualGoalId = JsonDocument.Parse(manualGoal).RootElement.GetProperty("Goal").GetProperty("Id").GetString()!;
            using var batchStartMissingConfirmResponse = await client.PostAsync(
                new Uri(new Uri(url), $"api/goals/{manualGoalId}/start-dispatches"),
                new StringContent(string.Empty));
            var batchStartMissingConfirm = await batchStartMissingConfirmResponse.Content.ReadAsStringAsync();
            using var dispatchStartMissingConfirmResponse = await client.PostAsync(
                new Uri(new Uri(url), $"api/goals/{manualGoalId}/tasks/1/start"),
                new StringContent(string.Empty));
            var dispatchStartMissingConfirm = await dispatchStartMissingConfirmResponse.Content.ReadAsStringAsync();
            using var subscriptionAdvanceMissingConfirmResponse = await client.PostAsync(
                new Uri(new Uri(url), $"api/goals/{manualGoalId}/advance-subscription"),
                new StringContent(string.Empty));
            var subscriptionAdvanceMissingConfirm = await subscriptionAdvanceMissingConfirmResponse.Content.ReadAsStringAsync();
            using var taskRunMissingConfirmResponse = await client.PostAsync(
                new Uri(new Uri(url), $"api/goals/{manualGoalId}/tasks/1/run"),
                new StringContent(string.Empty));
            var taskRunMissingConfirm = await taskRunMissingConfirmResponse.Content.ReadAsStringAsync();
            using var paidApiRunMissingConfirmResponse = await client.PostAsync(
                new Uri(new Uri(url), $"api/goals/{manualGoalId}/tasks/1/api-run?confirmTaskRun=true"),
                new StringContent(string.Empty));
            var paidApiRunMissingConfirm = await paidApiRunMissingConfirmResponse.Content.ReadAsStringAsync();
            using var developerAgentResponse = await client.PostAsync(
                new Uri(new Uri(url), "api/agents"),
                new StringContent(
                    "{\"role\":\"Developer\",\"providerName\":\"OpenAI\",\"modelName\":\"gpt-5.5\",\"executionPolicy\":\"PreferSubscription\",\"subscriptionProfileName\":\"codex-cli\"}",
                    System.Text.Encoding.UTF8,
                    "application/json"));
            var developerAgent = await developerAgentResponse.Content.ReadAsStringAsync();
            using var simpleGoalResponse = await client.PostAsync(
                new Uri(new Uri(url), "api/goals"),
                new StringContent(
                    "{\"objective\":\"Simple hosted dashboard goal\",\"workflow\":\"simple\",\"autoHandoff\":false}",
                    System.Text.Encoding.UTF8,
                    "application/json"));
            var simpleGoal = await simpleGoalResponse.Content.ReadAsStringAsync();
            const string largeAutoHandoffObjective = "Design and implement a production multi-tenant distributed architecture with API CLI dashboard provider subscription worker persistence state tests and rollback safety";
            using var largeAutoHandoffResponse = await client.PostAsync(
                new Uri(new Uri(url), "api/goals"),
                new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        objective = largeAutoHandoffObjective,
                        workflow = "simple",
                        autoHandoff = true,
                        confirmAutoHandoff = true
                    }),
                    System.Text.Encoding.UTF8,
                    "application/json"));
            var largeAutoHandoffGoal = await largeAutoHandoffResponse.Content.ReadAsStringAsync();
            using var defaultManualGoalResponse = await client.PostAsync(
                new Uri(new Uri(url), "api/goals"),
                new StringContent(
                    "{\"objective\":\"Default manual hosted dashboard goal\",\"workflow\":\"simple\"}",
                    System.Text.Encoding.UTF8,
                    "application/json"));
            var defaultManualGoal = await defaultManualGoalResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Created, simpleGoalResponse.StatusCode);
            var simpleGoalId = JsonDocument.Parse(simpleGoal).RootElement.GetProperty("Goal").GetProperty("Id").GetString()!;
            var simpleTaskId = JsonDocument.Parse(simpleGoal).RootElement.GetProperty("Tasks")[0].GetProperty("Id").GetString()!;
            using var readinessBlockedStartResponse = await client.PostAsync(
                new Uri(new Uri(url), $"api/goals/{simpleGoalId}/start-subscription-ready?confirmBatchStart=true"),
                new StringContent(string.Empty));
            var readinessBlockedStart = await readinessBlockedStartResponse.Content.ReadAsStringAsync();
            var simpleGoalDetail = await client.GetStringAsync(new Uri(new Uri(url), $"api/goals/{simpleGoalId}"));
            var simpleGoalWorkSummary = await client.GetStringAsync(new Uri(new Uri(url), $"api/goals/{simpleGoalId}/work-summary"));
            var simpleGoalEvents = await client.GetStringAsync(new Uri(new Uri(url), $"api/goals/{simpleGoalId}/events"));
            var simpleTaskWorkSummary = await client.GetStringAsync(new Uri(new Uri(url), $"api/tasks/{simpleTaskId}/work-summary"));
            using var cssResponse = await client.GetAsync(new Uri(new Uri(url), "assets/dashboard.css"));
            var css = await cssResponse.Content.ReadAsStringAsync();
            var js = await client.GetStringAsync(new Uri(new Uri(url), "assets/dashboard.js"));
            using var missingTaskResponse = await client.GetAsync(new Uri(new Uri(url), "api/task/999"));
            var missingTask = await missingTaskResponse.Content.ReadAsStringAsync();
            var longMissingTaskId = "task-start-" + new string('t', 2000) + "-task-tail";
            using var longMissingTaskResponse = await client.GetAsync(new Uri(new Uri(url), $"api/task/{Uri.EscapeDataString(longMissingTaskId)}"));
            var longMissingTask = await longMissingTaskResponse.Content.ReadAsStringAsync();
            var sourceSurvey = await client.GetStringAsync(new Uri(new Uri(url), "api/source-survey?max=25"));
            var defaultSourceSurvey = await client.GetStringAsync(new Uri(new Uri(url), "api/source-survey"));
            var continuations = await client.GetStringAsync(new Uri(new Uri(url), "api/continuations"));
            var continuationSummary = await client.GetStringAsync(new Uri(new Uri(url), "api/continuations/summary"));
            var processDiagnostic = await client.GetStringAsync(new Uri(new Uri(url), "api/system/processes"));
            var dashboardHostMetadata = await client.GetStringAsync(new Uri(new Uri(url), "api/system/dashboard-host"));
            var cleanupPlan = await client.GetStringAsync(new Uri(new Uri(url), "api/system/build-test-cleanup"));
            var buildTestRuns = await client.GetStringAsync(new Uri(new Uri(url), "api/system/build-test-runs"));
            using var paidSmokeGetResponse = await client.GetAsync(new Uri(new Uri(url), "api/provider-smoke?target=openai"));
            var paidSmokeGet = await paidSmokeGetResponse.Content.ReadAsStringAsync();
            using var broadSmokeGetResponse = await client.GetAsync(new Uri(new Uri(url), "api/provider-smoke?target=all"));
            var broadSmokeGet = await broadSmokeGetResponse.Content.ReadAsStringAsync();
            using var broadSmokePostResponse = await client.PostAsync(
                new Uri(new Uri(url), "api/provider-smoke"),
                new StringContent(
                    "{\"target\":\"all\"}",
                    System.Text.Encoding.UTF8,
                    "application/json"));
            var broadSmokePost = await broadSmokePostResponse.Content.ReadAsStringAsync();
            using var invalidGoalResponse = await client.PostAsync(
                new Uri(new Uri(url), "api/goals"),
                new StringContent(
                    "{\"objective\":\"\"}",
                    System.Text.Encoding.UTF8,
                    "application/json"));
            var invalidGoal = await invalidGoalResponse.Content.ReadAsStringAsync();
            using var stopResponse = await client.PostAsync(new Uri(new Uri(url), "api/system/stop-dashboard"), new StringContent(string.Empty));
            var stopDashboard = await stopResponse.Content.ReadAsStringAsync();

            Assert.Contains("Prototype: explore the agent orchestrator UI", goals, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, workerProfileResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, developerAgentResponse.StatusCode);
            Assert.Contains("\"Role\": \"Developer\"", developerAgent, StringComparison.Ordinal);
            Assert.Contains("\"TotalTasks\": 1", simpleGoal, StringComparison.Ordinal);
            Assert.Contains("\"Role\": \"Developer\"", simpleGoal, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Conflict, readinessBlockedStartResponse.StatusCode);
            Assert.Contains("goal readiness preflight blocked start-subscription-ready", readinessBlockedStart, StringComparison.Ordinal);
            Assert.Contains("workspace-missing", readinessBlockedStart, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Created, largeAutoHandoffResponse.StatusCode);
            Assert.Contains("\"AutoHandoff\"", largeAutoHandoffGoal, StringComparison.Ordinal);
            Assert.Contains("\"StopReason\"", largeAutoHandoffGoal, StringComparison.Ordinal);
            Assert.False(largeAutoHandoffGoal.Contains("--confirm-large-paid-subscription-start", StringComparison.Ordinal));
            Assert.Contains("\"VerificationSatisfied\"", simpleGoalDetail, StringComparison.Ordinal);
            Assert.Contains(simpleGoalId, simpleGoalDetail, StringComparison.Ordinal);
            Assert.Contains("\"MonitoringStreamPath\"", simpleGoalDetail, StringComparison.Ordinal);
            using (var workSummaryDocument = JsonDocument.Parse(simpleGoalWorkSummary))
            {
                var summary = workSummaryDocument.RootElement;
                Assert.Equal(simpleGoalId, summary.GetProperty("GoalId").GetString());
                Assert.Equal($"/api/goals/{simpleGoalId}/events/stream", summary.GetProperty("MonitoringStreamPath").GetString());
                Assert.Equal(JsonValueKind.Array, summary.GetProperty("Tasks").ValueKind);
                Assert.True(summary.GetProperty("TotalTasks").GetInt32() > 0);
                Assert.True(summary.GetProperty("PendingHumanInputCount").GetInt32() >= 0);
                Assert.True(summary.TryGetProperty("NextAction", out _));
                Assert.True(summary.GetProperty("Tasks").EnumerateArray().Any(task =>
                    task.TryGetProperty("Evidence", out var evidence) &&
                    !string.IsNullOrWhiteSpace(evidence.GetString())));
            }
            using (var eventsDocument = JsonDocument.Parse(simpleGoalEvents))
            {
                var events = eventsDocument.RootElement;
                Assert.Equal(simpleGoalId, events.GetProperty("GoalId").GetString());
                Assert.Equal($"/api/goals/{simpleGoalId}/events/stream", events.GetProperty("StreamPath").GetString());
                Assert.True(events.GetProperty("LastEventId").GetInt64() >= 0);
                Assert.Equal(simpleGoalId, events.GetProperty("Snapshot").GetProperty("GoalId").GetString());
                Assert.Equal(JsonValueKind.Array, events.GetProperty("Events").ValueKind);
            }
            using (var taskWorkSummaryDocument = JsonDocument.Parse(simpleTaskWorkSummary))
            {
                var summary = taskWorkSummaryDocument.RootElement;
                Assert.Equal(simpleGoalId, summary.GetProperty("GoalId").GetString());
                Assert.Equal(simpleTaskId, summary.GetProperty("Task").GetProperty("TaskId").GetString());
                Assert.Equal("prototype-ui", summary.GetProperty("Host").GetProperty("CommandName").GetString());
                Assert.True(summary.GetProperty("Host").GetProperty("OperatorControlsEnabled").GetBoolean());
                Assert.True(summary.TryGetProperty("NextAction", out _));
                Assert.False(summary.TryGetProperty("Tasks", out _));
            }
            Assert.Equal(HttpStatusCode.Created, manualGoalResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Created, defaultManualGoalResponse.StatusCode);
            Assert.Contains("\"Status\":", simpleGoal, StringComparison.Ordinal);
            Assert.Contains("\"Status\": \"Assigned\"", manualGoal, StringComparison.Ordinal);
            Assert.False(GoalResponseContainsDispatch(manualGoal));
            Assert.Contains("\"Status\": \"Assigned\"", defaultManualGoal, StringComparison.Ordinal);
            Assert.False(GoalResponseContainsDispatch(defaultManualGoal));
            Assert.Equal("no-store", cssResponse.Headers.CacheControl?.ToString());
            Assert.Equal(HttpStatusCode.NotFound, missingTaskResponse.StatusCode);
            Assert.Contains("dashboard not found", missingTask, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, longMissingTaskResponse.StatusCode);
            Assert.Contains("dashboard not found", longMissingTask, StringComparison.Ordinal);
            Assert.Contains("task-start", longMissingTask, StringComparison.Ordinal);
            Assert.Contains("task-tail", longMissingTask, StringComparison.Ordinal);
            Assert.Contains("[truncated", longMissingTask, StringComparison.Ordinal);
            Assert.True(!longMissingTask.Contains(new string('t', 2000), StringComparison.Ordinal));
            Assert.Contains("\"MaxFiles\": 25", sourceSurvey, StringComparison.Ordinal);
            Assert.Contains("\"MaxFiles\": 200", defaultSourceSurvey, StringComparison.Ordinal);
            Assert.Contains("\"RecommendedCommand\"", sourceSurvey, StringComparison.Ordinal);
            Assert.Contains("!**/.scratch/**", sourceSurvey, StringComparison.Ordinal);
            Assert.False(SourceSurveyContainsFilePathSegment(sourceSurvey, "/bin/"));
            Assert.False(SourceSurveyContainsFilePathSegment(sourceSurvey, "/obj/"));
            var continuationArray = JsonDocument.Parse(continuations).RootElement;
            Assert.Equal(JsonValueKind.Array, continuationArray.ValueKind);
            using (var continuationSummaryDocument = JsonDocument.Parse(continuationSummary))
            {
                var summary = continuationSummaryDocument.RootElement;
                Assert.Equal(JsonValueKind.Object, summary.ValueKind);
                Assert.True(summary.GetProperty("Total").GetInt32() >= 0);
                Assert.True(summary.GetProperty("Running").GetInt32() >= 0);
                Assert.Equal(JsonValueKind.Array, summary.GetProperty("RunningGoalPrefixes").ValueKind);
            }
            using (var processDocument = JsonDocument.Parse(processDiagnostic))
            {
                Assert.True(processDocument.RootElement.GetProperty("CurrentProcessId").GetInt32() > 0);
                Assert.Equal("Mcg.AgentOrchestrator.App", processDocument.RootElement.GetProperty("ProcessName").GetString());
                Assert.Equal(JsonValueKind.Array, processDocument.RootElement.GetProperty("CurrentListeningPorts").ValueKind);
                Assert.Equal(JsonValueKind.Array, processDocument.RootElement.GetProperty("SiblingProcesses").ValueKind);
            }
            using (var hostDocument = JsonDocument.Parse(dashboardHostMetadata))
            {
                var host = hostDocument.RootElement;
                Assert.Equal("prototype-ui", host.GetProperty("CommandName").GetString());
                Assert.True(host.GetProperty("SourceSurveyUrl").GetString()?.EndsWith("/api/source-survey?max=8", StringComparison.Ordinal) is true);
                Assert.Equal(JsonValueKind.Array, host.GetProperty("HostedSourceSurveyUrls").ValueKind);
            }
            using (var cleanupDocument = JsonDocument.Parse(cleanupPlan))
            {
                Assert.True(cleanupDocument.RootElement.GetProperty("CurrentProcessId").GetInt32() > 0);
                Assert.Equal("/api/system/stop-dashboard", cleanupDocument.RootElement.GetProperty("StopCurrentUrl").GetString());
                Assert.Equal("/api/system/run-build-test-cycle", cleanupDocument.RootElement.GetProperty("RunBuildTestCycleUrl").GetString());
                Assert.Contains("Invoke-IsolatedDotnet.ps1 build Mcg.AgentOrchestrator.sln --no-restore --verbosity minimal", cleanupPlan, StringComparison.Ordinal);
                Assert.Contains("Invoke-IsolatedDotnet.ps1 test Mcg.AgentOrchestrator.sln --verbosity minimal", cleanupPlan, StringComparison.Ordinal);
                Assert.Contains("Get-Process Mcg.AgentOrchestrator.App -ErrorAction SilentlyContinue", cleanupPlan, StringComparison.Ordinal);
                Assert.Contains("Invoke-DashboardBuildTestCycle.ps1", cleanupPlan, StringComparison.Ordinal);
            }
            Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(buildTestRuns).RootElement.ValueKind);
            Assert.Equal(HttpStatusCode.BadRequest, paidSmokeGetResponse.StatusCode);
            Assert.Contains("confirmPaidSmoke=true", paidSmokeGet, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, broadSmokeGetResponse.StatusCode);
            Assert.Contains("broad paid smoke tests are deliberate", broadSmokeGet, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, broadSmokePostResponse.StatusCode);
            Assert.Contains("confirmAll=true", broadSmokePost, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, batchStartMissingConfirmResponse.StatusCode);
            Assert.Contains("confirmBatchStart=true", batchStartMissingConfirm, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, dispatchStartMissingConfirmResponse.StatusCode);
            Assert.Contains("confirmDispatchStart=true", dispatchStartMissingConfirm, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, subscriptionAdvanceMissingConfirmResponse.StatusCode);
            Assert.Contains("confirmSubscriptionAdvance=true", subscriptionAdvanceMissingConfirm, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, taskRunMissingConfirmResponse.StatusCode);
            Assert.Contains("confirmTaskRun=true", taskRunMissingConfirm, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, paidApiRunMissingConfirmResponse.StatusCode);
            Assert.Contains("confirmPaidApiRun=true", paidApiRunMissingConfirm, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, invalidGoalResponse.StatusCode);
            Assert.Contains("dashboard invalid request", invalidGoal, StringComparison.Ordinal);
            Assert.Contains("dashboard-content", StringComparison.Ordinal) || text.Contains("body{font-family", css, StringComparison.Ordinal);
            Assert.Contains("refreshContent", js, StringComparison.Ordinal);
            Assert.Contains("summarizeResponse", js, StringComparison.Ordinal);
            Assert.Contains("new EventSource(url)", js, StringComparison.Ordinal);
            Assert.Contains("addEventListener('timeline'", js, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);
            Assert.Contains("\"ProcessId\"", stopDashboard, StringComparison.Ordinal);
            Assert.Contains("\"ListeningPorts\"", stopDashboard, StringComparison.Ordinal);
            Assert.Contains("\"SiblingProcesses\"", stopDashboard, StringComparison.Ordinal);
            Assert.Contains("\"RestartCommand\"", stopDashboard, StringComparison.Ordinal);
            Assert.Contains("prototype-ui", stopDashboard, StringComparison.Ordinal);
            Assert.True(process.WaitForExit(5000));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
    [Xunit.Fact(DisplayName = "Dashboard_build_test_run_history_summarizes_recent_runner_logs")]
    public void DashboardBuildTestRunHistorySummarizesRecentRunnerLogs()
    {
        var root = Directory.CreateTempSubdirectory("dashboard-build-test-runs-").FullName;
        try
        {
            var runner = Path.Combine(root, "dashboard-build-test-cycle-20260604-161905.ps1");
            var output = Path.Combine(root, "dashboard-build-test-cycle-20260604-161905.out.log");
            var error = Path.Combine(root, "dashboard-build-test-cycle-20260604-161905.err.log");
            File.WriteAllText(runner, "runner");
            File.WriteAllText(
                output,
                string.Join(
                    Environment.NewLine,
                    "Build succeeded.",
                    "Passed!  - Failed:     0, Passed:    60, Skipped:     0, Total:    60",
                    "Passed!  - Failed:     0, Passed:    61, Skipped:     0, Total:    61",
                    "BuildSucceeded        : True",
                    "TestSucceeded         : True"));
            File.WriteAllText(error, string.Empty);

            var runs = DashboardEndpoints.BuildTestRuns(root);
            Assert.Equal(1, runs.Count);
            var run = runs[0];

            Assert.Equal("20260604-161905", run.Stamp);
            Assert.Equal("Passed", run.Status);
            Assert.True(run.BuildSucceeded);
            Assert.True(run.TestSucceeded);
            Assert.True(run.HasOutputLog);
            Assert.True(run.HasErrorLog);
            Assert.Contains("BuildSucceeded        : True", run.OutputPreview, StringComparison.Ordinal);
            Assert.Contains("TestSucceeded         : True", run.OutputPreview, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "Dashboard_restart_command_preserves_tenant_scope")]
    public void DashboardRestartCommandPreservesTenantScope()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root, tenantName: "customer_1");
        var args = new DashboardHostArgs("http://localhost:5087/", 5, OpenBrowser: false, CommandName: "hosted-dashboard");

        var command = DashboardHost.BuildDashboardRestartCommand(args, workspace);
        var argv = command[".\\mcg-orchestrator.cmd ".Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var selection = OrchestratorTenantSelection.FromArgs(argv, null);

        Assert.True(command.Contains("hosted-dashboard", StringComparison.Ordinal));
        Assert.True(command.StartsWith(".\\mcg-orchestrator.cmd --tenant customer_1 hosted-dashboard", StringComparison.Ordinal));
        Assert.Equal("customer_1", selection.TenantName);
        Assert.Equal("hosted-dashboard", selection.CommandArgs[0]);
    }

    [Xunit.Fact(DisplayName = "Hosted_dashboard_defaults_to_localhost_binding")]
    public void HostedDashboardDefaultsToLocalhostBinding()
    {
        var args = DashboardHost.ParseDashboardHostArgs(
            ["serve-dashboard", "--no-open"],
            "hosted-dashboard",
            defaultOpenBrowser: false);

        var uri = new Uri(args.UrlPrefix);

        Assert.Equal("localhost", uri.Host);
        Assert.Equal(0, DashboardHost.GetHostedUrlPrefixes(args).Count);
        Assert.Equal(args.UrlPrefix, DashboardHost.GetBrowserUrl(args));
    }

    [Xunit.Fact(DisplayName = "Hosted_dashboard_bare_port_stays_localhost_binding")]
    public void HostedDashboardBarePortStaysLocalhostBinding()
    {
        var args = DashboardHost.ParseDashboardHostArgs(
            ["serve-dashboard", "5099", "--no-open"],
            "hosted-dashboard",
            defaultOpenBrowser: false);

        Assert.Equal("http://localhost:5099/", args.UrlPrefix);
        Assert.Equal(0, DashboardHost.GetHostedUrlPrefixes(args).Count);
        Assert.Equal("http://localhost:5099/", DashboardHost.GetBrowserUrl(args));
    }

    [Xunit.Fact(DisplayName = "Hosted_dashboard_lan_flag_binds_all_interfaces")]
    public void HostedDashboardLanFlagBindsAllInterfaces()
    {
        var args = DashboardHost.ParseDashboardHostArgs(
            ["serve-dashboard", "--lan", "--no-open"],
            "hosted-dashboard",
            defaultOpenBrowser: false);

        var uri = new Uri(args.UrlPrefix);

        Assert.Equal("0.0.0.0", uri.Host);
        Assert.True(uri.Port is >= 5087 and <= 5186);
    }

    [Xunit.Fact(DisplayName = "Hosted_dashboard_explicit_lan_url_prints_public_url")]
    public void HostedDashboardExplicitLanUrlPrintsPublicUrl()
    {
        var args = DashboardHost.ParseDashboardHostArgs(
            ["serve-dashboard", "http://192.0.2.10:5099/", "--no-open"],
            "hosted-dashboard",
            defaultOpenBrowser: false);

        Assert.Equal("http://0.0.0.0:5099/", args.UrlPrefix);
        Assert.Equal("http://192.0.2.10:5099/", args.PublicUrlPrefix);
        Assert.Equal("http://192.0.2.10:5099/", DashboardHost.GetBrowserUrl(args));
        Assert.True(DashboardHost.GetHostedUrlPrefixes(args).SequenceEqual(["http://192.0.2.10:5099/"]));
        Assert.True(DashboardHost.GetHostedUrlPrefixes(args)
            .Select(DashboardHost.GetDashboardPageUrl)
            .SequenceEqual(["http://192.0.2.10:5099/dashboard"]));
    }

    private static async Task<string> GetRequiredStringAsync(HttpClient client, Uri uri)
    {
        using var response = await client.GetAsync(uri);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{uri} returned {(int)response.StatusCode}: {body}");
        }

        return body;
    }

    private static bool GoalResponseContainsDispatch(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Tasks")
            .EnumerateArray()
            .Any(task => task.TryGetProperty("Evidence", out var evidence) &&
                evidence.GetString() is "dispatch" or "running-process" or "completed-process");
    }

    private static bool GoalResponseContainsProcess(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Tasks")
            .EnumerateArray()
            .Any(task => task.TryGetProperty("Evidence", out var evidence) &&
                evidence.GetString() is "running-process" or "completed-process");
    }

    private static void AssertArchitectureArrayContains(JsonElement array, string expectedSubstring)
    {
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.True(array.EnumerateArray().Any(item =>
            item.ValueKind == JsonValueKind.String &&
            item.GetString()?.Contains(expectedSubstring, StringComparison.Ordinal) is true));
    }

    private static bool SourceSurveyContainsFilePathSegment(string json, string segment)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Files")
            .EnumerateArray()
            .Any(file => file.GetString()?.Contains(segment, StringComparison.OrdinalIgnoreCase) is true);
    }

}
