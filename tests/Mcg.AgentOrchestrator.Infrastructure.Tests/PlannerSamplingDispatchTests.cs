using System.ComponentModel;
using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerSamplingDispatchTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void ConfiguredPlannerSampleCountProducesNCandidates()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        var plan = ReadPlannerFixture();
        File.WriteAllText(primaryPath, plan);
        const int configuredSampleCount = 3;
        var sampleCount = PlannerSamplingPolicy.EffectiveSampleCount(AgentRole.Planner, configuredSampleCount);
        var artifacts = PlannerSampleDispatcher.CreateArtifacts(primaryPath, sampleCount);
        var primaryParameters = CreateRunParameters(root, primaryPath);
        var launchedProcessIds = new List<int>();
        var sampleSandboxRoots = new List<string>();
        var launchCount = 0;
        var launches = PlannerSampleDispatcher.StartSamples(
            artifacts,
            primaryParameters,
            "dispatch-host.dll",
            "planner-sampling-test",
            startInfo =>
            {
                launchCount++;
                var sampleParameters = DispatchProcessHost.ReadParameters(startInfo.ArgumentList.Last());
                sampleSandboxRoots.Add(DispatchProcessHost.ResolveSandboxRoot(sampleParameters));
                File.WriteAllText(
                    sampleParameters.StdoutPath,
                    plan + Environment.NewLine + $"<!-- launched sample {launchCount} -->");
                DispatchExitArtifacts.Write(
                    sampleParameters.ExitCodePath,
                    DispatchExitArtifacts.Native(0, "test sample completed", DateTimeOffset.UtcNow));
                var process = StartSleeper();
                launchedProcessIds.Add(process.Id);
                return process;
            });

        try
        {
            Xunit.Assert.Equal(configuredSampleCount - 1, launchCount);
            Xunit.Assert.All(launches, launch => Xunit.Assert.True(WorkerProcessJobs.HasRegisteredJob(launch.Process.Id)));
            Xunit.Assert.Equal(configuredSampleCount - 1, sampleSandboxRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Xunit.Assert.All(sampleSandboxRoots, sandboxRoot => Xunit.Assert.StartsWith(
                Path.Combine(root, ".mcg-sandbox", "planner-sample-"),
                sandboxRoot,
                StringComparison.OrdinalIgnoreCase));

            var registeredProcessIds = launches.Select(launch => launch.Process.Id).ToArray();
            PlannerSampleDispatcher.ReleaseStartGates(launches);
            Xunit.Assert.All(registeredProcessIds, processId => Xunit.Assert.True(WorkerProcessJobs.HasRegisteredJob(processId)));

            var candidates = PlannerSampleDispatcher.CollectCandidates(primaryPath, sampleCount);

            Xunit.Assert.Equal(configuredSampleCount, candidates.Count);
            Xunit.Assert.Equal([0, 1, 2], candidates.Select(candidate => candidate.Index));
            Xunit.Assert.All(candidates.Skip(1), candidate => Xunit.Assert.Contains("launched sample", candidate.StandardOutput));
        }
        finally
        {
            foreach (var processId in launchedProcessIds)
                try { WorkerProcessJobs.TryKillOrFallback(processId); } catch { }
        }
    }

    [Xunit.Fact]
    public void SingleSampleProductionCompletionPreservesLegacyContractBytes()
    {
        var root = CreateSeededDispatchRepository();
        var plan = ReadPlannerFixture();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-22T18:00:00Z"));
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root,
            AgentRole.Planner,
            plan,
            string.Empty,
            clock,
            mutateWorktree: SeedFixtureCitationTargets);
        var capturedBeforeCompletion = File.ReadAllText(process.StandardOutputPath);
        var legacyContract = PlannerOutputContract.Resolve(
            capturedBeforeCompletion,
            string.Empty,
            process.WorkingDirectory);
        Xunit.Assert.True(legacyContract.Succeeded, legacyContract.Diagnostic);
        var expectedPersistedOutput = capturedBeforeCompletion + PlannerOutputContract.BuildIngestedReceipt(
            process.StandardOutputPath,
            legacyContract.Plan!);

        new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.True(task.LastVerification!.Succeeded, task.LastVerification.StandardError);
        Xunit.Assert.Null(task.LastVerification.PlannerCandidateDivergence);
        Xunit.Assert.Equal(expectedPersistedOutput, task.LastVerification.AuthoritativeStandardOutput);
        Xunit.Assert.Equal(expectedPersistedOutput, File.ReadAllText(process.StandardOutputPath));
    }

    [Xunit.Fact]
    public void UnreleasedSampleCleanupRemovesJobOwnership()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        var artifacts = PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2);
        var launches = PlannerSampleDispatcher.StartSamples(
            artifacts,
            CreateRunParameters(root, primaryPath),
            "dispatch-host.dll",
            "planner-sampling-cleanup-test",
            _ => StartSleeper());
        var processId = Xunit.Assert.Single(launches).Process.Id;
        try
        {
            Xunit.Assert.True(WorkerProcessJobs.HasRegisteredJob(processId));
        }
        finally
        {
            PlannerSampleDispatcher.TerminateUnreleased(launches);
        }

        Xunit.Assert.False(WorkerProcessJobs.HasRegisteredJob(processId));
    }

    [Xunit.Fact]
    public void LaunchFailureDegradesAndPrimaryCompletes()
    {
        var root = CreateSeededDispatchRepository();
        var logRoot = Path.Combine(root, "logs");
        SeedFixtureCitationTargets(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Planner sample launch failure", [
            new TaskSpec(TaskId.New(), "Produce a sampled plan.", AgentRole.Planner)
        ]);
        var planner = new AgentDefinition(
            new AgentId("planner"),
            "Planner",
            AgentRole.Planner,
            new ModelProfile(
                "OpenAI",
                AgentCatalog.OpenAiSubscriptionModelAlias,
                ModelCapability.Text,
                SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [planner]);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "local",
            "Write-Output planner",
            root,
            DateTimeOffset.Parse("2026-08-22T18:00:00Z"),
            PlannerSampleCount: 3));

        var processes = new List<Process>();
        var startedProcessIds = new List<int>();
        var simulatedLiveProcessIds = new HashSet<int>();
        var invocation = 0;
        var runner = new BackgroundDispatchRunner(
            isStillRunning: simulatedLiveProcessIds.Contains,
            disableProcessStart: false,
            startProcess: startInfo =>
            {
                invocation++;
                if (invocation == 3)
                    throw new Win32Exception("fixture sample launch failure");

                var parameters = DispatchProcessHost.ReadParameters(startInfo.ArgumentList.Last());
                File.WriteAllText(parameters.StdoutPath, ReadPlannerFixture());
                DispatchExitArtifacts.Write(
                    parameters.ExitCodePath,
                    DispatchExitArtifacts.Native(0, "fixture completed", DateTimeOffset.UtcNow));
                var process = StartSleeper();
                processes.Add(process);
                startedProcessIds.Add(process.Id);
                return process;
            });

        try
        {
            var start = runner.TryStartLatestDispatch(kernel, goal.Id, task.Id, logRoot);

            Xunit.Assert.NotNull(start.ProcessRecord);
            Xunit.Assert.Equal(3, invocation);
            Xunit.Assert.True(WorkerProcessJobs.HasRegisteredJob(start.ProcessRecord.ProcessId));
            Xunit.Assert.True(WorkerProcessJobs.HasRegisteredJob(startedProcessIds[1]));
            Xunit.Assert.Equal(startedProcessIds, start.ProcessRecord.TrackedProcessIds);
            Xunit.Assert.Equal(new[] { startedProcessIds[1] }, start.ProcessRecord.NonBlockingProcessIds);
            simulatedLiveProcessIds.Add(startedProcessIds[1]);
            var failedSample = PlannerSampleDispatcher.CreateArtifacts(
                start.ProcessRecord.StandardOutputPath,
                3)[1];
            Xunit.Assert.Contains(
                "fixture sample launch failure",
                File.ReadAllText(failedSample.LaunchDiagnosticPath),
                StringComparison.Ordinal);

            runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

            Xunit.Assert.True(task.LastVerification!.Succeeded, task.LastVerification.StandardError);
            Xunit.Assert.All(startedProcessIds, processId => Xunit.Assert.False(WorkerProcessJobs.HasRegisteredJob(processId)));
        }
        finally
        {
            foreach (var processId in startedProcessIds)
                try { WorkerProcessJobs.TryKillOrFallback(processId); } catch { }
            foreach (var process in processes)
                try { process.Dispose(); } catch { }
        }
    }

    [Xunit.Fact]
    public void ClaudeSamplesUseIndependentProviderSessions()
    {
        const string primaryCommand = "claude -p --session-id primary-session";

        var first = PlannerSampleDispatcher.BuildIndependentCommand(primaryCommand, WorkerSandboxProvider.Claude);
        var second = PlannerSampleDispatcher.BuildIndependentCommand(primaryCommand, WorkerSandboxProvider.Claude);

        Xunit.Assert.DoesNotContain("primary-session", first, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("primary-session", second, StringComparison.Ordinal);
        Xunit.Assert.NotEqual(first, second);
    }

    private static string ReadPlannerFixture() => File.ReadAllText(Path.Combine(
        InfrastructureTestSupport.FindRepositoryRoot(),
        "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures", "PlannerOutputContract",
        "658501ce-f6708f44-20260805012800.out.txt"));

    private static DispatchProcessHost.DispatchRunParameters CreateRunParameters(string root, string primaryPath) =>
        new(
            "planner-command",
            root,
            primaryPath,
            Path.Combine(root, "planner.err.log"),
            Path.Combine(root, "planner.exit.txt"),
            Path.Combine(root, "planner.heartbeat.json"),
            DisableSharedCompilation: true,
            Provider: WorkerSandboxProvider.Codex);

    private static Process StartSleeper()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithArguments(WorkerShell.BaseArguments().Concat(["Start-Sleep -Seconds 30"]));
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Planner sample test host.");
    }

    private static void SeedFixtureCitationTargets(string worktree)
    {
        string[] paths =
        [
            "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.InternalState.cs",
            "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.Recording.cs",
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs",
            "src/Mcg.AgentOrchestrator.Core/Application/ReviewerWorkerResultBlockers.cs",
            "src/Mcg.AgentOrchestrator.Core/Reports/TaskOutcomeClassification.cs",
            "src/Mcg.AgentOrchestrator.Core/Reports/VerificationReports.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs",
            "tests/Mcg.AgentOrchestrator.Core.Tests/DispatchOutcomeClassifyTests.cs",
            "tests/Mcg.AgentOrchestrator.Core.Tests/DispatchExecutionTests.cs",
            "tests/Mcg.AgentOrchestrator.Core.Tests/VerificationAndInputWorklistTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsWorkerResultClassification.cs",
            "tests/Mcg.AgentOrchestrator.Core.Tests/ModelOutcomeScorecardTests.cs"
        ];
        foreach (var relativePath in paths)
        {
            var path = Path.Combine(worktree, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture citation target");
        }
    }
}
