using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

public sealed class DashboardHostTests
{
    [Xunit.Fact(DisplayName = "Prototype_dashboard_serves_health_and_goal_json_over_kestrel")]
    public async Task PrototypeDashboardServesHealthAndGoalJsonOverKestrel()
{
    var root = CreateTempDirectory();
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
                "{\"name\":\"codex-cli\",\"commandTemplate\":\"codex exec --skip-git-repo-check --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})\"}",
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
        using var subscriptionAdvanceMissingConfirmResponse = await client.PostAsync(
            new Uri(new Uri(url), $"api/goals/{manualGoalId}/advance-subscription"),
            new StringContent(string.Empty));
        var subscriptionAdvanceMissingConfirm = await subscriptionAdvanceMissingConfirmResponse.Content.ReadAsStringAsync();
        using var taskRunMissingConfirmResponse = await client.PostAsync(
            new Uri(new Uri(url), $"api/goals/{manualGoalId}/tasks/1/run"),
            new StringContent(string.Empty));
        var taskRunMissingConfirm = await taskRunMissingConfirmResponse.Content.ReadAsStringAsync();
        using var simpleGoalResponse = await client.PostAsync(
            new Uri(new Uri(url), "api/goals"),
            new StringContent(
                "{\"objective\":\"Simple hosted dashboard goal\",\"workflow\":\"simple\",\"autoHandoff\":true,\"confirmAutoHandoff\":true}",
                System.Text.Encoding.UTF8,
                "application/json"));
        var simpleGoal = await simpleGoalResponse.Content.ReadAsStringAsync();
        using var defaultManualGoalResponse = await client.PostAsync(
            new Uri(new Uri(url), "api/goals"),
            new StringContent(
                "{\"objective\":\"Default manual hosted dashboard goal\",\"workflow\":\"simple\"}",
                System.Text.Encoding.UTF8,
                "application/json"));
        var defaultManualGoal = await defaultManualGoalResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, simpleGoalResponse.StatusCode);
        var simpleGoalId = JsonDocument.Parse(simpleGoal).RootElement.GetProperty("Goal").GetProperty("Id").GetString()!;
        var simpleGoalDetail = await client.GetStringAsync(new Uri(new Uri(url), $"api/goals/{simpleGoalId}"));
        var simpleGoalWorkSummary = await client.GetStringAsync(new Uri(new Uri(url), $"api/goals/{simpleGoalId}/work-summary"));
        using var cssResponse = await client.GetAsync(new Uri(new Uri(url), "assets/dashboard.css"));
        var css = await cssResponse.Content.ReadAsStringAsync();
        var js = await client.GetStringAsync(new Uri(new Uri(url), "assets/dashboard.js"));
        using var missingTaskResponse = await client.GetAsync(new Uri(new Uri(url), "api/task/999"));
        var missingTask = await missingTaskResponse.Content.ReadAsStringAsync();
        var sourceSurvey = await client.GetStringAsync(new Uri(new Uri(url), "api/source-survey?max=25"));
        var continuations = await client.GetStringAsync(new Uri(new Uri(url), "api/continuations"));
        var continuationSummary = await client.GetStringAsync(new Uri(new Uri(url), "api/continuations/summary"));
        var processDiagnostic = await client.GetStringAsync(new Uri(new Uri(url), "api/system/processes"));
        var cleanupPlan = await client.GetStringAsync(new Uri(new Uri(url), "api/system/build-test-cleanup"));
        var buildTestRuns = await client.GetStringAsync(new Uri(new Uri(url), "api/system/build-test-runs"));
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

        Assert.Contains(goals, text => text.Contains("Prototype: explore the agent orchestrator UI", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, workerProfileResponse.StatusCode);
        Assert.Contains(simpleGoal, text => text.Contains("\"TotalTasks\": 1", StringComparison.Ordinal));
        Assert.Contains(simpleGoal, text => text.Contains("\"Role\": \"Developer\"", StringComparison.Ordinal));
        Assert.Contains(simpleGoalDetail, text => text.Contains("\"VerificationSatisfied\"", StringComparison.Ordinal));
        Assert.Contains(simpleGoalDetail, text => text.Contains(simpleGoalId, StringComparison.Ordinal));
        using (var workSummaryDocument = JsonDocument.Parse(simpleGoalWorkSummary))
        {
            var summary = workSummaryDocument.RootElement;
            Assert.Equal(simpleGoalId, summary.GetProperty("GoalId").GetString());
            Assert.Equal(JsonValueKind.Array, summary.GetProperty("Tasks").ValueKind);
            Assert.True(summary.GetProperty("TotalTasks").GetInt32() > 0);
            Assert.True(summary.GetProperty("PendingHumanInputCount").GetInt32() >= 0);
            Assert.True(summary.TryGetProperty("NextAction", out _));
            Assert.True(summary.GetProperty("Tasks").EnumerateArray().Any(task =>
                task.TryGetProperty("Evidence", out var evidence) &&
                !string.IsNullOrWhiteSpace(evidence.GetString())));
            Assert.True(summary.GetProperty("Tasks").EnumerateArray().Any(task =>
                task.TryGetProperty("LastDispatch", out var dispatch) &&
                dispatch.ValueKind == JsonValueKind.Object));
        }
        Assert.Equal(HttpStatusCode.Created, manualGoalResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, defaultManualGoalResponse.StatusCode);
        Assert.Contains(simpleGoal, text => text.Contains("\"Status\": \"Running\"", StringComparison.Ordinal));
        Assert.True(GoalResponseContainsDispatch(simpleGoal));
        Assert.True(GoalResponseContainsProcess(simpleGoal));
        Assert.Contains(manualGoal, text => text.Contains("\"Status\": \"Assigned\"", StringComparison.Ordinal));
        Assert.False(GoalResponseContainsDispatch(manualGoal));
        Assert.False(GoalResponseContainsProcess(manualGoal));
        Assert.Contains(defaultManualGoal, text => text.Contains("\"Status\": \"Assigned\"", StringComparison.Ordinal));
        Assert.False(GoalResponseContainsDispatch(defaultManualGoal));
        Assert.False(GoalResponseContainsProcess(defaultManualGoal));
        Assert.Equal("no-store", cssResponse.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, missingTaskResponse.StatusCode);
        Assert.Contains(missingTask, text => text.Contains("dashboard not found", StringComparison.Ordinal));
        Assert.Contains(sourceSurvey, text => text.Contains("\"MaxFiles\": 25", StringComparison.Ordinal));
        Assert.Contains(sourceSurvey, text => text.Contains("\"RecommendedCommand\"", StringComparison.Ordinal));
        Assert.Contains(sourceSurvey, text => text.Contains("!**/.scratch/**", StringComparison.Ordinal));
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
        using (var cleanupDocument = JsonDocument.Parse(cleanupPlan))
        {
            Assert.True(cleanupDocument.RootElement.GetProperty("CurrentProcessId").GetInt32() > 0);
            Assert.Equal("/api/system/stop-dashboard", cleanupDocument.RootElement.GetProperty("StopCurrentUrl").GetString());
            Assert.Equal("/api/system/run-build-test-cycle", cleanupDocument.RootElement.GetProperty("RunBuildTestCycleUrl").GetString());
            Assert.Contains(cleanupPlan, text => text.Contains("dotnet build Mcg.AgentOrchestrator.sln --no-restore", StringComparison.Ordinal));
            Assert.Contains(cleanupPlan, text => text.Contains("dotnet test Mcg.AgentOrchestrator.sln --no-build", StringComparison.Ordinal));
            Assert.Contains(cleanupPlan, text => text.Contains("Get-Process Mcg.AgentOrchestrator.App -ErrorAction SilentlyContinue", StringComparison.Ordinal));
            Assert.Contains(cleanupPlan, text => text.Contains("Invoke-DashboardBuildTestCycle.ps1", StringComparison.Ordinal));
        }
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(buildTestRuns).RootElement.ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, broadSmokeGetResponse.StatusCode);
        Assert.Contains(broadSmokeGet, text => text.Contains("broad paid smoke tests are deliberate", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, broadSmokePostResponse.StatusCode);
        Assert.Contains(broadSmokePost, text => text.Contains("confirmAll=true", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, batchStartMissingConfirmResponse.StatusCode);
        Assert.Contains(batchStartMissingConfirm, text => text.Contains("confirmBatchStart=true", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, subscriptionAdvanceMissingConfirmResponse.StatusCode);
        Assert.Contains(subscriptionAdvanceMissingConfirm, text => text.Contains("confirmSubscriptionAdvance=true", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, taskRunMissingConfirmResponse.StatusCode);
        Assert.Contains(taskRunMissingConfirm, text => text.Contains("confirmTaskRun=true", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, invalidGoalResponse.StatusCode);
        Assert.Contains(invalidGoal, text => text.Contains("dashboard invalid request", StringComparison.Ordinal));
        Assert.Contains(css, text => text.Contains("dashboard-content", StringComparison.Ordinal) || text.Contains("body{font-family", StringComparison.Ordinal));
        Assert.Contains(js, text => text.Contains("refreshContent", StringComparison.Ordinal));
        Assert.Contains(js, text => text.Contains("summarizeResponse", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);
        Assert.Contains(stopDashboard, text => text.Contains("\"ProcessId\"", StringComparison.Ordinal));
        Assert.Contains(stopDashboard, text => text.Contains("\"ListeningPorts\"", StringComparison.Ordinal));
        Assert.Contains(stopDashboard, text => text.Contains("\"SiblingProcesses\"", StringComparison.Ordinal));
        Assert.Contains(stopDashboard, text => text.Contains("\"RestartCommand\"", StringComparison.Ordinal));
        Assert.Contains(stopDashboard, text => text.Contains("prototype-ui", StringComparison.Ordinal));
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
            Assert.Contains(run.OutputPreview, text => text.Contains("BuildSucceeded        : True", StringComparison.Ordinal));
            Assert.Contains(run.OutputPreview, text => text.Contains("TestSucceeded         : True", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static bool SourceSurveyContainsFilePathSegment(string json, string segment)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Files")
            .EnumerateArray()
            .Any(file => file.GetString()?.Contains(segment, StringComparison.OrdinalIgnoreCase) is true);
    }
}

