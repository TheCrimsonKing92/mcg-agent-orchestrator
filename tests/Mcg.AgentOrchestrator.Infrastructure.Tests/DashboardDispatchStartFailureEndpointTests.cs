using System.Text.Json;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

[Xunit.Collection("EnvMutation")]
public sealed class DashboardDispatchStartFailureEndpointTests
{
    [Xunit.Fact]
    public async Task TaskStart_RegistrationFailure_Returns503AndCommitsFailedState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var previousDisableStart = Environment.GetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable);
        try
        {
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, null);
            var fixture = await CreateStartReadyFixtureAsync(root);
            ConfigureCorruptWorkerRegistry(root);

            await using var app = BuildDashboardApp(fixture);
            var response = await InvokeMappedEndpointAsync(
                app,
                "/api/goals/{goalId}/tasks/{taskId}/{operation}",
                $"/api/goals/{fixture.Goal.Id.Value}/tasks/1/start",
                "?confirmDispatchStart=true",
                new RouteValueDictionary
                {
                    ["goalId"] = fixture.Goal.Id.Value,
                    ["taskId"] = "1",
                    ["operation"] = "start"
                });

            Assert.True(
                response.StatusCode == StatusCodes.Status503ServiceUnavailable,
                $"Expected 503 but received {response.StatusCode}: {response.Body}");
            using (var responseJson = JsonDocument.Parse(response.Body))
            {
                var failure = responseJson.RootElement;
                Assert.Equal(fixture.Goal.Id.Value, failure.GetProperty("GoalId").GetString());
                Assert.Equal(fixture.Task.Id.Value, failure.GetProperty("TaskId").GetString());
                Assert.Contains(
                    "worker-process-registration-failed",
                    failure.GetProperty("Reason").GetString(),
                    StringComparison.Ordinal);
            }

            AssertPersistedRegistrationFailure(await fixture.Repository.LoadAsync(), fixture.Goal.Id, fixture.Task.Id);
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, previousDisableStart);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public async Task SubscriptionAdvance_RegistrationFailure_ReturnsNotExecutedAndCommits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var previousDisableStart = Environment.GetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable);
        try
        {
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, null);
            var fixture = await CreateStartReadyFixtureAsync(root);
            ConfigureCorruptWorkerRegistry(root);

            await using var app = BuildDashboardApp(fixture);
            var response = await InvokeMappedEndpointAsync(
                app,
                "/api/goals/{goalId}/advance-subscription",
                $"/api/goals/{fixture.Goal.Id.Value}/advance-subscription",
                "?confirmSubscriptionAdvance=true",
                new RouteValueDictionary
                {
                    ["goalId"] = fixture.Goal.Id.Value
                });

            Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
            using (var responseJson = JsonDocument.Parse(response.Body))
            {
                var advance = responseJson.RootElement;
                Assert.False(advance.GetProperty("Executed").GetBoolean());
                Assert.True(advance.GetProperty("StateChanged").GetBoolean(), response.Body);
                Assert.Equal(
                    nameof(NextActionAutomationKind.StartRecordedDispatch),
                    advance.GetProperty("AutomationKind").GetString());
                Assert.Contains(
                    "worker-process-registration-failed",
                    advance.GetProperty("Result").GetProperty("Reason").GetString(),
                    StringComparison.Ordinal);
            }

            AssertPersistedRegistrationFailure(await fixture.Repository.LoadAsync(), fixture.Goal.Id, fixture.Task.Id);
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, previousDisableStart);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public async Task SubscriptionContinuation_RegistrationFailure_CommitsFailedState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var previousDisableStart = Environment.GetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable);
        try
        {
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, null);
            var fixture = await CreateStartReadyFixtureAsync(root);
            ConfigureCorruptWorkerRegistry(root);
            using var service = new DashboardContinuationService(TimeSpan.FromMilliseconds(10), maxIterations: 2);
            var services = new DashboardEndpointServices(
                new DashboardStateService(fixture.Repository),
                fixture.Workspace,
                new InMemoryModelProviderRegistry([]),
                new DashboardHostArgs("http://localhost:5087/", null, false, "prototype-ui"),
                new TestHostLifetime(),
                service);

            var started = service.StartSubscriptionWatch(services, fixture.Goal.Id.Value);
            Assert.True(started.IsRunning);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline && service.GetStatuses().Single().IsRunning)
            {
                await Task.Delay(25);
            }

            var status = service.GetStatuses().Single();
            Assert.False(status.IsRunning);
            Assert.True(status.IterationCount > 0);
            Assert.Contains("worker-process-registration-failed", status.StopReason, StringComparison.Ordinal);
            AssertPersistedRegistrationFailure(await fixture.Repository.LoadAsync(), fixture.Goal.Id, fixture.Task.Id);
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, previousDisableStart);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public async Task SubscriptionAdvanceUntilBlocked_RegistrationFailureAfterDispatch_ReturnsConflictAndCommits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var previousDisableStart = Environment.GetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable);
        try
        {
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, null);
            var fixture = await CreateStartReadyFixtureAsync(root, recordDispatch: false);
            ConfigureCorruptWorkerRegistry(root);

            await using var app = BuildDashboardApp(fixture);
            var response = await InvokeMappedEndpointAsync(
                app,
                "/api/goals/{goalId}/advance-subscription-until-blocked",
                $"/api/goals/{fixture.Goal.Id.Value}/advance-subscription-until-blocked",
                "?confirmSubscriptionAdvance=true",
                new RouteValueDictionary
                {
                    ["goalId"] = fixture.Goal.Id.Value
                });

            Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
            using (var responseJson = JsonDocument.Parse(response.Body))
            {
                var advance = responseJson.RootElement;
                Assert.True(advance.GetProperty("Executed").GetBoolean(), response.Body);
                Assert.Equal(1, advance.GetProperty("StepCount").GetInt32());
                Assert.True(advance.GetProperty("StateChanged").GetBoolean(), response.Body);
                Assert.Contains(
                    "worker-process-registration-failed",
                    advance.GetProperty("Failure").GetProperty("Reason").GetString(),
                    StringComparison.Ordinal);
            }

            AssertPersistedRegistrationFailure(await fixture.Repository.LoadAsync(), fixture.Goal.Id, fixture.Task.Id);
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, previousDisableStart);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<StartReadyFixture> CreateStartReadyFixtureAsync(string root, bool recordDispatch = true)
    {
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var agent = new AgentDefinition(
            new AgentId("registration-failure-planner"),
            "Registration failure planner",
            AgentRole.Planner,
            new ModelProfile("Test", "test-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("local"));
        var agents = new AgentCatalog([agent]);
        var profiles = new WorkerProfileCatalog(
        [
            new WorkerProfile("local", "Write-Output 'safe local fixture'; Write-Output {promptPath}")
        ]);
        AgentCatalogStore.Save(workspace.AgentCatalogPath, agents);
        WorkerProfileStore.Save(workspace.WorkerProfilePath, profiles);

        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(
            TaskId.New(),
            "Plan the deterministic registration failure fixture.",
            AgentRole.Planner,
            "Record explicit verification.");
        var goal = kernel.CreateGoal("Surface dispatch registration failure through dashboard endpoints", [task]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Dashboard registration failure fixture is already refined.",
            ["Registration failure is returned and persisted."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, agents.Agents);
        if (recordDispatch)
        {
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord(
                    "local",
                    "Write-Output 'safe local fixture'",
                    workspace.ExecutionDirectory,
                    DateTimeOffset.UtcNow));
        }

        var nextAction = Assert.Single(kernel.BuildNextActions(goal.Id).Items);
        Assert.Equal(
            recordDispatch ? NextActionKind.ExecuteRecordedDispatch : NextActionKind.RunAssignedTask,
            nextAction.Kind);
        Assert.Equal(
            recordDispatch ? NextActionAutomationKind.StartRecordedDispatch : NextActionAutomationKind.RunAssignedTask,
            NextActionAutomationPolicy.Build(nextAction).Kind);

        await repository.SaveAsync(kernel);
        return new StartReadyFixture(workspace, repository, agents, goal, task);
    }

    private static WebApplication BuildDashboardApp(StartReadyFixture fixture)
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        app.MapDashboardEndpoints(
            fixture.Repository,
            fixture.Workspace,
            new InMemoryModelProviderRegistry([]),
            new DashboardHostArgs("http://localhost:5087/", null, false, "prototype-ui"),
            fixture.Agents);
        return app;
    }

    private static async Task<EndpointResponse> InvokeMappedEndpointAsync(
        WebApplication app,
        string routePattern,
        string requestPath,
        string query,
        RouteValueDictionary routeValues)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                string.Equals(candidate.RoutePattern.RawText, routePattern, StringComparison.Ordinal) &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post) is true);
        Assert.NotNull(endpoint.RequestDelegate);

        var context = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = requestPath;
        context.Request.QueryString = new QueryString(query);
        context.Request.RouteValues = routeValues;
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await endpoint.RequestDelegate(context);
        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        return new EndpointResponse(context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static void ConfigureCorruptWorkerRegistry(string root)
    {
        var corruptRegistryPath = Path.Combine(root, "corrupt-worker-registry.db");
        File.WriteAllBytes(corruptRegistryPath, []);
        WorkerProcessJobs.ConfigureRegistry(corruptRegistryPath);
    }

    private static void AssertPersistedRegistrationFailure(
        AgentOrchestratorKernel restored,
        GoalId goalId,
        TaskId taskId)
    {
        var goal = restored.GetGoal(goalId);
        var task = restored.GetTask(goalId, taskId);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Null(task.LastProcess);
        Assert.Contains(goal.Timeline, item =>
            item.TaskId == taskId &&
            item.Kind == ProgressKind.TaskFailed &&
            item.Message.Contains("worker-process-registration-failed", StringComparison.Ordinal));
    }

    private sealed record StartReadyFixture(
        OrchestratorWorkspace Workspace,
        SqliteOrchestratorStateRepository Repository,
        AgentCatalog Agents,
        Goal Goal,
        TaskSpec Task);

    private sealed record EndpointResponse(int StatusCode, string Body);

    private sealed class TestHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
