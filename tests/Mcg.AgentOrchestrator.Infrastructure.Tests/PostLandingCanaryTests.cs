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
            PostLandingCanaryCommand.ClassifyFailure(noChecks, executedTestCount: 0));
        Assert.Equal(
            PostLandingCanaryFailureReason.InfrastructureError,
            PostLandingCanaryCommand.ClassifyFailure(interference, executedTestCount: 0));
        Assert.Equal(
            PostLandingCanaryFailureReason.Reject,
            PostLandingCanaryCommand.ClassifyFailure(productReject, executedTestCount: 1));
        Assert.Equal(
            PostLandingCanaryFailureReason.Reject,
            PostLandingCanaryCommand.ClassifyFailure(productReject, executedTestCount: 0));
    }

    [Xunit.Fact(DisplayName = "Canary fault classifier uses positive evidence for known and unexpected dispositions")]
    public void FaultClassifierSeparatesKnownAndUnexpectedDispositions()
    {
        var busy = new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy(
            "run-canary-classifier",
            [new DotnetBuildStableSlotWait(0, 100), new DotnetBuildStableSlotWait(1, 101)]));

        Assert.Equal(
            PostLandingCanaryFaultDisposition.ResourceBusy,
            PostLandingCanaryFailureClassifier.Classify(busy));
        Assert.Equal(
            PostLandingCanaryFaultDisposition.EnvironmentFault,
            PostLandingCanaryFailureClassifier.Classify(new IOException("environment unavailable")));
        Assert.Equal(
            PostLandingCanaryFaultDisposition.PreconditionFailure,
            PostLandingCanaryFailureClassifier.Classify(
                new PostLandingCanaryPreconditionException("operator action required")));
        Assert.Equal(
            PostLandingCanaryFaultDisposition.UnexpectedFault,
            PostLandingCanaryFailureClassifier.Classify(new InvalidOperationException("unexpected defect")));
        Assert.Equal(
            PostLandingCanaryFaultDisposition.VerdictFailure,
            PostLandingCanaryFailureClassifier.Classify(
                new PostLandingCanaryEvaluationException("landed artifact failed to build")));
        Assert.Equal(
            PostLandingCanaryFaultDisposition.VerdictFailure,
            PostLandingCanaryFailureClassifier.Classify(PostLandingCanaryOutcome.Failed(
                PostLandingCanaryFailureReason.EvaluatedArtifactFailure,
                "landed artifact failed to build")));
        Assert.Equal(
            PostLandingCanaryFaultDisposition.VerdictFailure,
            PostLandingCanaryFailureClassifier.Classify(PostLandingCanaryOutcome.Failed(
                PostLandingCanaryFailureReason.EmptyReceipt,
                "evaluation reported green without executing tests")));
        Assert.Equal(
            PostLandingCanaryFaultDisposition.EnvironmentFault,
            PostLandingCanaryFailureClassifier.Classify(PostLandingCanaryOutcome.Failed(
                PostLandingCanaryFailureReason.Timeout,
                "evaluation timed out")));
        Assert.Equal(
            PostLandingCanaryFaultDisposition.EnvironmentFault,
            PostLandingCanaryFailureClassifier.Classify(PostLandingCanaryOutcome.Failed(
                PostLandingCanaryFailureReason.InfrastructureError,
                "could not evaluate")));
        Assert.Equal(
            PostLandingCanaryFaultDisposition.VerdictFailure,
            PostLandingCanaryFailureClassifier.Classify(PostLandingCanaryOutcome.Failed(
                PostLandingCanaryFailureReason.Reject,
                "evaluated and failed")));
    }

    [Xunit.Fact(DisplayName = "Busy build slots defer without a verdict or circuit hold, then retry to a normal receipt")]
    public async Task BusyBuildSlotsDeferHealthyAndRetryThroughDurableQueue()
    {
        using var fixture = new CanaryTestFixture();
        var delayRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slotBusyCalls = 0;
        var now = DateTimeOffset.UtcNow;
        var busy = new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy(
            "run-slot-busy",
            [new DotnetBuildStableSlotWait(0, 100), new DotnetBuildStableSlotWait(1, 101)]));
        var (coordinator, circuit) = fixture.CreateCoordinator(
            new FakeRunner((request, _) =>
            {
                if (request.LandingSha == "sha-slot-busy" && Interlocked.Increment(ref slotBusyCalls) == 1)
                {
                    throw busy;
                }

                return Task.FromResult(PostLandingCanaryOutcome.Passed(1, $"passed {request.LandingSha}"));
            }),
            utcNow: () => now,
            delay: async (requested, cancellationToken) =>
            {
                delayRequested.TrySetResult();
                await releaseRetry.Task.WaitAsync(cancellationToken);
                now = now.Add(requested);
            },
            progress: line =>
            {
                if (line.Contains("sha=sha-slot-busy result=passed", StringComparison.Ordinal))
                {
                    retryCompleted.TrySetResult();
                }
            });

        var first = await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-slot-busy", ["engine/slot"]),
            CancellationToken.None);

        Assert.Equal(PostLandingCanaryDisposition.Deferred, first);
        await delayRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deferredHealth = circuit.Read();
        Assert.Equal(AcceptanceEngineHealth.Healthy, deferredHealth.Health);
        Assert.Equal("sha-slot-busy", deferredHealth.LandingSha);
        Assert.Equal("resource-busy", deferredHealth.FailureReason);
        Assert.StartsWith("run-event:", deferredHealth.ReceiptReference, StringComparison.Ordinal);
        Assert.Null(await fixture.RawStore.ReadByEventIdAsync(
            PostLandingCanaryEventIds.Receipt("sha-slot-busy")));
        Assert.Null(PostLandingCanaryFactory.BuildMutationBlockReason(
            OrchestratorWorkspace.ForDirectory(fixture.Root)));
        var deferred = Assert.Single((await fixture.RawStore.ReadByTypeSinceAsync(RunEventTypes.PostLandingCanary))
            .Where(item => item.Operation == "deferred"));
        Assert.Equal("CouldNotEvaluate", deferred.Status);
        Assert.Contains("DotnetBuildSlotsBusyException: Stable dotnet build slots busy", deferred.Detail);

        Assert.Equal(
            PostLandingCanaryDisposition.Passed,
            await coordinator.RunAsync(
                new PostLandingCanaryRequest("sha-not-blocked", ["engine/later"]),
                CancellationToken.None));
        Assert.NotNull(await fixture.RawStore.ReadByEventIdAsync(
            PostLandingCanaryEventIds.Receipt("sha-not-blocked")));

        releaseRetry.TrySetResult();
        await retryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var receipt = await fixture.RawStore.ReadByEventIdAsync(
            PostLandingCanaryEventIds.Receipt("sha-slot-busy"));
        Assert.Equal("Passed", Assert.IsType<RunEventRecord>(receipt).Status);
        Assert.Equal(AcceptanceEngineHealth.Healthy, circuit.Read().Health);
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
        var progress = new ConcurrentQueue<string>();
        var calls = 0;
        var runner = new FakeRunner((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(PostLandingCanaryOutcome.Passed(1, "known-green receipt"));
        });
        var (coordinator, circuit) = fixture.CreateCoordinator(runner, progress: progress.Enqueue);
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
        Assert.DoesNotContain(progress, line =>
            line.Contains("result=unverified", StringComparison.Ordinal) ||
            line.Contains("escalation=", StringComparison.Ordinal));
        Assert.Empty(await fixture.OperatorItems.GetAttentionQueueAsync());
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

    [Xunit.Fact(DisplayName = "An evaluated artifact failure trips the circuit; explicit clear restores it")]
    public async Task CircuitHoldsAcceptanceOnlyAndRequiresExplicitClearAfterFailure()
    {
        using var fixture = new CanaryTestFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            throw new PostLandingCanaryEvaluationException("freshly landed application failed to build");
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
        Assert.Equal("evaluated-artifact-failure", failed.FailureReason);
        Assert.True(ConductorBatchLoop.IsAcceptanceEngineCircuitHoldRequired(GoalStatus.Verified, failed));
        Assert.False(ConductorBatchLoop.IsAcceptanceEngineCircuitHoldRequired(GoalStatus.Active, failed));

        var cleared = circuit.Clear("engine repaired and independently verified");
        Assert.Equal(AcceptanceEngineHealth.Healthy, cleared.Health);
        Assert.False(ConductorBatchLoop.IsAcceptanceEngineCircuitHoldRequired(GoalStatus.Verified, cleared));
    }

    [Xunit.Fact(DisplayName = "A canary with an empty accept receipt trips the circuit as a verdict failure")]
    public async Task EmptyReceiptTripsCircuitAsVerdictFailure()
    {
        using var fixture = new CanaryTestFixture();
        var (coordinator, circuit) = fixture.CreateCoordinator(
            new FakeRunner((_, _) => Task.FromResult(PostLandingCanaryOutcome.Failed(
                PostLandingCanaryFailureReason.EmptyReceipt,
                "fixture started but produced no completed-test receipt"))),
            maxAttempts: 1);

        var disposition = await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-empty-receipt", ["engine/empty-receipt"]),
            CancellationToken.None);

        Assert.Equal(PostLandingCanaryDisposition.Failed, disposition);
        var failed = circuit.Read();
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, failed.Health);
        Assert.Equal("empty-receipt", failed.FailureReason);
    }

    [Xunit.Fact(DisplayName = "A canary infrastructure failure remains unverified and tells the operator")]
    public async Task InfrastructureFailureRemainsUnverifiedAndRaisesOperatorItem()
    {
        using var fixture = new CanaryTestFixture();
        var (coordinator, circuit) = fixture.CreateCoordinator(
            new FakeRunner((_, _) => Task.FromResult(PostLandingCanaryOutcome.Failed(
                PostLandingCanaryFailureReason.InfrastructureError,
                "receipt directory preflight was not writable"))),
            maxAttempts: 1);

        var disposition = await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-infrastructure-failure", ["engine/infrastructure-failure"]),
            CancellationToken.None);

        Assert.Equal(PostLandingCanaryDisposition.Abandoned, disposition);
        Assert.Equal(AcceptanceEngineHealth.Healthy, circuit.Read().Health);
        var item = Assert.Single(await fixture.OperatorItems.GetAttentionQueueAsync());
        Assert.Equal("Post-landing canary never evaluated sha-infrastructure-failure", item.Subject);
        Assert.Contains("UNVERIFIED after 1 attempts", item.Body, StringComparison.Ordinal);
        Assert.Contains("receipt directory preflight was not writable", item.Body, StringComparison.Ordinal);
        Assert.Contains("run-event:", item.Body, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Hard timeout cancels the runner, abandons unverified at the retry cap, and keeps process-tree kill path")]
    public async Task HardTimeoutCancelsRunnerAndAbandonsAtCap()
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
        var (coordinator, circuit) = fixture.CreateCoordinator(runner, maxAttempts: 1);

        var disposition = await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-timeout", ["engine/timeout"]),
            CancellationToken.None);

        Assert.Equal(PostLandingCanaryDisposition.Abandoned, disposition);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AcceptanceEngineHealth.Healthy, circuit.Read().Health);
        var abandoned = Assert.Single((await fixture.RawStore.ReadByTypeSinceAsync(RunEventTypes.PostLandingCanary))
            .Where(item => item.Operation == "abandoned"));
        Assert.Equal("Unverified", abandoned.Status);

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

    [Xunit.Fact(DisplayName = "Runner internal cancellation is recorded as infrastructure cancellation, not timeout")]
    public async Task RunnerInternalCancellationIsNotRecordedAsTimeout()
    {
        using var fixture = new CanaryTestFixture(timeoutSeconds: 10);
        using var runnerCancellation = new CancellationTokenSource();
        runnerCancellation.Cancel();
        var (coordinator, circuit) = fixture.CreateCoordinator(
            new FakeRunner((_, _) => Task.FromCanceled<PostLandingCanaryOutcome>(runnerCancellation.Token)),
            maxAttempts: 1);

        var disposition = await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-runner-cancel", ["engine/cancel"]),
            CancellationToken.None);

        Assert.Equal(PostLandingCanaryDisposition.Abandoned, disposition);
        Assert.Equal(AcceptanceEngineHealth.Healthy, circuit.Read().Health);
        var abandoned = Assert.Single((await fixture.RawStore.ReadByTypeSinceAsync(RunEventTypes.PostLandingCanary))
            .Where(item => item.Operation == "abandoned"));
        Assert.Contains("runner cancelled internally: TaskCanceledException", abandoned.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("hard timeout", abandoned.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exceeded", abandoned.Detail, StringComparison.OrdinalIgnoreCase);
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
        var (coordinator, _) = fixture.CreateCoordinator(runner, maxAttempts: 1);

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
        Assert.Equal(PostLandingCanaryDisposition.Abandoned, await first);
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

    [Xunit.Fact(DisplayName = "Post-main factory failure is nonblocking, fails closed, and raises an operator item")]
    public async Task PostMainFactoryFailureDefersAndRaisesOperatorItem()
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

            Assert.Equal(PostLandingCanaryDisposition.Deferred, disposition);
            Assert.Equal(
                AcceptanceEngineHealth.Unavailable,
                PostLandingCanaryFactory.CreateCircuit(workspace).Read().Health);
            var blockReason = PostLandingCanaryFactory.BuildMutationBlockReason(workspace);
            Assert.NotNull(blockReason);
            Assert.Contains("health=Unavailable policy=FailClosed outcome=denied", blockReason, StringComparison.Ordinal);
            var item = Assert.Single((await CollaborationItemStore
                .ForDirectory(workspace.OrchestratorDirectory)
                .GetAttentionQueueAsync())
                .Where(item => item.Subject.Contains("could not initialize", StringComparison.Ordinal)));
            Assert.Contains("sha-factory-failure", item.Subject, StringComparison.Ordinal);
            Assert.Contains("initialization failed", item.Body, StringComparison.Ordinal);
        }
        finally
        {
            PostLandingCanaryEmergencyCircuit.Clear(workspace.RunEventStorePath);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Theory(DisplayName = "Acceptance permission is an explicit health and unavailable-policy decision")]
    [Xunit.InlineData((int)AcceptanceEngineHealth.Healthy, (int)AcceptanceEngineUnavailablePolicy.FailOpen, true)]
    [Xunit.InlineData((int)AcceptanceEngineHealth.Healthy, (int)AcceptanceEngineUnavailablePolicy.FailClosed, true)]
    [Xunit.InlineData((int)AcceptanceEngineHealth.Pending, (int)AcceptanceEngineUnavailablePolicy.FailOpen, false)]
    [Xunit.InlineData((int)AcceptanceEngineHealth.Pending, (int)AcceptanceEngineUnavailablePolicy.FailClosed, false)]
    [Xunit.InlineData((int)AcceptanceEngineHealth.Unhealthy, (int)AcceptanceEngineUnavailablePolicy.FailOpen, false)]
    [Xunit.InlineData((int)AcceptanceEngineHealth.Unhealthy, (int)AcceptanceEngineUnavailablePolicy.FailClosed, false)]
    [Xunit.InlineData((int)AcceptanceEngineHealth.Unavailable, (int)AcceptanceEngineUnavailablePolicy.FailOpen, true)]
    [Xunit.InlineData((int)AcceptanceEngineHealth.Unavailable, (int)AcceptanceEngineUnavailablePolicy.FailClosed, false)]
    public void AcceptancePermissionRequiresHealthAndUnavailablePolicy(
        int healthValue,
        int policyValue,
        bool expectedAllowed)
    {
        var health = (AcceptanceEngineHealth)healthValue;
        var policy = (AcceptanceEngineUnavailablePolicy)policyValue;
        var decision = AcceptanceEngineAcceptanceGate.Decide(health, policy);

        Assert.Equal(expectedAllowed, decision.Allowed);
        Assert.Equal(health, decision.Health);
        Assert.Equal(policy, decision.PolicyApplied);
        Assert.Contains($"health={health}", decision.Reason, StringComparison.Ordinal);
        Assert.Contains($"policy={policy}", decision.Reason, StringComparison.Ordinal);
        Assert.Contains(expectedAllowed ? "outcome=permitted" : "outcome=denied", decision.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Unknown acceptance health and unavailable policy values deny")]
    public void AcceptanceGateDefaultsUnknownValuesToDeny()
    {
        var unknownHealth = AcceptanceEngineAcceptanceGate.Decide(
            (AcceptanceEngineHealth)int.MaxValue,
            AcceptanceEngineUnavailablePolicy.FailOpen);
        var unknownPolicy = AcceptanceEngineAcceptanceGate.Decide(
            AcceptanceEngineHealth.Unavailable,
            (AcceptanceEngineUnavailablePolicy)int.MaxValue);

        Assert.False(unknownHealth.Allowed);
        Assert.False(unknownPolicy.Allowed);
        Assert.Contains("defaulting to deny", unknownHealth.Reason, StringComparison.Ordinal);
        Assert.Contains("defaulting to deny", unknownPolicy.Reason, StringComparison.Ordinal);
        Assert.Null(typeof(AcceptanceEngineHealthSnapshot).GetProperty("AllowsAcceptance"));
    }

    [Xunit.Fact(DisplayName = "Persisted Unhealthy plus unreadable state fails closed without overwriting the circuit")]
    public async Task UnreadablePersistedUnhealthyStateFailsClosedAndRemainsPersisted()
    {
        using var fixture = new CanaryTestFixture();
        var (coordinator, _) = fixture.CreateCoordinator(new FakeRunner((_, _) => Task.FromResult(
            PostLandingCanaryOutcome.Failed(PostLandingCanaryFailureReason.Reject, "persisted verifier failure"))));
        await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-persisted-unhealthy", ["engine/unhealthy"]),
            CancellationToken.None);
        PostLandingCanaryEmergencyCircuit.Clear(fixture.DbPath);
        var stateReader = new ToggleAcceptanceEngineStateReader(fixture.Events)
        {
            Throws = true
        };
        var circuit = new AcceptanceEngineCircuitBreaker(
            fixture.Events,
            fixture.OperatorItems,
            stateReader: stateReader);

        var unavailable = circuit.Read();
        var decision = AcceptanceEngineAcceptanceGate.Decide(
            unavailable.Health,
            AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy);

        Assert.Equal(AcceptanceEngineHealth.Unavailable, unavailable.Health);
        Assert.Equal("state-unavailable", unavailable.FailureReason);
        Assert.False(decision.Allowed);
        Assert.Equal(AcceptanceEngineUnavailablePolicy.FailClosed, decision.PolicyApplied);
        Assert.Equal(AcceptanceEngineCircuitBreaker.StateReadMaxAttempts, stateReader.Attempts);
        Assert.Contains("InvalidOperationException: simulated SQLite lock", unavailable.OperatorNote, StringComparison.Ordinal);
        var item = Assert.Single((await fixture.OperatorItems.GetAttentionQueueAsync())
            .Where(item => item.CorrelationKey == AcceptanceEngineCircuitBreaker.StateUnavailableOperatorItemCorrelationKey));
        Assert.Contains("health=Unavailable policy=FailClosed outcome=denied", item.Body, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: simulated SQLite lock", item.Body, StringComparison.Ordinal);

        stateReader.Throws = false;
        var recovered = circuit.Read();
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, recovered.Health);
        Assert.Equal("sha-persisted-unhealthy", recovered.LandingSha);
    }

    [Xunit.Fact(DisplayName = "Unrecognized canary receipt status surfaces unavailable state without tripping unhealthy")]
    public async Task UnrecognizedReceiptStatusSurfacesUnavailableState()
    {
        using var fixture = new CanaryTestFixture();
        var payload = JsonSerializer.Serialize(new PostLandingCanaryEventPayload(
            PostLandingCanaryEventPayload.CanaryTag,
            "sha-version-skew",
            ["engine/version-skew"],
            null,
            1,
            "receipt from a newer producer",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
        await fixture.RawStore.AppendAsync(new RunEventAppend(
            RunEventTypes.PostLandingCanary,
            GoalId: null,
            Operation: "receipt",
            Status: "PassedWithWarnings",
            Detail: "unknown status fixture",
            PayloadJson: payload));

        var snapshot = fixture.CreateCoordinator(
            new FakeRunner((_, _) => Task.FromResult(PostLandingCanaryOutcome.Passed(1, "unused"))))
            .Circuit
            .Read();

        Assert.Equal(AcceptanceEngineHealth.Unavailable, snapshot.Health);
        Assert.NotEqual(AcceptanceEngineHealth.Unhealthy, snapshot.Health);
        Assert.Equal("state-unavailable", snapshot.FailureReason);
        Assert.Contains(nameof(PostLandingCanaryUnparseableReceiptException), snapshot.OperatorNote, StringComparison.Ordinal);
        Assert.Contains("PassedWithWarnings", snapshot.OperatorNote, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Explicit fail-open records Unavailable, policy, and permitted outcome")]
    public async Task UnreadableStateFailOpenIsTruthfulAndRecorded()
    {
        using var fixture = new CanaryTestFixture();
        var stateReader = new ToggleAcceptanceEngineStateReader(fixture.Events)
        {
            Throws = true
        };
        var circuit = new AcceptanceEngineCircuitBreaker(
            fixture.Events,
            fixture.OperatorItems,
            stateReader: stateReader);

        var unavailable = circuit.Read(AcceptanceEngineUnavailablePolicy.FailOpen);
        var decision = AcceptanceEngineAcceptanceGate.Decide(
            unavailable.Health,
            AcceptanceEngineUnavailablePolicy.FailOpen);

        Assert.Equal(AcceptanceEngineHealth.Unavailable, unavailable.Health);
        Assert.True(decision.Allowed);
        Assert.Contains("could not be checked, proceeding under policy FailOpen", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("health=Unavailable policy=FailOpen outcome=permitted", unavailable.OperatorNote, StringComparison.Ordinal);
        var item = Assert.Single(await fixture.OperatorItems.GetAttentionQueueAsync());
        Assert.Contains("health=Unavailable policy=FailOpen outcome=permitted", item.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("healthy", item.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "Unavailable-state item failure emits a non-database operator fallback")]
    public void UnavailableStateItemFailureEmitsOperatorFallback()
    {
        using var fixture = new CanaryTestFixture();
        var fallbackLines = new List<string>();
        var operatorItems = new FakeCollaborationItemStore
        {
            PendingRaise = Task.FromException<CollaborationItem>(
                new InvalidOperationException("collaboration store unavailable"))
        };
        var stateReader = new ToggleAcceptanceEngineStateReader(fixture.Events)
        {
            Throws = true
        };
        var circuit = new AcceptanceEngineCircuitBreaker(
            fixture.Events,
            operatorItems,
            stateReader: stateReader,
            stateUnavailableFallback: fallbackLines.Add);

        var snapshot = circuit.Read();

        Assert.Equal(AcceptanceEngineHealth.Unavailable, snapshot.Health);
        var fallback = Assert.Single(fallbackLines);
        Assert.Contains("result=operator-item-error", fallback, StringComparison.Ordinal);
        Assert.Contains("health=Unavailable policy=FailClosed outcome=denied", fallback, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: collaboration store unavailable", fallback, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "A transient state-read failure retries and returns the persisted fact")]
    public async Task TransientStateReadFailureRetriesBeforeDeclaringUnavailable()
    {
        using var fixture = new CanaryTestFixture();
        var stateReader = new ToggleAcceptanceEngineStateReader(fixture.Events)
        {
            FailuresRemaining = 2
        };
        var circuit = new AcceptanceEngineCircuitBreaker(
            fixture.Events,
            fixture.OperatorItems,
            stateReader: stateReader);

        var health = circuit.Read();

        Assert.Equal(AcceptanceEngineHealth.Healthy, health.Health);
        Assert.Equal(3, stateReader.Attempts);
        Assert.Empty(await fixture.OperatorItems.GetAttentionQueueAsync());
    }

    [Xunit.Fact(DisplayName = "Acceptance-engine CLI reports and explicitly clears the typed circuit")]
    public async Task AcceptanceEngineCliReportsAndClearsCircuit()
    {
        using var fixture = new CanaryTestFixture();
        var runner = new FakeRunner((_, _) => Task.FromResult(
            PostLandingCanaryOutcome.Failed(
                PostLandingCanaryFailureReason.Reject,
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

    [Xunit.Fact(DisplayName = "Retry exhaustion stays healthy and raises one unverified landing item")]
    public async Task RetryExhaustionAbandonsWithoutBlockingAndRaisesOneItem()
    {
        using var fixture = new CanaryTestFixture();
        var busy = new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy(
            "run-cap-exhaustion",
            [new DotnetBuildStableSlotWait(0, 100), new DotnetBuildStableSlotWait(1, 101)]));
        var (coordinator, circuit) = fixture.CreateCoordinator(
            new FakeRunner((_, _) => throw busy),
            maxAttempts: 1);
        var request = new PostLandingCanaryRequest("sha-unverified", ["engine/unverified"]);

        Assert.Equal(
            PostLandingCanaryDisposition.Abandoned,
            await coordinator.RunAsync(request, CancellationToken.None));
        Assert.Equal(AcceptanceEngineHealth.Healthy, circuit.Read().Health);
        Assert.Equal(
            PostLandingCanaryDisposition.AlreadyCompleted,
            await coordinator.RunAsync(request, CancellationToken.None));

        var item = Assert.Single(await fixture.OperatorItems.GetAttentionQueueAsync());
        Assert.Contains("sha-unverified", item.Subject, StringComparison.Ordinal);
        Assert.Contains("UNVERIFIED after 1 attempts", item.Body, StringComparison.Ordinal);
        Assert.Contains(
            "DotnetBuildSlotsBusyException: Stable dotnet build slots busy for run-cap-exhaustion.",
            item.Body,
            StringComparison.Ordinal);
        Assert.Contains("run-event:", item.Body, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Three identical canary faults escalate unverified without another backoff")]
    public async Task ThreeIdenticalFaultsEscalateWithoutAnotherBackoff()
    {
        using var fixture = new CanaryTestFixture();
        var now = DateTimeOffset.UtcNow;
        var escalationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ConcurrentQueue<string>();
        var (coordinator, circuit) = fixture.CreateCoordinator(
            new FakeRunner((_, _) => Task.FromException<PostLandingCanaryOutcome>(
                new InvalidOperationException("repeated deterministic defect"))),
            maxAttempts: 4,
            utcNow: () => now,
            delay: (requested, _) =>
            {
                now = now.Add(requested);
                return Task.CompletedTask;
            },
            progress: line =>
            {
                progress.Enqueue(line);
                if (line.Contains("escalation=repeated-identical-fault", StringComparison.Ordinal))
                {
                    escalationObserved.TrySetResult();
                }
            });

        Assert.Equal(
            PostLandingCanaryDisposition.Deferred,
            await coordinator.RunAsync(
                new PostLandingCanaryRequest("sha-repeated-fault", ["engine/repeated"]),
                CancellationToken.None));
        await escalationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var events = await fixture.Events.ReadForLandingAsync("sha-repeated-fault");
        Assert.Equal(2, events.Count(item => item.Kind == PostLandingCanaryEventKind.Deferred));
        var abandoned = Assert.Single(events.Where(item => item.Kind == PostLandingCanaryEventKind.Abandoned));
        Assert.Equal(3, abandoned.Payload.AttemptCount);
        Assert.Null(abandoned.Payload.NotBefore);
        Assert.Equal(AcceptanceEngineHealth.Healthy, circuit.Read().Health);
        Assert.Contains(progress, line =>
            line.Contains("result=unverified attempts=3", StringComparison.Ordinal) &&
            line.Contains("InvalidOperationException: repeated deterministic defect", StringComparison.Ordinal) &&
            line.Contains("escalation=repeated-identical-fault", StringComparison.Ordinal));
        var item = Assert.Single(await fixture.OperatorItems.GetAttentionQueueAsync());
        Assert.Contains("same fault repeated three times", item.Body, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Circuit failures create one receipt-rich item per unhealthy episode and clear resolves it")]
    public async Task CircuitFailureOperatorItemIsDeduplicatedAndResolvedPerEpisode()
    {
        using var fixture = new CanaryTestFixture();
        var (coordinator, circuit) = fixture.CreateCoordinator(new FakeRunner((request, _) =>
            Task.FromResult(PostLandingCanaryOutcome.Failed(
                PostLandingCanaryFailureReason.Reject,
                $"CanaryEvaluationException: evaluated failure for {request.LandingSha}"))));

        await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-episode-one", ["engine/one"]),
            CancellationToken.None);
        await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-episode-two", ["engine/two"]),
            CancellationToken.None);

        var firstEpisode = Assert.Single(await fixture.OperatorItems.GetAttentionQueueAsync());
        Assert.Contains("repeat-count=2", firstEpisode.Body, StringComparison.Ordinal);
        Assert.Contains("CanaryEvaluationException: evaluated failure for sha-episode-two", firstEpisode.Body, StringComparison.Ordinal);
        Assert.Contains("run-event:", firstEpisode.Body, StringComparison.Ordinal);

        circuit.Clear("fixture repair");
        Assert.Empty(await fixture.OperatorItems.GetAttentionQueueAsync());

        await coordinator.RunAsync(
            new PostLandingCanaryRequest("sha-episode-three", ["engine/three"]),
            CancellationToken.None);
        var secondEpisode = Assert.Single(await fixture.OperatorItems.GetAttentionQueueAsync());
        Assert.NotEqual(firstEpisode.Id, secondEpisode.Id);
        Assert.Contains("repeat-count=1", secondEpisode.Body, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Run-event show reads full goal-less receipt text and rejects an unknown sequence")]
    public async Task RunEventShowReadsGoalLessReceiptBySequence()
    {
        using var fixture = new CanaryTestFixture();
        const string detail =
            "DotnetBuildSlotsBusyException: Stable dotnet build slots busy for run-reader.";
        var record = await fixture.RawStore.AppendAsync(new RunEventAppend(
            RunEventTypes.PostLandingCanary,
            GoalId: null,
            Operation: "escalation",
            Status: "CanaryGateFailure",
            Detail: detail,
            PayloadJson: "{\"tag\":\"canary\"}"));
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var kernel = new AgentOrchestratorKernel();

        var text = ExecuteCliAndCapture(
            ["run-event", "show", record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            kernel,
            workspace);
        Assert.Contains($"run-event:{record.Sequence}", text, StringComparison.Ordinal);
        Assert.Contains("goal: none", text, StringComparison.Ordinal);
        Assert.Contains(detail, text, StringComparison.Ordinal);

        var error = Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(
            ["run-event", "show", (record.Sequence + 999).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            kernel,
            workspace));
        Assert.Contains("was not found", error.Message, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "Windows post-landing capture passes exact argv without a shell")]
    public async Task WindowsFileCapturePassesExactArgumentVectorWithoutShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "mcg canary argv & (tests)",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var scriptPath = Path.Combine(testRoot, "capture-argv.ps1");
        var outputPath = Path.Combine(testRoot, "received argv & (capture).json");
        var stdoutPath = Path.Combine(testRoot, "native stdout & (capture).log");
        var stderrPath = Path.Combine(testRoot, "native stderr & (capture).log");
        var expectedArguments = new[]
        {
            Path.Combine("plain", "path"),
            Path.Combine(testRoot, "path with spaces", "repository"),
            Path.Combine(testRoot, "path&with&metacharacters", "repository"),
            Path.Combine(testRoot, "path(with(parentheses)", "repository"),
            Path.Combine(testRoot, "path%PATH%with-expansion-token", "repository"),
            Path.Combine(testRoot, new string('l', 220), "repository")
        };
        try
        {
            File.WriteAllText(
                scriptPath,
                "$received = @($args | Select-Object -Skip 1)\r\n" +
                "[IO.File]::WriteAllText($args[0], (ConvertTo-Json -InputObject $received -Compress))\r\n" +
                "[Console]::Out.WriteLine('native stdout marker')\r\n" +
                "[Console]::Error.WriteLine('native stderr marker')\r\n");
            var arguments = new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath,
                outputPath
            }.Concat(expectedArguments).ToArray();
            var startInfo = PostLandingCanaryRunner.BuildFileCaptureStartInfo(
                "powershell.exe",
                arguments,
                testRoot,
                redirectStandardStreams: false);

            Assert.Equal("powershell.exe", startInfo.FileName);
            Assert.Equal(arguments, startInfo.ArgumentList);
            Assert.False(startInfo.RedirectStandardOutput);
            Assert.False(startInfo.RedirectStandardError);
            using var process = WorkerProcessJobs.StartRegisteredWithFileCaptureOrThrow(
                startInfo,
                stdoutPath,
                stderrPath,
                "post-landing-canary-argv-test");
            try
            {
                await process.WaitForExitAsync();

                Assert.True(
                    process.ExitCode == 0,
                    $"stdout: {File.ReadAllText(stdoutPath)}{Environment.NewLine}" +
                    $"stderr: {File.ReadAllText(stderrPath)}");
                Assert.Equal(expectedArguments, JsonSerializer.Deserialize<string[]>(File.ReadAllText(outputPath)));
                var stdout = File.ReadAllText(stdoutPath);
                var stderr = File.ReadAllText(stderrPath);
                Assert.Contains("native stdout marker", stdout, StringComparison.Ordinal);
                Assert.DoesNotContain("native stderr marker", stdout, StringComparison.Ordinal);
                Assert.Contains("native stderr marker", stderr, StringComparison.Ordinal);
                Assert.DoesNotContain("native stdout marker", stderr, StringComparison.Ordinal);
            }
            finally
            {
                WorkerProcessJobs.Release(process.Id);
            }
        }
        finally
        {
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    [Xunit.Theory(DisplayName = "Windows post-landing capture rejects batch targets with actionable configuration guidance")]
    [InlineData("dotnet.cmd")]
    [InlineData("fake-dotnet.bat")]
    public void WindowsFileCaptureRejectsBatchTargets(string fileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var targetPath = Path.Combine("C:\\tools with spaces", fileName);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostLandingCanaryRunner.BuildFileCaptureStartInfo(
                targetPath,
                ["--info"],
                Environment.CurrentDirectory,
                redirectStandardStreams: false));

        Assert.Contains(targetPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetExtension(targetPath), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MCG_ORCHESTRATOR_DOTNET_PATH", exception.Message, StringComparison.Ordinal);
        Assert.Contains("underlying executable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "Known-green fixture runs from an isolated landed worktree despite a dirty operator checkout")]
    public async Task KnownGreenFixtureRunsThroughFreshBinaryWithoutDirtyingRepository()
    {
        var root = FindRepoRoot();
        var landingSha = GoalAcceptanceVerifier.ResolveGitText(root, "rev-parse", "HEAD")?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(landingSha));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var buildCacheRoot = Path.Combine(
            Path.GetTempPath(),
            "mcg-canary-binary-tests",
            Guid.NewGuid().ToString("N"));
        var logDirectory = Path.Combine(buildCacheRoot, "logs");
        var dirtySentinel = Path.Combine(root, $"post-landing-canary-dirty-{Guid.NewGuid():N}.sentinel");
        try
        {
            File.WriteAllText(dirtySentinel, "operator-owned uncommitted content");
            var statusBefore = GoalAcceptanceVerifier.ResolveGitText(
                root,
                "status",
                "--porcelain",
                "--untracked-files=all");
            Assert.Contains(
                Path.GetFileName(dirtySentinel),
                statusBefore,
                StringComparison.Ordinal);
            var outcome = await new PostLandingCanaryRunner(
                    root,
                    buildCacheRoot: buildCacheRoot,
                    logDirectory: logDirectory)
                .RunAsync(
                    new PostLandingCanaryRequest(landingSha!, ["integration-test"]),
                    timeout.Token);

            Assert.True(outcome.Green, outcome.Detail);
            Assert.True(outcome.ExecutedTestCount > 0);
            Assert.NotEmpty(Directory.GetFiles(
                logDirectory,
                $"post-landing-canary-{landingSha}-*.out.log"));
            Assert.NotEmpty(Directory.GetFiles(
                logDirectory,
                $"post-landing-canary-{landingSha}-*.err.log"));
            Assert.NotEmpty(Directory.GetFiles(
                logDirectory,
                $"post-landing-canary-{landingSha}-*.trx"));
            Assert.Equal(
                statusBefore,
                GoalAcceptanceVerifier.ResolveGitText(
                    root,
                    "status",
                    "--porcelain",
                    "--untracked-files=all"));
        }
        finally
        {
            try { File.Delete(dirtySentinel); } catch { }
            try { Directory.Delete(buildCacheRoot, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "A runner process fault preserves SHA-named stdout and stderr logs")]
    public async Task RunnerFaultPreservesShaNamedStdoutAndStderrLogs()
    {
        var repositoryRoot = FindRepoRoot();
        var landingSha = GoalAcceptanceVerifier.ResolveGitText(repositoryRoot, "rev-parse", "HEAD")?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(landingSha));
        var testRoot = Path.Combine(Path.GetTempPath(), "mcg-canary-log-tests", Guid.NewGuid().ToString("N"));
        var logDirectory = Path.Combine(testRoot, "logs");
        var buildCacheRoot = Path.Combine(testRoot, "build");
        Directory.CreateDirectory(testRoot);
        var failingTool = CreateFailingCanaryTool(testRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var runner = new PostLandingCanaryRunner(
                repositoryRoot,
                dotnetPath: failingTool,
                buildCacheRoot: buildCacheRoot,
                logDirectory: logDirectory);

            var exception = await Assert.ThrowsAsync<PostLandingCanaryEvaluationException>(() => runner.RunAsync(
                new PostLandingCanaryRequest(landingSha!, ["integration-test"]),
                timeout.Token));

            var expectedStderr = OperatingSystem.IsWindows()
                ? "not a git command"
                : "induced canary stderr";
            Assert.Contains(expectedStderr, exception.Message, StringComparison.OrdinalIgnoreCase);
            var stdoutLogs = Directory.GetFiles(
                logDirectory,
                $"post-landing-canary-{landingSha}-*.out.log");
            var stderrLogs = Directory.GetFiles(
                logDirectory,
                $"post-landing-canary-{landingSha}-*.err.log");
            Assert.NotEmpty(stdoutLogs);
            Assert.NotEmpty(stderrLogs);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Contains(stdoutLogs, path =>
                    File.ReadAllText(path).Contains("induced canary stdout", StringComparison.Ordinal));
            }
            Assert.Contains(stderrLogs, path =>
                File.ReadAllText(path).Contains(expectedStderr, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    [Xunit.Theory(DisplayName = "Post-landing repository invariant accepts clean unchanged and fast-forwarded main")]
    [InlineData("baseline-sha")]
    [InlineData("other-goal-landing-sha")]
    public void RepositoryInvariantAcceptsCleanUnchangedOrFastForwardedMain(string currentSha)
    {
        var verdict = PostLandingCanaryRepositoryInvariant.Evaluate(
            "baseline-sha",
            currentSha,
            [],
            [],
            currentIsDescendantOfBaseline: true);

        Assert.True(verdict.Green, verdict.Detail);
        Assert.False(verdict.PreconditionFailure);
        Assert.Contains("Baseline SHA: baseline-sha", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains($"Current SHA: {currentSha}", verdict.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Post-landing repository invariant rejects history rewrites with diagnosable state")]
    public void RepositoryInvariantRejectsHistoryRewrite()
    {
        var verdict = PostLandingCanaryRepositoryInvariant.Evaluate(
            "baseline-sha",
            "rewritten-sha",
            [],
            [],
            currentIsDescendantOfBaseline: false);

        Assert.False(verdict.Green);
        Assert.False(verdict.PreconditionFailure);
        Assert.Contains("Baseline SHA: baseline-sha", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("Current SHA: rewritten-sha", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("Current is at or ahead of baseline: False", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("Current dirty paths: <none>", verdict.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Post-landing repository invariant distinguishes dirty precondition from run dirtiness")]
    public void RepositoryInvariantDistinguishesDirtyPreconditionFromRunDirtiness()
    {
        var precondition = PostLandingCanaryRepositoryInvariant.Evaluate(
            "baseline-sha",
            "baseline-sha",
            ["operator-note.txt"],
            ["operator-note.txt"],
            currentIsDescendantOfBaseline: true);
        var runDirtiness = PostLandingCanaryRepositoryInvariant.Evaluate(
            "baseline-sha",
            "other-goal-landing-sha",
            [],
            ["generated-by-canary.txt"],
            currentIsDescendantOfBaseline: true);

        Assert.False(precondition.Green);
        Assert.True(precondition.PreconditionFailure);
        Assert.Contains("operator-note.txt", precondition.Detail, StringComparison.Ordinal);
        Assert.False(runDirtiness.Green);
        Assert.False(runDirtiness.PreconditionFailure);
        Assert.Contains("generated-by-canary.txt", runDirtiness.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Repository preconditions defer without consuming attempt budget while repository verdicts fail closed")]
    public async Task RepositoryFailureChannelsDriveDistinctCoordinatorDispositions()
    {
        var precondition = PostLandingCanaryRepositoryInvariant.Evaluate(
            "baseline-sha",
            "baseline-sha",
            ["operator-note.txt"],
            ["operator-note.txt"],
            currentIsDescendantOfBaseline: true);
        var verdict = PostLandingCanaryRepositoryInvariant.Evaluate(
            "baseline-sha",
            "other-goal-landing-sha",
            [],
            ["generated-by-canary.txt"],
            currentIsDescendantOfBaseline: true);
        var preconditionException = PostLandingCanaryRunner.CreateRepositoryFailureException(precondition);
        var verdictException = PostLandingCanaryRunner.CreateRepositoryFailureException(verdict);

        Assert.IsType<PostLandingCanaryPreconditionException>(preconditionException);
        Assert.Equal(
            PostLandingCanaryFaultDisposition.PreconditionFailure,
            PostLandingCanaryFailureClassifier.Classify(preconditionException));
        Assert.IsType<PostLandingCanaryEvaluationException>(verdictException);
        Assert.Equal(
            PostLandingCanaryFaultDisposition.VerdictFailure,
            PostLandingCanaryFailureClassifier.Classify(verdictException));

        using (var preconditionFixture = new CanaryTestFixture())
        {
            var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var (coordinator, circuit) = preconditionFixture.CreateCoordinator(
                new FakeRunner((_, _) => Interlocked.Increment(ref calls) == 1
                    ? Task.FromException<PostLandingCanaryOutcome>(preconditionException)
                    : Task.FromResult(PostLandingCanaryOutcome.Passed(1, "precondition cleared"))),
                maxAttempts: 1,
                delay: (_, cancellationToken) => releaseRetry.Task.WaitAsync(cancellationToken),
                progress: line =>
                {
                    if (line.Contains("result=passed", StringComparison.Ordinal))
                    {
                        retryCompleted.TrySetResult();
                    }
                });

            Assert.Equal(
                PostLandingCanaryDisposition.Deferred,
                await coordinator.RunAsync(
                    new PostLandingCanaryRequest("sha-precondition", ["engine/precondition"]),
                    CancellationToken.None));
            Assert.Equal(AcceptanceEngineHealth.Healthy, circuit.Read().Health);
            var records = await preconditionFixture.RawStore.ReadByTypeSinceAsync(
                RunEventTypes.PostLandingCanary);
            var deferred = Assert.Single(records.Where(record => record.Operation == "deferred"));
            Assert.Equal("CouldNotEvaluate", deferred.Status);
            Assert.Contains("PostLandingCanaryPreconditionException", deferred.Detail, StringComparison.Ordinal);
            var payload = JsonSerializer.Deserialize<PostLandingCanaryEventPayload>(
                Assert.IsType<string>(deferred.PayloadJson),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var deferredPayload = Assert.IsType<PostLandingCanaryEventPayload>(payload);
            Assert.Equal("precondition-failure", deferredPayload.FailureReason);
            Assert.Equal(0, deferredPayload.AttemptCount);
            Assert.DoesNotContain(records, record => record.Operation == "abandoned");
            Assert.DoesNotContain(records, record => record.Operation == "escalation");
            var operatorItem = Assert.Single(await preconditionFixture.OperatorItems.GetAttentionQueueAsync());
            Assert.Contains("precondition requires operator action", operatorItem.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("operator-note.txt", operatorItem.Body, StringComparison.Ordinal);

            releaseRetry.TrySetResult();
            await retryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var completedEvents = await preconditionFixture.Events.ReadForLandingAsync("sha-precondition");
            Assert.Equal(2, calls);
            Assert.Equal(
                [1, 1],
                completedEvents
                    .Where(item => item.Kind == PostLandingCanaryEventKind.Started)
                    .Select(item => item.Payload.AttemptCount));
            Assert.Contains(completedEvents, item =>
                item.Kind == PostLandingCanaryEventKind.Passed && item.Payload.ExecutedTestCount == 1);
            Assert.Empty(await preconditionFixture.OperatorItems.GetAttentionQueueAsync());
        }

        using (var verdictFixture = new CanaryTestFixture())
        {
            var (coordinator, circuit) = verdictFixture.CreateCoordinator(
                new FakeRunner((_, _) => Task.FromException<PostLandingCanaryOutcome>(verdictException)),
                maxAttempts: 1);

            Assert.Equal(
                PostLandingCanaryDisposition.Failed,
                await coordinator.RunAsync(
                    new PostLandingCanaryRequest("sha-verdict", ["engine/verdict"]),
                    CancellationToken.None));
            Assert.Equal(AcceptanceEngineHealth.Unhealthy, circuit.Read().Health);
            var records = await verdictFixture.RawStore.ReadByTypeSinceAsync(
                RunEventTypes.PostLandingCanary);
            Assert.Contains(records, record =>
                record.Operation == "receipt" && record.Status == "Failed");
            Assert.Contains(records, record =>
                record.Operation == "escalation" && record.Status == "CanaryGateFailure");
            Assert.DoesNotContain(records, record => record.Operation == "abandoned");
        }

        using (var unexpectedFixture = new CanaryTestFixture())
        {
            var unexpected = new InvalidOperationException("unexpected deterministic defect");
            var progress = new ConcurrentQueue<string>();
            var (coordinator, _) = unexpectedFixture.CreateCoordinator(
                new FakeRunner((_, _) => Task.FromException<PostLandingCanaryOutcome>(unexpected)),
                maxAttempts: 1,
                progress: progress.Enqueue);

            Assert.Equal(
                PostLandingCanaryDisposition.Abandoned,
                await coordinator.RunAsync(
                    new PostLandingCanaryRequest("sha-unexpected", ["engine/unexpected"]),
                    CancellationToken.None));
            var abandoned = Assert.Single((await unexpectedFixture.RawStore.ReadByTypeSinceAsync(
                    RunEventTypes.PostLandingCanary))
                .Where(record => record.Operation == "abandoned"));
            Assert.Equal("InvalidOperationException: unexpected deterministic defect", abandoned.Detail);
            Assert.Contains(progress, line =>
                line.Contains("reason=unexpected-fault", StringComparison.Ordinal) &&
                line.Contains(
                    "detail=InvalidOperationException: unexpected deterministic defect",
                    StringComparison.Ordinal));
            var payload = JsonSerializer.Deserialize<PostLandingCanaryEventPayload>(
                Assert.IsType<string>(abandoned.PayloadJson),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal("unexpected-fault", Assert.IsType<PostLandingCanaryEventPayload>(payload).FailureReason);
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

    private static string CreateFailingCanaryTool(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return "git.exe";
        }

        var scriptPath = Path.Combine(root, "fail-canary.sh");
        File.WriteAllText(
            scriptPath,
            "#!/bin/sh\nprintf '%s\\n' 'induced canary stdout'\nprintf '%s\\n' 'induced canary stderr' >&2\nexit 23\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return scriptPath;
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
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ToggleAcceptanceEngineStateReader(PostLandingCanaryEventStore inner)
        : IAcceptanceEngineStateReader
    {
        internal bool Throws { get; set; }
        internal int FailuresRemaining { get; set; }
        internal int Attempts { get; private set; }

        public Task<IReadOnlyList<PostLandingCanaryEvent>> ReadProjectionEventsAsync(
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Throws || FailuresRemaining > 0)
            {
                FailuresRemaining = Math.Max(0, FailuresRemaining - 1);
                return Task.FromException<IReadOnlyList<PostLandingCanaryEvent>>(
                    new InvalidOperationException("simulated SQLite lock"));
            }

            return inner.ReadProjectionEventsAsync(cancellationToken);
        }
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
            OperatorItems = CollaborationItemStore.ForDirectory(
                OrchestratorWorkspace.ForDirectory(_root).OrchestratorDirectory);
        }

        internal SqliteRunEventStore RawStore { get; }
        internal CollaborationItemStore OperatorItems { get; }
        internal PostLandingCanaryEventStore Events => _events;
        internal string Root => _root;
        internal string DbPath => OrchestratorWorkspace.ForDirectory(_root).RunEventStorePath;

        internal (PostLandingCanaryCoordinator Coordinator, AcceptanceEngineCircuitBreaker Circuit)
            CreateCoordinator(
                IPostLandingCanaryRunner runner,
                int maxAttempts = PostLandingCanaryConfiguration.DefaultMaxAttempts,
                Func<DateTimeOffset>? utcNow = null,
                Func<TimeSpan, CancellationToken, Task>? delay = null,
                Action<string>? progress = null)
        {
            var circuit = new AcceptanceEngineCircuitBreaker(_events, OperatorItems);
            return (
                new PostLandingCanaryCoordinator(
                    new PostLandingCanaryConfiguration(
                        true,
                        _timeoutSeconds,
                        [],
                        maxAttempts,
                        [1, 1, 1]),
                    runner,
                    _events,
                    circuit,
                    OperatorItems,
                    utcNow: utcNow,
                    delay: delay,
                    progress: progress),
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
