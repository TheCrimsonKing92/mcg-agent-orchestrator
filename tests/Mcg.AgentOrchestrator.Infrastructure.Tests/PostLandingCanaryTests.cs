using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

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
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/TestCoverageInvariant.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBaseBuildCache.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateHeartbeatArtifacts.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceAttemptArtifactCustody.cs",
            "src/Mcg.AgentOrchestrator.App/Orchestration/PostLandingCanaryCoordinator.cs",
            "src/Mcg.AgentOrchestrator.Core/Application/PostLandingCanaryTrigger.cs",
            "tests/canary-fixture/global.json",
            "config/acceptance-manifest.json"
        };

        Assert.Equal(
            [
                "acceptance-verifier",
                "test-impact-planner",
                "build-environment",
                "change-classifier",
                "gate-settings",
                "test-coverage-invariant",
                "base-build-cache",
                "gate-heartbeats",
                "attempt-artifact-custody",
                "post-landing-canary",
                "acceptance-manifest"
            ],
            AcceptanceEngineSurfaceRegistry.Surfaces.Select(surface => surface.Name));
        foreach (var path in triggering)
        {
            if (path.StartsWith("tests/canary-fixture/", StringComparison.Ordinal))
            {
                Assert.True(File.Exists(Path.Combine(FindRepoRoot(), path)), $"Missing watched fixture path: {path}");
            }

            var result = PostLandingCanaryTrigger.Evaluate([path]);
            Assert.True(result.ShouldRun, path);
            Assert.Equal(path, Assert.Single(result.TriggeringPaths));
        }

        Assert.False(PostLandingCanaryTrigger.Evaluate(
            ["src/Mcg.AgentOrchestrator.App/Dashboard/DashboardHost.cs"]).ShouldRun);
    }

    [Xunit.Fact(DisplayName = "Canary command uses injected verifier and rejects an accept verdict with an empty receipt")]
    public void CanaryCommandRequiresExecutedReceiptFromInjectedVerifier()
    {
        var acceptedVerifier = new FakeAcceptanceVerifier(new AcceptanceVerificationResult(
            Passed: true,
            Skipped: false,
            ExitCode: 0,
            OutputTail: null,
            TestResultPaths: ["injected.trx"]));
        var accepted = RunCanaryCommand(acceptedVerifier, executedTestCount: 3);

        Assert.Equal(0, accepted.ExitCode);
        Assert.True(accepted.Probe.Green);
        Assert.Null(accepted.Probe.FailureReason);
        Assert.Equal(3, accepted.Probe.ExecutedTestCount);
        Assert.Equal("fixture-root", acceptedVerifier.WorktreePath);
        Assert.Equal(
            ["tests/Mcg.AgentOrchestrator.Core.Tests/CanaryTests.cs"],
            acceptedVerifier.ChangedFiles);

        var emptyVerifier = new FakeAcceptanceVerifier(new AcceptanceVerificationResult(
            Passed: true,
            Skipped: false,
            ExitCode: 0,
            OutputTail: null,
            TestResultPaths: []));
        var empty = RunCanaryCommand(emptyVerifier, executedTestCount: 0);

        Assert.Equal(1, empty.ExitCode);
        Assert.False(empty.Probe.Green);
        Assert.Equal(PostLandingCanaryFailureReason.EmptyReceipt, empty.Probe.FailureReason);
        Assert.Equal(0, empty.Probe.ExecutedTestCount);
    }

    [Xunit.Fact(DisplayName = "Canary command classifies missing and environmental checks as infrastructure failures")]
    public void CanaryCommandClassifiesRejectAndInfrastructureFailures()
    {
        var noChecks = new AcceptanceVerificationResult(false, false, 1, "no checks");
        var interference = new AcceptanceVerificationResult(
            false,
            false,
            1,
            "slot interference",
            Checks:
            [
                new AcceptanceCheckResult(
                    "structural coverage",
                    false,
                    1,
                    "interference",
                    FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference)
            ]);
        var productReject = new AcceptanceVerificationResult(
            false,
            false,
            1,
            "real rejection",
            Checks: [new AcceptanceCheckResult("core tests", false, 1, "failed")]);

        Assert.Equal(
            PostLandingCanaryFailureReason.InfrastructureError,
            PostLandingCanaryCommand.ClassifyFailure(noChecks));
        Assert.Equal(
            PostLandingCanaryFailureReason.InfrastructureError,
            PostLandingCanaryCommand.ClassifyFailure(interference));
        Assert.Equal(
            PostLandingCanaryFailureReason.Reject,
            PostLandingCanaryCommand.ClassifyFailure(productReject));
    }

    [Xunit.Fact(DisplayName = "Portable file lease serializes canaries without named OS semaphores")]
    public async Task PortableFileLeaseSerializesConcurrentCanaries()
    {
        using var fixture = new CanaryTestFixture();
        using var first = await PostLandingCanarySerializationLease.AcquireAsync(
            fixture.DbPath,
            CancellationToken.None);
        var secondTask = PostLandingCanarySerializationLease.AcquireAsync(
            fixture.DbPath,
            CancellationToken.None);

        await Task.Delay(250);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.ReadAllText(Path.Combine(
                FindRepoRoot(),
                "src",
                "Mcg.AgentOrchestrator.App",
                "Orchestration",
                "PostLandingCanaryCoordinator.cs"))
            .Contains("new Semaphore(", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Landing launch persists Pending and returns without waiting for the canary")]
    public async Task LandingLaunchQueuesSynchronouslyAndRunsInBackground()
    {
        using var fixture = new CanaryTestFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return PostLandingCanaryOutcome.Passed(1, "background pass");
        });
        var (coordinator, circuit) = fixture.CreateCoordinator(runner);
        var clock = Stopwatch.StartNew();

        var run = coordinator.LaunchLandingAsync(new ConductorLandingReceipt(
            "goal-background",
            ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/TestCoverageInvariant.cs"],
            "sha-background"));
        clock.Stop();

        Assert.False(run.IsCompleted);
        Assert.Equal(AcceptanceEngineHealth.Pending, circuit.Read().Health);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        Assert.Equal(PostLandingCanaryDisposition.Passed, await run);
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

    [Xunit.Fact(DisplayName = "An earlier pass cannot mask a later unreceipted SHA and a failure latches until clear")]
    public async Task CircuitProjectsHealthPerShaAndLatchesFailure()
    {
        using var fixture = new CanaryTestFixture();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(async (request, cancellationToken) =>
        {
            if (request.LandingSha == "sha-one")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return PostLandingCanaryOutcome.Passed(1, "first passed");
            }

            if (request.LandingSha == "sha-two")
            {
                secondStarted.TrySetResult();
                await releaseSecond.Task.WaitAsync(cancellationToken);
                return PostLandingCanaryOutcome.Passed(1, "second passed");
            }

            return request.LandingSha == "sha-failed"
                ? PostLandingCanaryOutcome.Failed(PostLandingCanaryFailureReason.Reject, "latched failure")
                : PostLandingCanaryOutcome.Passed(1, "later pass");
        });
        var (coordinator, circuit) = fixture.CreateCoordinator(runner);

        var first = coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-one", ["engine/one"]),
            CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-two", ["engine/two"]),
            CancellationToken.None);
        Assert.Equal("sha-one", circuit.Read().LandingSha);

        releaseFirst.TrySetResult();
        await first;
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stillPending = circuit.Read();
        Assert.Equal(AcceptanceEngineHealth.Pending, stillPending.Health);
        Assert.Equal("sha-two", stillPending.LandingSha);

        releaseSecond.TrySetResult();
        await second;
        Assert.Equal(AcceptanceEngineHealth.Healthy, circuit.Read().Health);

        await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-failed", ["engine/fail"]),
            CancellationToken.None);
        await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-later-pass", ["engine/pass"]),
            CancellationToken.None);
        var unhealthy = circuit.Read();
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, unhealthy.Health);
        Assert.Equal("sha-failed", unhealthy.LandingSha);

        Assert.Equal(
            AcceptanceEngineHealth.Healthy,
            circuit.Clear("explicitly repaired after latched failure").Health);
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
        Assert.Contains("WaitForExitAsync(CancellationToken.None)", runnerSource, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Timeout receipt and FIFO lease wait for runner termination confirmation")]
    public async Task TimeoutKeepsSerializationUntilRunnerTerminationIsConfirmed()
    {
        using var fixture = new CanaryTestFixture(timeoutSeconds: 1);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminationConfirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(async (request, cancellationToken) =>
        {
            if (request.LandingSha == "sha-timeout-held")
            {
                firstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    await terminationConfirmed.Task;
                    throw;
                }
            }

            secondStarted.TrySetResult();
            return PostLandingCanaryOutcome.Passed(1, "second ran after termination");
        });
        var (coordinator, _) = fixture.CreateCoordinator(runner);

        var first = coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-timeout-held", ["engine/timeout"]),
            CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-after-timeout", ["engine/next"]),
            CancellationToken.None);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(first.IsCompleted);
        Assert.False(secondStarted.Task.IsCompleted);
        Assert.Null(await fixture.RawStore.ReadByEventIdAsync(
            PostLandingCanaryEventIds.Receipt("sha-timeout-held")));

        terminationConfirmed.TrySetResult();
        Assert.Equal(PostLandingCanaryDisposition.Failed, await first);
        Assert.Equal(PostLandingCanaryDisposition.Passed, await second);
        Assert.True(secondStarted.Task.IsCompleted);
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
        var goals = File.ReadAllText(Path.Combine(
            root, "src", "Mcg.AgentOrchestrator.App", "Cli", "CliCommandHandlers.Goals.cs"));

        Assert.Contains("_postLandingCanary.LaunchLandingAsync(receipt)", conductor, StringComparison.Ordinal);
        Assert.Contains("driver.LandingMutationBlocker", conductor, StringComparison.Ordinal);
        Assert.Contains("RunPostLandingCanary(context, goal, landingChangedFiles)", acceptance, StringComparison.Ordinal);
        Assert.Contains("BuildMutationBlockReason(context.Workspace)", acceptance, StringComparison.Ordinal);
        Assert.Contains("PostLandingCanaryFactory.HandleLandingAfterMainAdvanced(", workspace, StringComparison.Ordinal);
        Assert.Contains("BuildMutationBlockReason(context.Workspace)", workspace, StringComparison.Ordinal);
        Assert.Contains("var mergeChangedFiles = merge.ChangedFiles", workspace, StringComparison.Ordinal);
        Assert.Contains("mutationBlocker: () => PostLandingCanaryFactory.BuildMutationBlockReason", goals, StringComparison.Ordinal);
        Assert.Contains("var landChangedFiles = landResult.ChangedFiles", goals, StringComparison.Ordinal);
        Assert.Contains("PostLandingCanaryFactory.HandleLandingAfterMainAdvanced(", goals, StringComparison.Ordinal);
        Assert.Contains("PostLandingCanaryFactory.HandleLandingAfterMainAdvanced(", acceptance, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Post-main factory failure is nonthrowing and leaves acceptance unhealthy")]
    public void PostMainFactoryFailureSignalsEmergencyCircuit()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-canary-factory-failure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        Directory.CreateDirectory(workspace.RunEventStorePath);
        try
        {
            var disposition = PostLandingCanaryFactory.HandleLandingAfterMainAdvanced(
                workspace,
                new ConductorLandingReceipt(
                    "goal-factory-failure",
                    ["src/Mcg.AgentOrchestrator.App/Orchestration/PostLandingCanaryCoordinator.cs"],
                    "sha-factory-failure"),
                _ => throw new InvalidOperationException("progress sink failed"));

            Assert.Equal(PostLandingCanaryDisposition.Failed, disposition);
            Assert.Equal(
                AcceptanceEngineHealth.Unhealthy,
                PostLandingCanaryFactory.CreateCircuit(workspace).Read().Health);
            Assert.Contains(
                "acceptance engine circuit is Unhealthy",
                PostLandingCanaryFactory.BuildMutationBlockReason(workspace),
                StringComparison.Ordinal);
        }
        finally
        {
            PostLandingCanaryEmergencyCircuit.Clear(workspace.RunEventStorePath);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
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

    [Xunit.Fact(DisplayName = "Fresh workspace starts with a healthy initialized acceptance circuit")]
    public void FreshWorkspaceCircuitInitializesWithoutPriorRunEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-canary-fresh", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = PostLandingCanaryFactory
                .CreateCircuit(OrchestratorWorkspace.ForDirectory(root))
                .Read();

            Assert.Equal(AcceptanceEngineHealth.Healthy, snapshot.Health);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "Canary projection stays event-kind bounded with 418k unrelated run events")]
    public async Task ProjectionUsesTypedIndexedQueriesAtProductionScale()
    {
        using var fixture = new CanaryTestFixture();
        SeedUnrelatedRunEvents(fixture.DbPath, 418_000);
        var (coordinator, circuit) = fixture.CreateCoordinator(new FakeRunner(
            (_, _) => Task.FromResult(PostLandingCanaryOutcome.Passed(1, "bounded projection"))));

        await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-scale", ["engine/scale"]),
            CancellationToken.None);
        var clock = Stopwatch.StartNew();
        var snapshot = circuit.Read();
        clock.Stop();

        Assert.Equal(AcceptanceEngineHealth.Healthy, snapshot.Health);
        var typed = await fixture.RawStore.ReadByTypeSinceAsync(RunEventTypes.PostLandingCanary);
        Assert.Equal(3, typed.Count);

        using var connection = new SqliteConnection($"Data Source={fixture.DbPath};Pooling=False;");
        connection.Open();
        using var plan = connection.CreateCommand();
        plan.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT seq
            FROM run_events
            WHERE event_type = $event_type
              AND operation = $operation
            ORDER BY seq DESC
            LIMIT 1
            """;
        plan.Parameters.AddWithValue("$event_type", RunEventTypes.PostLandingCanary);
        plan.Parameters.AddWithValue("$operation", "clear");
        using var reader = plan.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
        {
            details.Add(reader.GetString(3));
        }

        Assert.Contains(
            details,
            detail => detail.Contains("ix_run_events_type_operation_seq", StringComparison.Ordinal));
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

    private static void SeedUnrelatedRunEvents(string dbPath, int count)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False;");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO run_events (
                event_id, occurred_at, event_type, goal_id, operation, status, detail, payload_json
            ) VALUES (
                $event_id, $occurred_at, $event_type, NULL, 'tick', 'Completed', NULL, NULL
            )
            """;
        var eventId = command.Parameters.Add("$event_id", SqliteType.Text);
        command.Parameters.AddWithValue("$occurred_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$event_type", RunEventTypes.ConductorTick);
        command.Prepare();
        for (var index = 0; index < count; index++)
        {
            eventId.Value = $"scale-{index:D6}";
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static (int ExitCode, PostLandingCanaryProbeResult Probe) RunCanaryCommand(
        FakeAcceptanceVerifier verifier,
        int executedTestCount)
    {
        var exitCode = -1;
        var output = CaptureConsole(() =>
            exitCode = PostLandingCanaryCommand.Run(
                [PostLandingCanaryCommand.SubcommandName, "fixture-root"],
                verifier,
                _ => executedTestCount));
        var resultLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(line => line.StartsWith(PostLandingCanaryCommand.ResultPrefix, StringComparison.Ordinal));
        var probe = JsonSerializer.Deserialize<PostLandingCanaryProbeResult>(
            resultLine[PostLandingCanaryCommand.ResultPrefix.Length..],
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return (exitCode, Assert.IsType<PostLandingCanaryProbeResult>(probe));
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

    private sealed class FakeAcceptanceVerifier(AcceptanceVerificationResult result)
        : IGoalAcceptanceVerifier
    {
        internal string? WorktreePath { get; private set; }
        internal IReadOnlyList<string>? ChangedFiles { get; private set; }

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default)
        {
            WorktreePath = worktreePath;
            ChangedFiles = changedFiles;
            return Task.FromResult(result);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
        internal string DbPath => OrchestratorWorkspace.ForDirectory(_root).RunEventStorePath;

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
