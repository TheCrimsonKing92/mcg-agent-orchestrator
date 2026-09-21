using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSlotGateJobResources : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_unexpected_runner_fault_carries_phase_target_and_stack")]
    public async Task GoalAcceptanceVerifierUnexpectedRunnerFaultCarriesPhaseTargetAndStack()
    {
        static Task<GoalAcceptanceVerifier.CommandResult> ThrowUnexpectedRunnerFault(
            string[] _,
            string __,
            CancellationToken ___) =>
            throw new InvalidOperationException("owned process lifecycle failed");

        var verifier = new GoalAcceptanceVerifier(TestOverrides, ThrowUnexpectedRunnerFault);

        var exception = await Xunit.Record.ExceptionAsync(() =>
            verifier.RunAsync("C:\\fake\\worktree"));

        Assert.NotNull(exception);
        Assert.Equal("AcceptanceGateEngineException", exception.GetType().Name);
        Assert.Equal(
            "System.InvalidOperationException",
            exception.GetType().GetProperty("FaultType")?.GetValue(exception));
        Assert.Equal(
            "check-execution",
            exception.GetType().GetProperty("GatePhase")?.GetValue(exception));
        Assert.Equal(
            "dotnet test",
            exception.GetType().GetProperty("GateTarget")?.GetValue(exception));
        var faultStack = Assert.IsType<string>(
            exception.GetType().GetProperty("FaultStack")?.GetValue(exception));
        Assert.Contains("owned process lifecycle failed", faultStack, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowUnexpectedRunnerFault), faultStack, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_production_runner_handles_fast_ordinary_and_signaled_children")]
    public async Task GoalAcceptanceVerifierProductionRunnerHandlesChildControlMatrix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var shim = Path.Combine(root, "fake-dotnet.cmd");
        var receivedArguments = Path.Combine(root, "received-arguments.txt");
        var ready = Path.Combine(root, "slow.ready");
        var signal = Path.Combine(root, "slow.signal");
        File.WriteAllText(shim, """
            @echo off
            set mode=%~1
            shift
            if "%mode%"=="build-server" (
              if not "%~1"=="shutdown" exit /b 91
              > "%~2" echo build-server shutdown
              echo shutdown-out
              echo shutdown-err 1>&2
              exit /b 0
            )
            if "%mode%"=="ordinary" (
              echo ordinary-out
              echo ordinary-err 1>&2
              exit /b 7
            )
            if "%mode%"=="slow" (
              > "%~1" echo ready
              :wait_for_signal
              if not exist "%~2" goto wait_for_signal
              echo slow-out
              echo slow-err 1>&2
              exit /b 0
            )
            exit /b 92
            """);

        try
        {
            var shutdown = await GoalAcceptanceVerifier.RunProcessForTestsAsync(
                [shim, "build-server", "shutdown", receivedArguments],
                root,
                TimeSpan.FromSeconds(10));
            var ordinary = await GoalAcceptanceVerifier.RunProcessForTestsAsync(
                [shim, "ordinary"],
                root,
                TimeSpan.FromSeconds(10));

            var readyObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(root, Path.GetFileName(ready))
            {
                EnableRaisingEvents = true
            };
            watcher.Created += (_, _) => readyObserved.TrySetResult();
            var slowTask = GoalAcceptanceVerifier.RunProcessForTestsAsync(
                [shim, "slow", ready, signal],
                root,
                TimeSpan.FromSeconds(10));
            if (File.Exists(ready))
            {
                readyObserved.TrySetResult();
            }

            await readyObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            File.WriteAllText(signal, "go");
            var slow = await slowTask;

            Assert.Equal("build-server shutdown", File.ReadAllText(receivedArguments).Trim());
            Assert.Equal(0, shutdown.ExitCode);
            Assert.Contains("shutdown-out", shutdown.Output, StringComparison.Ordinal);
            Assert.Contains("shutdown-err", shutdown.Output, StringComparison.Ordinal);
            Assert.Equal(7, ordinary.ExitCode);
            Assert.Contains("ordinary-out", ordinary.Output, StringComparison.Ordinal);
            Assert.Contains("ordinary-err", ordinary.Output, StringComparison.Ordinal);
            Assert.Equal(0, slow.ExitCode);
            Assert.Contains("slow-out", slow.Output, StringComparison.Ordinal);
            Assert.Contains("slow-err", slow.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task ProductionRunnerConcurrentChildrenReleaseOwnedResourcesOnce()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var registryPath = Path.Combine(root, "spawn-registry.db");
        var shim = Path.Combine(root, "concurrent-child.cmd");
        var readyPaths = new[] { Path.Combine(root, "first.ready"), Path.Combine(root, "second.ready") };
        var signalPaths = new[] { Path.Combine(root, "first.signal"), Path.Combine(root, "second.signal") };
        var heartbeatPaths = new[] { Path.Combine(root, "first-heartbeat.json"), Path.Combine(root, "second-heartbeat.json") };
        var observations = new ConcurrentQueue<AcceptanceProcessCleanupObservation>();
        File.WriteAllText(shim, """
            @echo off
            if not "%~1"=="slow" exit /b 91
            > "%~2" echo ready
            :wait_for_signal
            if not exist "%~3" goto wait_for_signal
            echo %~4-out
            echo %~4-err 1>&2
            exit /b 0
            """);
        _ = StateDbMigrations.EnsureUpToDate(registryPath);
        WorkerProcessJobs.ConfigureRegistry(registryPath);
        Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());

        try
        {
            var readyObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(root, "*.ready")
            {
                EnableRaisingEvents = true
            };
            watcher.Created += (_, _) =>
            {
                if (readyPaths.All(File.Exists))
                {
                    readyObserved.TrySetResult();
                }
            };

            var runs = Enumerable.Range(0, 2)
                .Select(index => GoalAcceptanceVerifier.RunProcessWithHeartbeatForTestsAsync(
                    [shim, "slow", readyPaths[index], signalPaths[index], $"child-{index}"],
                    root,
                    TimeSpan.FromSeconds(15),
                    heartbeatPaths[index],
                    observations.Enqueue,
                    registrationIdentityReader: DeterministicRegistrationIdentity))
                .ToArray();
            if (readyPaths.All(File.Exists))
            {
                readyObserved.TrySetResult();
            }

            var firstRunCompletion = Task.WhenAny(runs).Unwrap();
            if (ReferenceEquals(
                    await Task.WhenAny(readyObserved.Task, firstRunCompletion),
                    firstRunCompletion))
            {
                _ = await firstRunCompletion;
            }

            await readyObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, WorkerProcessJobs.ListActiveRegistryEntriesForTests().Count);
            foreach (var signalPath in signalPaths)
            {
                File.WriteAllText(signalPath, "go");
            }

            var completed = await Task.WhenAll(runs);

            Assert.All(completed, run => Assert.Equal(0, run.Result.ExitCode));
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Equal(2, observations.Select(observation => observation.ProcessId).Distinct().Count());
            foreach (var processObservations in observations.GroupBy(observation => observation.ProcessId))
            {
                Assert.Equal(
                    ["started", "heartbeat-final", "registration-released", "owned-child-disposed"],
                    processObservations.Select(observation => observation.Stage).ToArray());
                var heartbeatFinal = Assert.Single(
                    processObservations,
                    observation => observation.Stage == "heartbeat-final");
                Assert.True(heartbeatFinal.RegistryActive);
                Assert.True(heartbeatFinal.JobActive);
                Assert.True(heartbeatFinal.NativeHandleOpen);

                var registrationReleased = Assert.Single(
                    processObservations,
                    observation => observation.Stage == "registration-released");
                Assert.False(registrationReleased.RegistryActive);
                Assert.False(registrationReleased.JobActive);
                Assert.True(registrationReleased.NativeHandleOpen);

                var disposed = Assert.Single(
                    processObservations,
                    observation => observation.Stage == "owned-child-disposed");
                Assert.False(disposed.RegistryActive);
                Assert.False(disposed.JobActive);
                Assert.False(disposed.NativeHandleOpen);
                Assert.True(disposed.ProcessDisposed);
            }

            foreach (var heartbeatPath in heartbeatPaths)
            {
                using var heartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPath));
                Assert.Equal("completed", heartbeat.RootElement.GetProperty("state").GetString());
            }
        }
        finally
        {
            foreach (var signalPath in signalPaths)
            {
                File.WriteAllText(signalPath, "cleanup");
            }

            WorkerProcessJobs.ClearRegistryForTests();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task ProductionRunnerMixedFailuresReleaseOwnedResourcesOnce()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var registryPath = Path.Combine(root, "spawn-registry.db");
        var shim = Path.Combine(root, "held-child.cmd");
        var modes = new[] { "timeout", "cancel", "fault" };
        var readyPaths = modes.ToDictionary(mode => mode, mode => Path.Combine(root, $"{mode}.ready"));
        var signalPaths = modes.ToDictionary(mode => mode, mode => Path.Combine(root, $"{mode}.signal"));
        var heartbeatPaths = modes.ToDictionary(mode => mode, mode => Path.Combine(root, $"{mode}-heartbeat.json"));
        var observations = new ConcurrentQueue<(string Mode, AcceptanceProcessCleanupObservation Observation)>();
        File.WriteAllText(shim, """
            @echo off
            > "%~2" echo ready
            :wait_for_signal
            if not exist "%~3" goto wait_for_signal
            echo %~1-out
            echo %~1-err 1>&2
            exit /b 0
            """);
        _ = StateDbMigrations.EnsureUpToDate(registryPath);
        WorkerProcessJobs.ConfigureRegistry(registryPath);
        Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
        using var timeoutSignal = new CancellationTokenSource();
        using var cancellation = new CancellationTokenSource();

        try
        {
            var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(root, "*.ready")
            {
                EnableRaisingEvents = true
            };
            watcher.Created += (_, _) =>
            {
                if (readyPaths.Values.All(File.Exists))
                {
                    allReady.TrySetResult();
                }
            };

            Action<AcceptanceProcessCleanupObservation> Observe(string mode) => observation =>
            {
                observations.Enqueue((mode, observation));
                if (mode == "fault" && observation.Stage == "heartbeat-final")
                {
                    throw new InvalidOperationException("post-start cleanup observation fault");
                }
            };

            var timeoutRun = GoalAcceptanceVerifier.RunProcessWithHeartbeatForTestsAsync(
                [shim, "timeout", readyPaths["timeout"], signalPaths["timeout"]],
                root,
                TimeSpan.FromMinutes(1),
                heartbeatPaths["timeout"],
                Observe("timeout"),
                timeoutSignal: timeoutSignal.Token,
                registrationIdentityReader: DeterministicRegistrationIdentity);
            var cancellationRun = GoalAcceptanceVerifier.RunProcessWithHeartbeatForTestsAsync(
                [shim, "cancel", readyPaths["cancel"], signalPaths["cancel"]],
                root,
                TimeSpan.FromMinutes(1),
                heartbeatPaths["cancel"],
                Observe("cancel"),
                cancellation.Token,
                registrationIdentityReader: DeterministicRegistrationIdentity);
            var faultRun = GoalAcceptanceVerifier.RunProcessWithHeartbeatForTestsAsync(
                [shim, "fault", readyPaths["fault"], signalPaths["fault"]],
                root,
                TimeSpan.FromMinutes(1),
                heartbeatPaths["fault"],
                Observe("fault"),
                registrationIdentityReader: DeterministicRegistrationIdentity);
            if (readyPaths.Values.All(File.Exists))
            {
                allReady.TrySetResult();
            }

            var runs = new[] { timeoutRun, cancellationRun, faultRun };
            var firstRunCompletion = Task.WhenAny(runs).Unwrap();
            if (ReferenceEquals(
                    await Task.WhenAny(allReady.Task, firstRunCompletion),
                    firstRunCompletion))
            {
                _ = await firstRunCompletion;
            }

            await allReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(3, WorkerProcessJobs.ListActiveRegistryEntriesForTests().Count);
            timeoutSignal.Cancel();
            cancellation.Cancel();
            File.WriteAllText(signalPaths["fault"], "go");

            var timedOut = await timeoutRun;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancellationRun);
            var fault = await Assert.ThrowsAsync<InvalidOperationException>(async () => await faultRun);

            Assert.True(timedOut.Result.TimedOut);
            Assert.Equal(-1, timedOut.Result.ExitCode);
            Assert.Equal("post-start cleanup observation fault", fault.Message);
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            foreach (var mode in modes)
            {
                var modeObservations = observations
                    .Where(entry => entry.Mode == mode)
                    .Select(entry => entry.Observation)
                    .ToArray();
                Assert.Equal(
                    ["started", "heartbeat-final", "registration-released", "owned-child-disposed"],
                    modeObservations.Select(observation => observation.Stage).ToArray());
                Assert.Single(modeObservations, observation => observation.Stage == "heartbeat-final");
                Assert.Single(modeObservations, observation => observation.Stage == "registration-released");
                Assert.Single(modeObservations, observation => observation.Stage == "owned-child-disposed");
                var released = modeObservations.Single(observation => observation.Stage == "registration-released");
                Assert.False(released.RegistryActive);
                Assert.False(released.JobActive);
                Assert.True(released.NativeHandleOpen);
                var disposed = modeObservations.Single(observation => observation.Stage == "owned-child-disposed");
                Assert.False(disposed.NativeHandleOpen);
                Assert.True(disposed.ProcessDisposed);
            }

            using var timeoutHeartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPaths["timeout"]));
            using var cancellationHeartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPaths["cancel"]));
            using var faultHeartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPaths["fault"]));
            Assert.Equal("timed-out", timeoutHeartbeat.RootElement.GetProperty("state").GetString());
            Assert.Equal("failed", cancellationHeartbeat.RootElement.GetProperty("state").GetString());
            Assert.Equal("completed", faultHeartbeat.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            foreach (var signalPath in signalPaths.Values)
            {
                File.WriteAllText(signalPath, "cleanup");
            }

            WorkerProcessJobs.ClearRegistryForTests();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier cancellation kills its ready descendant tree and releases ownership")]
    public async Task ProductionRunnerCancellationKillsReadyDescendantTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var registryPath = Path.Combine(root, "spawn-registry.db");
        var childPidPath = Path.Combine(root, "descendant.pid");
        var heartbeatPath = Path.Combine(root, "cancellation-heartbeat.json");
        var observations = new ConcurrentQueue<AcceptanceProcessCleanupObservation>();
        var dotnetPath = InfrastructureTestSupport.ResolveDotnetHostPath();
        var acceptanceAssemblyDirectory = Path.GetDirectoryName(typeof(GoalAcceptanceVerifier).Assembly.Location)!;
        const string probeAssemblyName = "Mcg.AgentOrchestrator.IsolatedDotnetProbe.dll";
        const string probeProjectName = "Mcg.AgentOrchestrator.IsolatedDotnetProbe";
        var leafDirectory = new DirectoryInfo(acceptanceAssemblyDirectory);
        var parentDirectory = leafDirectory.Parent;
        var candidateProbeAssemblyPaths = new List<string>
        {
            Path.Combine(acceptanceAssemblyDirectory, probeAssemblyName),
        };
        // Default SDK layout: <testProject>/bin/<Configuration>/<TargetFramework>.
        if (parentDirectory?.Parent?.Parent is { } testProjectDirectory)
        {
            candidateProbeAssemblyPaths.Add(Path.Combine(
                testProjectDirectory.FullName,
                "Fixtures",
                "IsolatedDotnetProbe",
                "bin",
                parentDirectory.Name,
                leafDirectory.Name,
                probeAssemblyName));
        }

        // Centralized artifacts layout: <artifacts>/bin/<Project>/<configuration>. The probe is
        // referenced with ReferenceOutputAssembly="false", so its managed assembly is never copied
        // beside the test assembly even though its apphost and runtime config are.
        if (parentDirectory?.Parent is { } projectOutputRoot)
        {
            candidateProbeAssemblyPaths.Add(Path.Combine(
                projectOutputRoot.FullName,
                probeProjectName,
                leafDirectory.Name,
                probeAssemblyName));
        }

        var probeAssemblyPath = string.Empty;
        foreach (var candidate in candidateProbeAssemblyPaths)
        {
            if (File.Exists(candidate))
            {
                probeAssemblyPath = candidate;
                break;
            }
        }

        Assert.True(
            probeAssemblyPath.Length > 0,
            $"Missing process-tree probe. Tried: {string.Join("; ", candidateProbeAssemblyPaths)}");
        var arguments = new[] { dotnetPath, probeAssemblyPath, "spawn-descendant", childPidPath };
        using var fixtureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixtureTimeout.Token);
        var fixtureBound = Task.Delay(Timeout.InfiniteTimeSpan, fixtureTimeout.Token);
        Process? child = null;
        Task<(GoalAcceptanceVerifier.CommandResult Result, string HeartbeatPath)>? run = null;
        _ = StateDbMigrations.EnsureUpToDate(registryPath);
        WorkerProcessJobs.ConfigureRegistry(registryPath);
        Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());

        try
        {
            run = GoalAcceptanceVerifier.RunProcessWithHeartbeatForTestsAsync(
                arguments,
                root,
                TimeSpan.FromMinutes(1),
                heartbeatPath,
                observations.Enqueue,
                cancellation.Token,
                registrationIdentityReader: DeterministicRegistrationIdentity);

            var childPid = 0;
            while (!File.Exists(childPidPath) ||
                   !int.TryParse(
                       File.ReadAllText(childPidPath).Trim(),
                       System.Globalization.CultureInfo.InvariantCulture,
                       out childPid) ||
                   childPid <= 0)
            {
                await Task.Delay(25, fixtureTimeout.Token);
            }

            try
            {
                child = Process.GetProcessById(childPid);
            }
            catch (ArgumentException exception)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Descendant exited before the cancellation control. pid={childPid}; {exception.Message}");
            }
            child.Refresh();
            Assert.False(child.HasExited, $"Descendant exited before the cancellation control. pid={childPid}");
            Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());

            cancellation.Cancel();
            Assert.Same(run, await Task.WhenAny(run, fixtureBound));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
            await child.WaitForExitAsync(fixtureTimeout.Token);

            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Equal(
                ["started", "heartbeat-final", "registration-released", "owned-child-disposed"],
                observations.Select(observation => observation.Stage).ToArray());
            var disposed = Assert.Single(
                observations,
                observation => observation.Stage == "owned-child-disposed");
            Assert.False(disposed.RegistryActive);
            Assert.False(disposed.JobActive);
            Assert.False(disposed.NativeHandleOpen);
            Assert.True(disposed.ProcessDisposed);
        }
        finally
        {
            cancellation.Cancel();
            if (run is not null)
            {
                try { await run.WaitAsync(fixtureTimeout.Token); } catch { }
            }

            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync(fixtureTimeout.Token);
                    }
                }
                catch { }
                child.Dispose();
            }

            WorkerProcessJobs.ClearRegistryForTests();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_rejects_unbounded_focused_evidence_request")]
    public async Task GoalAcceptanceVerifierRejectsUnboundedFocusedEvidenceRequest()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "should not run"));
            });

            var result = await verifier.RunFocusedEvidenceAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                "Infrastructure.Tests: all");

            Assert.False(result.Accepted);
            Assert.False(result.Passed);
            Assert.Contains("unbounded evidence request rejected", result.Summary);
            Assert.Empty(result.Checks);
            Assert.Empty(calls);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_builds_once_and_runs_later_tests_no_build")]
    public async Task GoalAcceptanceVerifierSlotGateBuildsOnceAndRunsLaterTestsNoBuild()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "first tests", "type": "dotnet-test", "project": "tests/First.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "second tests", "type": "dotnet-test", "project": "tests/Second.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var firstTestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, async (args, _, _) =>
            {
                calls.Add(args);
                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "test" && args.Contains("tests/First.Tests.csproj"))
                {
                    firstTestStarted.SetResult();
                    await releaseFirstTest.Task.ConfigureAwait(false);
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.");
                }

                return new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length >= 2 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : "Build succeeded.");
            });

            var run = verifier.RunAsync(root, new GoalId("11112222333344445555666677778888"), stableSlotIndex: 0);
            await firstTestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Task.Delay(100);
            Assert.DoesNotContain(calls, call => call.Length >= 3 && call[0] == "dotnet" && call[1] == "build" && call[2] == "tests/Second.Tests.csproj");

            releaseFirstTest.SetResult();
            var result = await run;

            Assert.True(result.Passed);
            var buildCalls = calls
                .Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build")
                .ToArray();
            var firstTestIndex = calls.FindIndex(call => call.Length >= 3 && call[0] == "dotnet" && call[1] == "test" && call[2] == "tests/First.Tests.csproj");
            var secondTestIndex = calls.FindIndex(call => call.Length >= 3 && call[0] == "dotnet" && call[1] == "test" && call[2] == "tests/Second.Tests.csproj");
            Assert.Single(buildCalls);
            Assert.Equal("tests/First.Tests.csproj", buildCalls[0][2]);
            Assert.True(firstTestIndex > 0);
            Assert.True(secondTestIndex > firstTestIndex);
            Assert.DoesNotContain(calls, call => call.Length >= 3 && call[0] == "dotnet" && call[1] == "build" && call[2] == "tests/Second.Tests.csproj");
            Assert.All(calls.Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test"), call => Assert.Contains("--no-build", call));
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_retry_reaps_recorded_heartbeat_child_before_rebuild")]
    public void GoalAcceptanceVerifierSlotGateRetryReapsRecordedHeartbeatChildBeforeRebuild()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var buildAttempts = 0;
        var sleeper = StartSleepProcess();
        var goalId = new GoalId("99998888777766665555444433332222");
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
            "handle64-timeout");

        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    buildAttempts++;
                    if (buildAttempts == 1)
                    {
                        var artifactsPath = GetArtifactsPath(args);
                        var heartbeatEnvironment =
                            DotnetBuildEnvironmentManager.ResolveGoalEnvironment(goalId);
                        var heartbeatPath =
                            GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
                                "core tests",
                                heartbeatEnvironment,
                                stableSlotIndex: 0);
                        GateHeartbeatArtifacts.Write(
                            heartbeatPath,
                            new GateHeartbeatSnapshot(
                                "99998888777766665555444433332222",
                                "verification-check",
                                "core tests",
                                0,
                                sleeper.Id,
                                sleeper.Id,
                                "running",
                                DateTimeOffset.UtcNow,
                                DateTimeOffset.UtcNow,
                                DateTimeOffset.UtcNow,
                                0,
                                0,
                                0,
                                $"dotnet test --artifacts-path {artifactsPath}"));
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                            1,
                            $"error CS2012: Cannot open '{Path.Combine(artifactsPath, "bin", "Core.dll")}' for writing because it is being used by another process."));
                    }

                    Assert.True(sleeper.HasExited);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            });

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(root, goalId, stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(result);
            Assert.True(result!.Passed);
            Assert.True(result.Retried);
            Assert.Equal(2, buildAttempts);
            Assert.Contains("LOCK_CONTEXT ", output, StringComparison.Ordinal);
            Assert.Contains("heartbeat_child_alive=true", output, StringComparison.Ordinal);
            Assert.Contains($"holderPid={sleeper.Id}", output, StringComparison.Ordinal);
            Assert.Contains("source=handle64-timeout+gate-context", output, StringComparison.Ordinal);
            Assert.Contains(calls, call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test" && call.Contains("--no-build"));
        }
        finally
        {
            LockAttribution.AttributeForTests = null;
            if (!sleeper.HasExited)
            {
                try { sleeper.Kill(entireProcessTree: true); } catch { }
            }

            sleeper.Dispose();
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_lock_context_keeps_fast_exit_child_output_evidence")]
    public void GoalAcceptanceVerifierSlotGateLockContextKeepsFastExitChildOutputEvidence()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var buildAttempts = 0;
        var goalId = new GoalId("aaaabbbbccccddddeeeeffff00001111");
        var stdoutPath = Path.Combine(Path.GetTempPath(), $"mcg-acc-test-{Guid.NewGuid():N}.out");
        var stderrPath = Path.Combine(Path.GetTempPath(), $"mcg-acc-test-{Guid.NewGuid():N}.err");
        File.WriteAllText(stdoutPath, "fast child stdout");
        File.WriteAllText(stderrPath, "fast child stderr");
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
            "handle64-timeout");

        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    buildAttempts++;
                    if (buildAttempts == 1)
                    {
                        var artifactsPath = GetArtifactsPath(args);
                        var lockedPath = Path.Combine(artifactsPath, "bin", "Core.dll");
                        var heartbeatEnvironment =
                            DotnetBuildEnvironmentManager.ResolveGoalEnvironment(goalId);
                        var heartbeatPath =
                            GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
                                "core tests",
                                heartbeatEnvironment,
                                stableSlotIndex: 0);
                        GateHeartbeatArtifacts.Write(
                            heartbeatPath,
                            new GateHeartbeatSnapshot(
                                "aaaabbbbccccddddeeeeffff00001111",
                                "verification-check",
                                "core tests",
                                0,
                                999999,
                                999999,
                                "completed",
                                DateTimeOffset.UtcNow.AddSeconds(-18),
                                DateTimeOffset.UtcNow,
                                DateTimeOffset.UtcNow,
                                new FileInfo(stdoutPath).Length,
                                new FileInfo(stderrPath).Length,
                                new FileInfo(stdoutPath).Length + new FileInfo(stderrPath).Length,
                                $"dotnet test --artifacts-path {artifactsPath}",
                                1,
                                stdoutPath,
                                stderrPath));
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                            1,
                            $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process.",
                            StdoutPath: stdoutPath,
                            StderrPath: stderrPath,
                            Elapsed: TimeSpan.FromSeconds(18),
                            StdoutBytes: new FileInfo(stdoutPath).Length,
                            StderrBytes: new FileInfo(stderrPath).Length));
                    }

                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            });

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(root, goalId, stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(result);
            Assert.True(result!.Passed);
            Assert.True(result.Retried);
            Assert.Equal(2, buildAttempts);
            Assert.Contains("LOCK_CONTEXT ", output, StringComparison.Ordinal);
            Assert.Contains("exit_code=1", output, StringComparison.Ordinal);
            Assert.Contains("elapsed_ms=18000", output, StringComparison.Ordinal);
            Assert.Contains("stdout_bytes=17", output, StringComparison.Ordinal);
            Assert.Contains("stderr_bytes=17", output, StringComparison.Ordinal);
            Assert.Contains($"stdout={stdoutPath}", output, StringComparison.Ordinal);
            Assert.Contains($"stderr={stderrPath}", output, StringComparison.Ordinal);
            Assert.Contains("heartbeat_state=completed", output, StringComparison.Ordinal);
            Assert.Contains("heartbeat_child_alive=false", output, StringComparison.Ordinal);
            Assert.Contains("heartbeat_output_bytes=34", output, StringComparison.Ordinal);
        }
        finally
        {
            LockAttribution.AttributeForTests = null;
            TryDeleteStableSlotHeartbeat(0);
            try { File.Delete(stdoutPath); } catch { }
            try { File.Delete(stderrPath); } catch { }
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_records_job_resource_receipt")]
    public async Task GoalAcceptanceVerifierSlotGateRecordsJobResourceReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "missing project gate", "type": "dotnet-test", "project": "MissingProject.csproj", "arguments": ["--verbosity", "minimal"], "timeoutMinutes": 1 }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides);
            var result = await verifier.RunAsync(
                root,
                new GoalId("24682468246824682468246824682468"),
                stableSlotIndex: 0);

            Assert.False(result.Passed);
            var check = Assert.Single(result.Checks!);
            Assert.Equal("missing project gate", check.Name);
            Assert.True(check.ResultSummary?.Contains("RESOURCE phase=gate", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("cpu_ms=", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("peak_mem_bytes=", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("io_bytes=", StringComparison.Ordinal) == true);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_RunProcess_OwnedStart_ExecutesMarker")]
    public async Task RunProcessOwnedStartExecutesMarker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                {
                  "name": "owned start marker",
                  "type": "command",
                  "command": "powershell",
                  "arguments": [
                    "-NoProfile",
                    "-Command",
                    "Set-Content -LiteralPath 'owned-start.marker' -Value started"
                  ],
                  "timeoutMinutes": 1
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides);
            var result = await verifier.RunAsync(
                root,
                new GoalId("13571357135713571357135713571357"),
                stableSlotIndex: 0);

            Assert.True(result.Passed, result.OutputTail);
            Assert.Equal("started", File.ReadAllText(Path.Combine(root, "owned-start.marker")).Trim());
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_timeout_records_job_resource_receipt")]
    public async Task GoalAcceptanceVerifierSlotGateTimeoutRecordsJobResourceReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "slow build gate", "type": "command", "command": "dotnet", "arguments": ["build", "SlowGate.csproj", "--nologo"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var originalTimeout = AppContext.GetData(AcceptanceCheckTimeouts.AppContextKey);
        AppContext.SetData(AcceptanceCheckTimeouts.AppContextKey, TimeSpan.FromMilliseconds(500));
        try
        {
            File.WriteAllText(Path.Combine(root, "SlowGate.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <Target Name="SleepBeforeBuild" BeforeTargets="CoreCompile">
                    <Exec Command="powershell -NoProfile -ExecutionPolicy Bypass -Command &quot;Start-Sleep -Seconds 30&quot;" />
                  </Target>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Program.cs"), "Console.WriteLine(\"slow gate\");");

            var verifier = new GoalAcceptanceVerifier(TestOverrides);
            var result = await verifier.RunAsync(
                root,
                new GoalId("24682468246824682468246824682468"),
                stableSlotIndex: 0);

            Assert.False(result.Passed);
            var check = Assert.Single(result.Checks!);
            Assert.StartsWith("acceptance-check-timeout: slow-build-gate", check.Name, StringComparison.Ordinal);
            Assert.True(check.ResultSummary?.Contains("RESOURCE phase=gate", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("cpu_ms=", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("peak_mem_bytes=", StringComparison.Ordinal) == true);
            Assert.True(check.ResultSummary?.Contains("io_bytes=", StringComparison.Ordinal) == true);
        }
        finally
        {
            AppContext.SetData(AcceptanceCheckTimeouts.AppContextKey, originalTimeout);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_prefers_completed_red_test_verdict_after_transient_build_lock")]
    public void GoalAcceptanceVerifierSlotGatePrefersCompletedRedTestVerdictAfterTransientBuildLock()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Infra.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        var calls = new List<string[]>();
        var buildAttempts = 0;
        var testAttempts = 0;
        var fakeTimeProvider = new RecordingTimeProvider();
        var leaseTimeStartedAt = fakeTimeProvider.GetUtcNow();
        var slotEnvironment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var leasePrepareAttempts = 0;
        var previousWindow = TestOverrides.TransientNoHolderBuildLockWaitWindow;
        var previousPoll = TestOverrides.TransientNoHolderBuildLockPollInterval;
        var previousMaxCycles = TestOverrides.TransientNoHolderBuildLockMaxRetryCycles;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == slotEnvironment.ExecutionLockPath &&
                Interlocked.Increment(ref leasePrepareAttempts) <= 2)
            {
                var lockedPath = Path.Combine(current.ArtifactsPath, "bin", "Core.Tests.dll");
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
            "handle64-timeout");
        TestOverrides.TransientNoHolderBuildLockWaitWindow = TimeSpan.FromSeconds(2);
        TestOverrides.TransientNoHolderBuildLockPollInterval = TimeSpan.FromMilliseconds(10);
        TestOverrides.TransientNoHolderBuildLockMaxRetryCycles = 1;

        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    buildAttempts++;
                    if (buildAttempts == 1)
                    {
                        var artifactsPath = GetArtifactsPath(args);
                        var lockedPath = Path.Combine(artifactsPath, "bin", "Core.Tests.dll");
                        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
                        File.WriteAllText(lockedPath, "transiently reported by compiler");
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                            1,
                            $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."));
                    }

                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "test")
                {
                    testAttempts++;
                    return Task.FromResult(testAttempts == 1
                        ? new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 519, Skipped: 0, Total: 519.")
                        : new GoalAcceptanceVerifier.CommandResult(
                            1,
                            "Failed! - Failed: 1, Passed: 1980, Skipped: 0, Total: 1981.\n" +
                            "Red.Namespace.FailingTest failed\n" +
                            "error CS2012: Cannot open 'C:\\mcg-dotnet-isolated\\slots\\slot-0\\artifacts\\bin\\Core.Tests.dll' for writing because it is being used by another process."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            }, fakeTimeProvider, fakeTimeProvider.Advance);

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(
                    root,
                    new GoalId("11112222333344445555666677778888"),
                    stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(result);
            Assert.False(result!.Passed);
            Assert.True(result.Retried);
            Assert.True(leasePrepareAttempts >= 3);
            Assert.True(fakeTimeProvider.GetUtcNow() - leaseTimeStartedAt >= TimeSpan.FromMilliseconds(200));
            Assert.Equal(2, buildAttempts);
            Assert.Equal(2, testAttempts);
            Assert.Equal(1, result.ExitCode);
            Assert.NotNull(result.OutputTail);
            Assert.Contains("Red.Namespace.FailingTest", result.OutputTail!, StringComparison.Ordinal);
            Assert.Contains("LOCK_TRANSIENT_WAIT ", output, StringComparison.Ordinal);
            Assert.Contains("released=true", output, StringComparison.Ordinal);
            Assert.Matches(@"waited-ms=([0-9]+)", output);
            Assert.Contains("LOCK_TRANSIENT_RETRY ", output, StringComparison.Ordinal);
            Assert.Contains("verdict=completed", output, StringComparison.Ordinal);
            Assert.Contains("build-lock=false", output, StringComparison.Ordinal);
            Assert.DoesNotContain("verdict=blocked", output, StringComparison.Ordinal);
            Assert.All(calls.Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test"),
                call => Assert.Contains("--no-build", call));
        }
        finally
        {
            TestOverrides.TransientNoHolderBuildLockWaitWindow = previousWindow;
            TestOverrides.TransientNoHolderBuildLockPollInterval = previousPoll;
            TestOverrides.TransientNoHolderBuildLockMaxRetryCycles = previousMaxCycles;
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_self_heals_once_on_CS2012_file_lock_and_returns_passed")]
    public async Task GoalAcceptanceVerifierSelfHealsOnceOnCs2012FileLockAndReturnsPassed()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(1, "error CS2012: Cannot open 'Core.dll' for writing because it is being used by another process."),
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")                              // dotnet test - retry passes
        ]);

        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var goalId = new GoalId("abcd1234abcd1234abcd1234abcd1234");
        var result = await verifier.RunAsync("C:\\fake\\worktree", goalId);

        Assert.True(result.Passed);
        Assert.True(result.Retried);
        Assert.True(result.OutputTail is null);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, calls.Count);
        AssertIsolatedTestCommand(calls[0]);
        AssertIsolatedTestCommand(calls[1]);
        Assert.Equal(GetArtifactsPath(calls[0]), GetArtifactsPath(calls[1]));
        Assert.True(result.ArtifactsPath is not null);
        Assert.Contains(Path.Combine("goals", "abcd1234", "artifacts"), result.ArtifactsPath!, StringComparison.OrdinalIgnoreCase);
        var check = result.Checks!.Single(item => item.Name == "dotnet test");
        Assert.Equal("goal-acceptance-verifier", check.BrokerName);
        Assert.Equal("goal-abcd1234", check.LeaseId);
        Assert.True(check.DurationMilliseconds is >= 0);
        Assert.True(check.LockRemediationApplied);
        Assert.True(check.ResultSummary?.Contains("build artifact lock detected; holder attributed; remediation retried", StringComparison.Ordinal) == true);
        Assert.True(check.ResultSummary?.Contains("Failed: 0", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_stable_slot_shuts_down_compiler_server_before_CS2012_retry")]
    public async Task GoalAcceptanceVerifierStableSlotShutsDownCompilerServerBeforeCs2012Retry()
    {
        var events = new List<string>();
        var buildAttempts = 0;
        DotnetBuildEnvironmentManager.ShutdownBuildServersForTests = () => events.Add("shutdown");

        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    events.Add(buildAttempts++ == 0 ? "initial-build" : "retry-build");
                    return Task.FromResult(buildAttempts == 1
                        ? new GoalAcceptanceVerifier.CommandResult(
                            1,
                            "error CS2012: Cannot open 'Core.dll' for writing because it is being used by another process.")
                        : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            });
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));

            var result = await verifier.RunAsync(
                "C:\\fake\\worktree",
                new GoalId("abcd1234abcd1234abcd1234abcd1234"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);
            lease.Dispose();

            Assert.True(result.Passed);
            Assert.True(result.Retried);
            Assert.Equal(["initial-build", "shutdown", "retry-build"], events);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ShutdownBuildServersForTests =
                AssemblyBuildServerShutdownIsolation.SafeDefault;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_dotnet_test_adds_attempt_trx_logger_and_records_advisory_missing_trx")]
    public void GoalAcceptanceVerifierDotnetTestAddsAttemptTrxLoggerAndRecordsAdvisoryMissingTrx()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj" }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var goalId = new GoalId("feedfacefeedfacefeedfacefeedface");
        var attemptDirectory = Path.Combine(Path.GetTempPath(), $"mcg-trx-attempt-{Guid.NewGuid():N}");
        var attemptPrefix = Path.Combine(attemptDirectory, "feedface-0-20260717120000000");
        Directory.CreateDirectory(attemptDirectory);
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var calls = new List<string[]>();
        try
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, attemptPrefix);
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            });

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(root, goalId).GetAwaiter().GetResult());

            Assert.NotNull(result);
            Assert.True(result!.Passed);
            var testCall = calls.Single(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test");
            var expectedTrx = Path.Combine(attemptDirectory, "feedface-0-20260717120000000.core-tests.trx");
            Assert.Contains("--logger", testCall);
            Assert.Contains($"trx;LogFileName={Path.GetFileName(expectedTrx)}", testCall);
            Assert.Contains("--results-directory", testCall);
            Assert.Contains(attemptDirectory, testCall);
            Assert.Contains(expectedTrx, result.TestResultPaths!);
            Assert.Contains(expectedTrx, result.Checks!.Single().TestResultPaths!);
            Assert.Contains("TRX_TELEMETRY_UNAVAILABLE", output, StringComparison.Ordinal);
            Assert.True(result.OutputTail is null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(attemptDirectory);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_self_heals_once_on_compiler_lock_and_returns_failed_when_retry_also_fails")]
    public void GoalAcceptanceVerifierSelfHealsOnceOnCompilerLockAndReturnsFailedWhenRetryAlsoFails()
    {
        var calls = new List<string[]>();
        var lockedPath = Path.Combine("C:\\fake\\worktree", "obj", "Core.dll");
        var previousMaxCycles = TestOverrides.TransientNoHolderBuildLockMaxRetryCycles;
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(1, "MSB3491: Could not write lines to file because it is being used by another process."),
            new(1, $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."),
            new(1, $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process.")
        ]);

        try
        {
            TestOverrides.TransientNoHolderBuildLockMaxRetryCycles = 2;
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(responses.Dequeue());
            });

            BuildLockBlockedException? blocked = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                blocked = Assert.ThrowsAsync<BuildLockBlockedException>(() => verifier.RunAsync("C:\\fake\\worktree"))
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(blocked);
            Assert.Equal(lockedPath, blocked!.Attribution.Path);
            Assert.Equal(3, calls.Count);
            AssertIsolatedTestCommand(calls[0]);
            AssertIsolatedTestCommand(calls[1]);
            AssertIsolatedTestCommand(calls[2]);
            Assert.Contains("LOCK_TRANSIENT_WAIT ", output, StringComparison.Ordinal);
            Assert.Contains("max-cycles=2", output, StringComparison.Ordinal);
            Assert.Contains("cycle=2", output, StringComparison.Ordinal);
            Assert.Contains("LOCK_TRANSIENT_RETRY ", output, StringComparison.Ordinal);
            Assert.Contains("verdict=blocked", output, StringComparison.Ordinal);
            Assert.Contains("holder-pid=none", output, StringComparison.Ordinal);
            Assert.Contains("attribution-source=", output, StringComparison.Ordinal);
        }
        finally
        {
            TestOverrides.TransientNoHolderBuildLockMaxRetryCycles = previousMaxCycles;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_blocks_when_owned_process_kill_retry_still_reports_lock")]
    public async Task GoalAcceptanceVerifierBlocksWhenOwnedProcessKillRetryStillReportsLock()
    {
        var calls = new List<string[]>();
        var killed = new List<int>();
        var lockedPath = Path.Combine("C:\\fake\\worktree", "obj", "Core.dll");
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(1, $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."),
            new(1, $"error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."),
            new(1, $"MSB3491: Could not write lines to file '{lockedPath}' because it is being used by another process.")
        ]);
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(987654, "testhost", $"dotnet test --artifacts-path {path}", true)],
            "test");
        WorkerProcessJobs.TryKillPidTree = pid =>
        {
            killed.Add(pid);
            return true;
        };

        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(responses.Dequeue());
            });

            var blocked = await Assert.ThrowsAsync<BuildLockBlockedException>(() => verifier.RunAsync("C:\\fake\\worktree"));

            Assert.Equal(lockedPath, blocked.Attribution.Path);
            Assert.Equal([987654], killed);
            Assert.Equal(3, calls.Count);
            AssertIsolatedTestCommand(calls[0]);
            AssertIsolatedTestCommand(calls[1]);
            AssertIsolatedTestCommand(calls[2]);
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_does_not_self_heal_non_lock_failure")]
    public async Task GoalAcceptanceVerifierDoesNotSelfHealNonLockFailure()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(1, "error CS2012: Cannot open 'Core.dll' for writing")
        ]);

        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.False(result.Passed);
        Assert.False(result.Retried);
        Assert.True(result.OutputTail is not null);
        Assert.Equal(1, result.ExitCode);
        Assert.Single(calls);
        AssertIsolatedTestCommand(calls[0]);
        Assert.False(result.Checks!.Single(item => item.Name == "dotnet test").LockRemediationApplied);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_returns_failed_check_with_timeout_diagnostics")]
    public async Task GoalAcceptanceVerifierReturnsFailedCheckWithTimeoutDiagnostics()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "focused CLI infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": [ "--filter", "CliHelpTests", "--verbosity", "minimal" ] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    -1,
                    "restore complete\nstill running infrastructure tests",
                    TimedOut: true,
                    CommandLine: "dotnet test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    StdoutPath: "C:\\temp\\acceptance.out",
                    StderrPath: "C:\\temp\\acceptance.err",
                    Timeout: TimeSpan.FromMinutes(10)));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcd1234abcd1234abcd1234abcd1234"));

            Assert.False(result.Passed);
            Assert.Equal(-1, result.ExitCode);
            Assert.Single(calls);
            var check = Assert.Single(result.Checks!.Where(check => !check.Passed));
            Assert.Equal("acceptance-check-timeout: focused-cli-infrastructure-tests elapsed=10m budget=10m", check.Name);
            Assert.False(check.Passed);
            Assert.Equal("elapsed=10m budget=10m", check.ResultSummary);
            Assert.Equal(result.ArtifactsPath, check.ArtifactsPath);
            var outputTail = result.OutputTail ?? string.Empty;
            Assert.True(outputTail.Contains("Verification command timed out after elapsed=10m budget=10m.", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("Command: dotnet test", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("stdout: C:\\temp\\acceptance.out", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("stderr: C:\\temp\\acceptance.err", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("still running infrastructure tests", StringComparison.Ordinal), outputTail);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_returns_passed_without_retry_on_first_time_pass")]
    public async Task GoalAcceptanceVerifierReturnsPassedWithoutRetryOnFirstTimePass()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, "Test run succeeded.")
        ]);

        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.True(result.Passed);
        Assert.False(result.Retried);
        Assert.True(result.OutputTail is null);
        Assert.Equal(0, result.ExitCode);
        Assert.Single(calls);
        AssertIsolatedTestCommand(calls[0]);
        Assert.Equal(1, result.Checks!.Count);
        Assert.Equal("dotnet test", result.Checks![0].Name);
    }

    private static SpawnProcessIdentity DeterministicRegistrationIdentity(Process process) =>
        new(
            process.Id,
            DateTimeOffset.UnixEpoch.AddTicks(process.Id),
            $"test-owned-process-{process.Id}.exe");

}
