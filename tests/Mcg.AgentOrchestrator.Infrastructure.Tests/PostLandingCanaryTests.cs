using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PostLandingCanaryTests : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "Post-landing canary classifier covers every engine surface and ignores unrelated paths")]
    public void ClassifierCoversEveryEngineSurface()
    {
        var triggering = new[]
        {
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs",
            "src/Mcg.AgentOrchestrator.Core/Application/RepositoryTestImpactPlanner.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs",
            "src/Mcg.AgentOrchestrator.Core/Application/RepositoryChangeClassifier.cs",
            "config/acceptance-manifest.json"
        };

        foreach (var path in triggering)
        {
            var result = PostLandingCanaryTrigger.Evaluate([path]);
            Assert.True(result.ShouldRun, path);
            Assert.Equal(path, Assert.Single(result.TriggeringPaths));
        }

        Assert.False(PostLandingCanaryTrigger.Evaluate(
            ["src/Mcg.AgentOrchestrator.App/Dashboard/DashboardHost.cs"]).ShouldRun);
    }

    [Xunit.Fact(DisplayName = "Canary persists one typed goal-less receipt per landing SHA and deduplicates reruns")]
    public async Task PersistsTypedGoalLessReceiptAndDeduplicates()
    {
        using var fixture = new CanaryTestFixture();
        var calls = 0;
        var runner = new FakeRunner((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(PostLandingCanaryOutcome.Passed(1, "known-green receipt"));
        });
        var (coordinator, circuit) = fixture.CreateCoordinator(runner);
        var landing = new ConductorLandingReceipt(
            "goal-not-persisted",
            ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"],
            "ABC123");

        Assert.Equal(PostLandingCanaryDisposition.Passed, coordinator.HandleLanding(landing));
        Assert.Equal(PostLandingCanaryDisposition.AlreadyCompleted, coordinator.HandleLanding(landing));
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(AcceptanceEngineHealth.Healthy, circuit.Read().Health);

        var records = (await fixture.RawStore.ReadSinceAsync())
            .Where(record => record.EventType == RunEventTypes.PostLandingCanary)
            .ToArray();
        Assert.All(records, record => Assert.Null(record.GoalId));
        var receipt = Assert.Single(records.Where(record => record.Operation == "receipt"));
        Assert.Equal("Passed", receipt.Status);
        using var payload = JsonDocument.Parse(receipt.PayloadJson!);
        Assert.Equal("canary", payload.RootElement.GetProperty("tag").GetString());
        Assert.Equal("ABC123", payload.RootElement.GetProperty("landingSha").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("executedTestCount").GetInt32());
    }

    [Xunit.Fact(DisplayName = "Queued landing SHAs run FIFO and each receive exactly one receipt")]
    public async Task QueuedLandingShasRunFifoWithOneReceiptEach()
    {
        using var fixture = new CanaryTestFixture();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        var runner = new FakeRunner(async (request, cancellationToken) =>
        {
            calls.Enqueue(request.LandingSha);
            if (request.LandingSha == "sha-one")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return PostLandingCanaryOutcome.Passed(1, request.LandingSha);
        });
        var (coordinator, _) = fixture.CreateCoordinator(runner);

        var first = coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-one", ["engine/one"]),
            CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-two", ["engine/two"]),
            CancellationToken.None);

        await Task.Delay(150);
        Assert.Equal(["sha-one"], calls.ToArray());
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["sha-one", "sha-two"], calls.ToArray());
        var receipts = (await fixture.RawStore.ReadSinceAsync())
            .Where(record =>
                record.EventType == RunEventTypes.PostLandingCanary &&
                record.Operation == "receipt")
            .ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.Equal(2, receipts.Select(record => record.EventId).Distinct().Count());
    }

    [Xunit.Fact(DisplayName = "Pending and failed canaries hold acceptance only; explicit clear restores it")]
    public async Task CircuitHoldsAcceptanceOnlyAndRequiresExplicitClearAfterFailure()
    {
        using var fixture = new CanaryTestFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return PostLandingCanaryOutcome.Failed(
                PostLandingCanaryFailureReason.Reject,
                "forced red");
        });
        var (coordinator, circuit) = fixture.CreateCoordinator(runner);
        var run = coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-red", ["engine/red"]),
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pending = circuit.Read();
        Assert.Equal(AcceptanceEngineHealth.Pending, pending.Health);
        Assert.True(ConductorBatchLoop.IsAcceptanceEngineCircuitHoldRequired(GoalStatus.Verified, pending));
        Assert.False(ConductorBatchLoop.IsAcceptanceEngineCircuitHoldRequired(GoalStatus.Active, pending));

        release.TrySetResult();
        Assert.Equal(PostLandingCanaryDisposition.Failed, await run);
        var failed = circuit.Read();
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, failed.Health);
        Assert.True(ConductorBatchLoop.IsAcceptanceEngineCircuitHoldRequired(GoalStatus.Verified, failed));
        Assert.False(ConductorBatchLoop.IsAcceptanceEngineCircuitHoldRequired(GoalStatus.Active, failed));

        var cleared = circuit.Clear("engine repaired and independently verified");
        Assert.Equal(AcceptanceEngineHealth.Healthy, cleared.Health);
        Assert.False(ConductorBatchLoop.IsAcceptanceEngineCircuitHoldRequired(GoalStatus.Verified, cleared));
    }

    [Xunit.Fact(DisplayName = "Hard timeout cancels the runner, records timeout failure, and keeps process-tree kill path")]
    public async Task HardTimeoutCancelsRunnerAndRecordsFailure()
    {
        using var fixture = new CanaryTestFixture(timeoutSeconds: 1);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The timeout did not cancel the runner.");
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        });
        var (coordinator, circuit) = fixture.CreateCoordinator(runner);

        var disposition = await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-timeout", ["engine/timeout"]),
            CancellationToken.None);

        Assert.Equal(PostLandingCanaryDisposition.Failed, disposition);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var failed = circuit.Read();
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, failed.Health);
        Assert.Equal("timeout", failed.FailureReason);

        var runnerSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "Mcg.AgentOrchestrator.App",
            "Orchestration",
            "PostLandingCanaryRunner.cs"));
        Assert.Contains("WorkerProcessJobs.TryKillOrFallback(process.Id)", runnerSource, StringComparison.Ordinal);
        Assert.Contains("process.Kill(entireProcessTree: true)", runnerSource, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Autonomous, acceptance merge, and manual workspace merge paths invoke the canary hook")]
    public void EveryLandingPathInvokesCanaryHook()
    {
        var root = FindRepoRoot();
        var conductor = File.ReadAllText(Path.Combine(
            root, "src", "Mcg.AgentOrchestrator.App", "Orchestration", "ConductorBatchLoop.cs"));
        var acceptance = File.ReadAllText(Path.Combine(
            root, "src", "Mcg.AgentOrchestrator.App", "Cli", "CliCommandHandlers.Goals.Acceptance.cs"));
        var workspace = File.ReadAllText(Path.Combine(
            root, "src", "Mcg.AgentOrchestrator.App", "Cli", "CliCommandHandlers.Goals.Workspace.cs"));

        Assert.Contains("_postLandingCanary?.HandleLanding(receipt)", conductor, StringComparison.Ordinal);
        Assert.Contains("RunPostLandingCanary(context, goal, landingChangedFiles)", acceptance, StringComparison.Ordinal);
        Assert.Contains("PostLandingCanaryFactory.CreateDefault(context.Workspace)", workspace, StringComparison.Ordinal);
        Assert.Contains(".HandleLanding(new ConductorLandingReceipt(", workspace, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Acceptance-engine CLI reports and explicitly clears the typed circuit")]
    public async Task AcceptanceEngineCliReportsAndClearsCircuit()
    {
        using var fixture = new CanaryTestFixture();
        var runner = new FakeRunner((_, _) => Task.FromResult(
            PostLandingCanaryOutcome.Failed(
                PostLandingCanaryFailureReason.InfrastructureError,
                "forced CLI circuit")));
        var (coordinator, _) = fixture.CreateCoordinator(runner);
        await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-cli", ["engine/cli"]),
            CancellationToken.None);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var kernel = new AgentOrchestratorKernel();

        var status = ExecuteCliAndCapture(["acceptance-engine", "status"], kernel, workspace);
        Assert.Contains("Acceptance engine: Unhealthy", status, StringComparison.Ordinal);
        Assert.Contains("landing=sha-cli", status, StringComparison.Ordinal);

        var clear = ExecuteCliAndCapture(
            ["acceptance-engine", "clear", "engine", "repaired"],
            kernel,
            workspace);
        Assert.Contains("Acceptance engine circuit cleared", clear, StringComparison.Ordinal);
        Assert.Equal(
            AcceptanceEngineHealth.Healthy,
            PostLandingCanaryFactory.CreateCircuit(workspace).Read().Health);
    }

    [Xunit.Fact(DisplayName = "Known-green fixture runs through the freshly built public acceptance entrypoint without dirtying main")]
    public async Task KnownGreenFixtureRunsThroughFreshBinaryWithoutDirtyingRepository()
    {
        var root = FindRepoRoot();
        var landingSha = GoalAcceptanceVerifier.ResolveGitText(root, "rev-parse", "HEAD")?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(landingSha));
        var statusBefore = GoalAcceptanceVerifier.ResolveGitText(root, "status", "--porcelain");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var buildCacheRoot = Path.Combine(
            Path.GetTempPath(),
            "mcg-canary-binary-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var outcome = await new PostLandingCanaryRunner(
                    root,
                    buildCacheRoot: buildCacheRoot)
                .RunAsync(
                    new PostLandingCanaryRequest(landingSha!, ["integration-test"]),
                    timeout.Token);

            Assert.True(outcome.Green, outcome.Detail);
            Assert.True(outcome.ExecutedTestCount > 0);
            Assert.Equal(
                statusBefore,
                GoalAcceptanceVerifier.ResolveGitText(root, "status", "--porcelain"));
        }
        finally
        {
            try { Directory.Delete(buildCacheRoot, recursive: true); } catch { }
        }
    }

    private static string FindRepoRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath) ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class FakeRunner(
        Func<PostLandingCanaryRequest, CancellationToken, Task<PostLandingCanaryOutcome>> run)
        : IPostLandingCanaryRunner
    {
        public Task<PostLandingCanaryOutcome> RunAsync(
            PostLandingCanaryRequest request,
            CancellationToken cancellationToken) =>
            run(request, cancellationToken);
    }

    private sealed class CanaryTestFixture : IDisposable
    {
        private readonly string _root;
        private readonly int _timeoutSeconds;
        private readonly PostLandingCanaryEventStore _events;

        internal CanaryTestFixture(int timeoutSeconds = 10)
        {
            _timeoutSeconds = timeoutSeconds;
            _root = Path.Combine(Path.GetTempPath(), "mcg-canary-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var dbPath = OrchestratorWorkspace.ForDirectory(_root).RunEventStorePath;
            RawStore = new SqliteRunEventStore(dbPath);
            _events = new PostLandingCanaryEventStore(RawStore, dbPath);
        }

        internal SqliteRunEventStore RawStore { get; }
        internal string Root => _root;

        internal (PostLandingCanaryCoordinator Coordinator, AcceptanceEngineCircuitBreaker Circuit)
            CreateCoordinator(IPostLandingCanaryRunner runner)
        {
            var circuit = new AcceptanceEngineCircuitBreaker(_events);
            return (
                new PostLandingCanaryCoordinator(
                    new PostLandingCanaryConfiguration(true, _timeoutSeconds, []),
                    runner,
                    _events,
                    circuit),
                circuit);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
