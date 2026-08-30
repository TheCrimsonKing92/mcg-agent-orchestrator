using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
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
    public void CollectCandidates_CodexJsonl_NormalizesWithoutProcessStart()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        var plan = ReadPlannerFixture();
        File.WriteAllText(primaryPath, ToCodexJsonl(plan, 11, 7, 3));
        var artifacts = PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2);
        var fakeHostStarts = 0;
        var subscriptionCommandStarts = 0;
        var startedAt = DateTimeOffset.Parse("2026-08-25T20:00:00Z");
        var launches = PlannerSampleDispatcher.StartSamples(
            artifacts,
            CreateRunParameters(root, primaryPath),
            "dispatch-host.dll",
            "planner-jsonl-preflight",
            startInfo =>
            {
                fakeHostStarts++;
                if (!string.Equals(startInfo.FileName, "dotnet", StringComparison.OrdinalIgnoreCase))
                    subscriptionCommandStarts++;
                var sampleParameters = DispatchProcessHost.ReadParameters(startInfo.ArgumentList.Last());
                File.WriteAllText(sampleParameters.StdoutPath, ToCodexJsonl(plan, 13, 8, 5));
                DispatchExitArtifacts.Write(
                    sampleParameters.ExitCodePath,
                    DispatchExitArtifacts.Native(0, "fixture completed", startedAt.AddSeconds(3)));
                return StartSleeper();
            },
            () => startedAt);
        var dispatch = new TaskDispatchRecord(
            "planner",
            "codex exec --json",
            root,
            DateTimeOffset.Parse("2026-08-25T20:00:00Z"),
            WorkerProviderKind: ProviderKind.OpenAICodexCli,
            PlannerSampleCount: 2);

        try
        {
            var candidates = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2, dispatch, 2_000);

            var sample = Xunit.Assert.Single(artifacts);
            var secondary = candidates[1];
            Xunit.Assert.Equal(1, fakeHostStarts);
            Xunit.Assert.Equal(0, subscriptionCommandStarts);
            Xunit.Assert.Equal(plan.ReplaceLineEndings("\n"), secondary.StandardOutput.ReplaceLineEndings("\n"));
            Xunit.Assert.Equal(PlannerCandidateNormalizationState.Normalized, secondary.NormalizationState);
            Xunit.Assert.Equal(PlannerCandidateTerminalState.Succeeded, secondary.TerminalState);
            Xunit.Assert.Equal(3_000, secondary.ElapsedMilliseconds);
            Xunit.Assert.Equal(13, secondary.ProviderUsage!.InputTokens);
            Xunit.Assert.Equal(8, secondary.ProviderUsage.CachedInputTokens);
            Xunit.Assert.Equal(5, secondary.ProviderUsage.OutputTokens);
            Xunit.Assert.Equal(64, secondary.ArtifactSha256!.Length);
            Xunit.Assert.True(File.Exists(sample.StandardOutputPath + ".jsonl"));
        }
        finally
        {
            PlannerSampleDispatcher.TerminateUnreleased(launches);
        }
    }

    [Xunit.Fact]
    public void SuccessfulEmptySample_IsTypedAndCannotCompete()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ToCodexJsonl(ReadPlannerFixture(), 11, 7, 3));
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.StandardOutputPath, string.Empty);
        DispatchExitArtifacts.Write(
            sample.ExitCodePath,
            DispatchExitArtifacts.Native(0, "fixture completed", DateTimeOffset.UtcNow));
        var dispatch = new TaskDispatchRecord(
            "planner",
            "codex exec --json",
            root,
            DateTimeOffset.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli,
            PlannerSampleCount: 2);

        var candidates = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2, dispatch);
        var result = PlannerCandidateSelector.Select(candidates, InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.Equal(PlannerCandidateNormalizationState.Empty, candidates[1].NormalizationState);
        var evidence = Xunit.Assert.Single(result.Receipt.Candidates!, candidate => candidate.CandidateIndex == 1);
        Xunit.Assert.Equal(PlannerCandidateContractVerdict.NotEvaluated, evidence.ContractVerdict);
        Xunit.Assert.Equal(0, result.Receipt.SelectedCandidateIndex);
    }

    [Xunit.Fact]
    public void CollectCandidates_UnsupportedJsonl_IsTypedUnrecognizedInReceipt()
    {
        AssertInvalidCodexSampleState(
            "{\"type\":\"future.event\",\"payload\":{}}",
            PlannerCandidateNormalizationState.Unrecognized);
    }

    [Xunit.Fact]
    public void CollectCandidates_MalformedJsonl_IsTypedMalformedInReceipt()
    {
        AssertInvalidCodexSampleState(
            "{not-jsonl}",
            PlannerCandidateNormalizationState.Malformed);
    }

    [Xunit.Fact]
    public void CollectCandidates_PartiallyMalformedJsonl_IsTypedMalformedAndCannotCompete()
    {
        var plan = ReadPlannerFixture();
        AssertInvalidCodexSampleState(
            ToCodexJsonl(plan, 13, 8, 5) + Environment.NewLine + "{not-jsonl}",
            PlannerCandidateNormalizationState.Malformed);
    }

    [Xunit.Fact]
    public void CollectCandidates_NonZeroCodexSample_PreservesReportedUsage()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ToCodexJsonl(ReadPlannerFixture(), 11, 7, 3));
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.StandardOutputPath, ToCodexJsonl("failed sample", 13, 8, 5));
        DispatchExitArtifacts.Write(
            sample.ExitCodePath,
            DispatchExitArtifacts.Native(17, "fixture failed", DateTimeOffset.UtcNow));
        var dispatch = CreateCodexDispatch(root);

        var candidates = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2, dispatch);
        var result = PlannerCandidateSelector.Select(candidates, InfrastructureTestSupport.FindRepositoryRoot());

        var secondary = candidates[1];
        Xunit.Assert.Equal(PlannerCandidateTerminalState.NonZeroExit, secondary.TerminalState);
        Xunit.Assert.Equal(13, secondary.ProviderUsage?.InputTokens);
        var evidence = Xunit.Assert.Single(result.Receipt.Candidates!, candidate => candidate.CandidateIndex == 1);
        Xunit.Assert.Equal(PlannerCandidateUsageState.Reported, evidence.ProviderUsage.State);
        Xunit.Assert.Equal(13, evidence.ProviderUsage.InputTokens);
    }

    [Xunit.Fact]
    public void CollectCandidates_NonZeroExitReasonContainingCancel_RemainsNonZeroExit()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        DispatchExitArtifacts.Write(
            sample.ExitCodePath,
            DispatchExitArtifacts.Native(17, "worker cancelled its own request", DateTimeOffset.UtcNow));

        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.NonZeroExit, candidate.TerminalState);
    }

    [Xunit.Fact]
    public void CollectCandidates_TypedCancellationRecord_ClassifiesCancelled()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        var cancelledAt = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        File.WriteAllText(
            sample.LaunchRecordPath,
            JsonSerializer.Serialize(new { ProcessId = 123, StartedAt = cancelledAt.AddSeconds(-1) }));
        PlannerSampleDispatcher.RecordCancelledSamples(primaryPath, 2, cancelledAt);
        DispatchExitArtifacts.Write(
            sample.ExitCodePath,
            DispatchExitArtifacts.Native(17, "fixture failed", cancelledAt.AddSeconds(1)));

        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.Cancelled, candidate.TerminalState);
        Xunit.Assert.Equal(2_000, candidate.ElapsedMilliseconds);
    }

    [Xunit.Fact]
    public void CollectCandidates_ExitZero_PreservesCancelledTerminal()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        var cancelledAt = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        File.WriteAllText(
            sample.LaunchRecordPath,
            JsonSerializer.Serialize(new { ProcessId = 123, StartedAt = cancelledAt.AddSeconds(-1) }));
        PlannerSampleDispatcher.RecordCancelledSamples(primaryPath, 2, cancelledAt);
        DispatchExitArtifacts.Write(
            sample.ExitCodePath,
            DispatchExitArtifacts.Native(0, "fixture completed", cancelledAt.AddSeconds(1)));

        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.Cancelled, candidate.TerminalState);
        Xunit.Assert.Empty(candidate.StandardOutput);
    }

    [Xunit.Fact]
    public void RecordCancelledSamples_RequiresLaunchIdentityAndPreservesPreLaunchCauses()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var samples = PlannerSampleDispatcher.CreateArtifacts(primaryPath, 3).ToArray();
        File.WriteAllText(samples[0].LaunchDiagnosticPath, "fixture launch failed");

        PlannerSampleDispatcher.RecordCancelledSamples(
            primaryPath,
            3,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"));

        var candidates = PlannerSampleDispatcher.CollectCandidates(primaryPath, 3);
        Xunit.Assert.Equal(PlannerCandidateTerminalState.LaunchFailed, candidates[1].TerminalState);
        Xunit.Assert.Equal(PlannerCandidateTerminalState.MissingExitArtifact, candidates[2].TerminalState);
        Xunit.Assert.All(samples, sample => Xunit.Assert.False(File.Exists(sample.TerminalRecordPath)));
    }

    [Xunit.Fact]
    public void CancelLatestProcess_PlannerN2_WritesTypedCancellationForCollection()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Cancel a sampled Planner", [
            new TaskSpec(TaskId.New(), "Produce a sampled plan.", AgentRole.Planner)
        ]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        var dispatch = new TaskDispatchRecord(
            "planner",
            "fixture planner command",
            root,
            now,
            PlannerSampleCount: 2);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(
                111,
                dispatch.Command,
                dispatch.WorkingDirectory,
                primaryPath,
                Path.Combine(root, "planner.err.log"),
                Path.Combine(root, "planner.exit.txt"),
                now,
                null,
                null,
                OwnedProcessIds: [111]));
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(
            sample.LaunchRecordPath,
            JsonSerializer.Serialize(new { ProcessId = 111, StartedAt = now }));
        var running = true;
        var runner = new BackgroundDispatchRunner(
            new TestClock(now.AddSeconds(5)),
            isStillRunning: _ => running,
            tryKillOwnedProcess: _ =>
            {
                running = false;
                return true;
            });

        runner.CancelLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.True(File.Exists(sample.TerminalRecordPath));
        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];
        Xunit.Assert.Equal(PlannerCandidateTerminalState.Cancelled, candidate.TerminalState);
    }

    [Xunit.Fact]
    public void CancelRunningProcesses_CancellationReceiptFailures_RecordAllAuthoritativeCancellations()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Cancel sampled Planners with receipt failures", [
            new TaskSpec(TaskId.New(), "Produce sampled plan A.", AgentRole.Planner),
            new TaskSpec(TaskId.New(), "Produce sampled plan B.", AgentRole.Planner)
        ]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var processId = 221;
        foreach (var task in goal.Tasks)
        {
            processId++;
            var primaryPath = Path.Combine(root, $"planner-{processId}.out.log");
            File.WriteAllText(primaryPath, ReadPlannerFixture());
            var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
            File.WriteAllText(
                sample.LaunchRecordPath,
                JsonSerializer.Serialize(new { ProcessId = processId, StartedAt = now.AddSeconds(-1) }));
            Directory.CreateDirectory(sample.TerminalRecordPath);
            var dispatch = new TaskDispatchRecord(
                "planner",
                "fixture planner command",
                root,
                now,
                PlannerSampleCount: 2);
            kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(
                    processId,
                    dispatch.Command,
                    dispatch.WorkingDirectory,
                    primaryPath,
                    Path.Combine(root, $"planner-{processId}.err.log"),
                    Path.Combine(root, $"planner-{processId}.exit.txt"),
                    now,
                    null,
                    null,
                    OwnedProcessIds: [processId]));
        }

        var runner = new BackgroundDispatchRunner(
            new TestClock(now.AddSeconds(5)),
            isStillRunning: _ => true,
            tryKillOwnedProcess: _ => true);

        var cancelledCount = runner.CancelRunningProcessesForGoal(kernel, goal.Id);

        Xunit.Assert.Equal(2, cancelledCount);
        foreach (var task in goal.Tasks)
        {
            var cancelled = kernel.GetTask(goal.Id, task.Id).LastProcess;
            Xunit.Assert.NotNull(cancelled);
            Xunit.Assert.True(cancelled.WasCancelled);
            Xunit.Assert.Equal(now.AddSeconds(5), cancelled.CompletedAt);
            Xunit.Assert.Contains(
                goal.Timeline,
                entry => entry.TaskId == task.Id &&
                    entry.Kind == ProgressKind.TaskNote &&
                    entry.Message.Contains(
                        "Planner sample cancellation evidence could not be persisted",
                        StringComparison.Ordinal));
        }
    }

    [Xunit.Fact]
    public void CollectCandidates_MalformedUsage_RemainsTypedUnknown()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        var plan = ReadPlannerFixture();
        File.WriteAllText(primaryPath, ToCodexJsonl(plan, 11, 7, 3));
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(
            sample.StandardOutputPath,
            JsonSerializer.Serialize(new
            {
                type = "item.completed",
                item = new { type = "agent_message", text = plan }
            }) + Environment.NewLine +
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":13,\"output_tokens\":\"bad\"}}");
        DispatchExitArtifacts.Write(
            sample.ExitCodePath,
            DispatchExitArtifacts.Native(0, "fixture completed", DateTimeOffset.UtcNow));

        var candidates = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2, CreateCodexDispatch(root));
        var result = PlannerCandidateSelector.Select(candidates, InfrastructureTestSupport.FindRepositoryRoot());

        var evidence = Xunit.Assert.Single(result.Receipt.Candidates!, candidate => candidate.CandidateIndex == 1);
        Xunit.Assert.Equal(PlannerCandidateUsageState.Unknown, evidence.ProviderUsage.State);
        Xunit.Assert.Equal("malformed", evidence.ProviderUsage.UnknownReason);
        Xunit.Assert.Null(evidence.ProviderUsage.InputTokens);
    }

    [Xunit.Fact]
    public void CollectCandidates_AuditOnlyCodexJsonl_NormalizesAndPreservesUsage()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ToCodexJsonl(ReadPlannerFixture(), 11, 7, 3));
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.StandardOutputPath + ".jsonl", ToCodexJsonl(ReadPlannerFixture(), 13, 8, 5));
        DispatchExitArtifacts.Write(
            sample.ExitCodePath,
            DispatchExitArtifacts.Native(0, "fixture completed", DateTimeOffset.UtcNow));

        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2, CreateCodexDispatch(root))[1];

        Xunit.Assert.Equal(PlannerCandidateNormalizationState.Normalized, candidate.NormalizationState);
        Xunit.Assert.NotEmpty(candidate.StandardOutput);
        Xunit.Assert.Equal(13, candidate.ProviderUsage?.InputTokens);
        Xunit.Assert.True(File.Exists(sample.StandardOutputPath + ".jsonl"));
    }

    [Xunit.Fact]
    public void CollectCandidates_MissingExitCodexJsonl_TypesEnvelopeBeforeExclusion()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ToCodexJsonl(ReadPlannerFixture(), 11, 7, 3));
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.StandardOutputPath, ToCodexJsonl(ReadPlannerFixture(), 13, 8, 5));

        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2, CreateCodexDispatch(root))[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.MissingExitArtifact, candidate.TerminalState);
        Xunit.Assert.Equal(PlannerCandidateNormalizationState.Normalized, candidate.NormalizationState);
        Xunit.Assert.Equal(13, candidate.ProviderUsage?.InputTokens);
        Xunit.Assert.Empty(candidate.StandardOutput);
    }

    [Xunit.Fact]
    public void CollectCandidates_MalformedTerminalRecord_IsLoudlyTyped()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.TerminalRecordPath, "{not-json}");

        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.MalformedTerminalArtifact, candidate.TerminalState);
        var selection = PlannerCandidateSelector.Select(
            PlannerSampleDispatcher.CollectCandidates(primaryPath, 2),
            InfrastructureTestSupport.FindRepositoryRoot());
        var evidence = Xunit.Assert.Single(
            selection.Receipt.Candidates!,
            item => item.CandidateIndex == sample.Index);
        Xunit.Assert.Equal(PlannerCandidateTerminalState.MalformedTerminalArtifact, evidence.TerminalState);
    }

    [Xunit.Fact]
    public void CollectCandidates_EmptyTerminalRecord_IsMalformed()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.TerminalRecordPath, "{}");

        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.MalformedTerminalArtifact, candidate.TerminalState);
    }

    [Xunit.Fact]
    public void CollectCandidates_IncompleteTerminalRecord_IsMalformed()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.TerminalRecordPath, "{\"State\":3}");

        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.MalformedTerminalArtifact, candidate.TerminalState);
    }

    [Xunit.Fact]
    public void CollectCandidates_OutOfDomainTerminalState_IsMalformed()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(
            sample.TerminalRecordPath,
            "{\"State\":999,\"RecordedAt\":\"2026-08-25T00:00:00Z\"}");

        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.MalformedTerminalArtifact, candidate.TerminalState);
    }

    [Xunit.Fact]
    public void CollectCandidates_UnreadableTerminalRecord_IsLoudlyTyped()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.TerminalRecordPath, "{}");

        using var locked = new FileStream(
            sample.TerminalRecordPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.UnreadableTerminalArtifact, candidate.TerminalState);
    }

    [Xunit.Fact]
    public void CollectCandidates_ExitZero_MalformedTerminalFailsClosed()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.StandardOutputPath, ReadPlannerFixture());
        File.WriteAllText(sample.TerminalRecordPath, "{not-json}");
        DispatchExitArtifacts.Write(
            sample.ExitCodePath,
            DispatchExitArtifacts.Native(0, "fixture completed", DateTimeOffset.UtcNow));

        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.MalformedTerminalArtifact, candidate.TerminalState);
        Xunit.Assert.Empty(candidate.StandardOutput);
    }

    [Xunit.Fact]
    public void CollectCandidates_ExitZero_UnreadableTerminalFailsClosed()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.StandardOutputPath, ReadPlannerFixture());
        File.WriteAllText(sample.TerminalRecordPath, "{}");
        DispatchExitArtifacts.Write(
            sample.ExitCodePath,
            DispatchExitArtifacts.Native(0, "fixture completed", DateTimeOffset.UtcNow));

        using var locked = new FileStream(
            sample.TerminalRecordPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.UnreadableTerminalArtifact, candidate.TerminalState);
        Xunit.Assert.Empty(candidate.StandardOutput);
    }

    [Xunit.Fact]
    public void CollectCandidates_MissingOutputArtifact_HasNoArtifactIdentity()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        DispatchExitArtifacts.Write(
            sample.ExitCodePath,
            DispatchExitArtifacts.Native(0, "fixture completed", DateTimeOffset.UtcNow));

        var candidates = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2);
        var result = PlannerCandidateSelector.Select(candidates, InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.Null(candidates[1].ArtifactSha256);
        var evidence = Xunit.Assert.Single(result.Receipt.Candidates!, candidate => candidate.CandidateIndex == 1);
        Xunit.Assert.Null(evidence.ArtifactSha256);
    }

    [Xunit.Fact]
    public void CollectCandidates_FreeTextTimeoutDiagnostic_DoesNotAssertDeadlineFired()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ReadPlannerFixture());
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.LaunchDiagnosticPath, "Host timed out while writing a launch diagnostic.");

        var candidate = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2)[1];

        Xunit.Assert.Equal(PlannerCandidateTerminalState.LaunchFailed, candidate.TerminalState);
    }

    [Xunit.Fact]
    public void StartSamples_LaunchRecordPersistenceFailure_DoesNotAdmitLiveSample()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        Directory.CreateDirectory(sample.LaunchRecordPath);

        var launches = PlannerSampleDispatcher.StartSamples(
            [sample],
            CreateRunParameters(root, primaryPath),
            "dispatch-host.dll",
            "planner-launch-record-failure",
            _ => StartSleeper());

        Xunit.Assert.Empty(launches);
        Xunit.Assert.True(File.Exists(sample.LaunchDiagnosticPath));
    }

    [Xunit.Fact]
    public void StartSamples_LaunchDiagnosticPersistenceFailure_IsLoud()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2)) with
        {
            LaunchDiagnosticPath = root
        };

        Xunit.Assert.Throws<UnauthorizedAccessException>(() => PlannerSampleDispatcher.StartSamples(
            [sample],
            CreateRunParameters(root, primaryPath),
            "dispatch-host.dll",
            "planner-launch-diagnostic-failure",
            _ => null));
    }

    [Xunit.Fact]
    public void StartSamples_DoublePersistenceFailure_TerminatesRegisteredProcess()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2)) with
        {
            LaunchDiagnosticPath = root
        };
        Directory.CreateDirectory(sample.LaunchRecordPath);
        Process? started = null;
        int? startedProcessId = null;

        try
        {
            Xunit.Assert.Throws<UnauthorizedAccessException>(() => PlannerSampleDispatcher.StartSamples(
                [sample],
                CreateRunParameters(root, primaryPath),
                "dispatch-host.dll",
                "planner-double-persistence-failure",
                _ =>
                {
                    started = StartSleeper();
                    startedProcessId = started.Id;
                    return started;
                }));

            Xunit.Assert.NotNull(started);
            Xunit.Assert.NotNull(startedProcessId);
            Xunit.Assert.False(WorkerProcessJobs.HasRegisteredJob(startedProcessId.Value));
        }
        finally
        {
            if (started is not null && startedProcessId is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(startedProcessId.Value); } catch { }
                try { started.Dispose(); } catch { }
            }
        }
    }

    [Xunit.Fact]
    public void ReleaseStartGates_DiagnosticPersistenceFailure_CleansFailedSampleAndContinues()
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        var artifacts = PlannerSampleDispatcher.CreateArtifacts(primaryPath, 3).ToArray();
        artifacts[0] = artifacts[0] with
        {
            StartGatePath = root,
            LaunchDiagnosticPath = root
        };
        var launches = PlannerSampleDispatcher.StartSamples(
            artifacts,
            CreateRunParameters(root, primaryPath),
            "dispatch-host.dll",
            "planner-start-gate-diagnostic-failure",
            _ => StartSleeper());
        var processIds = launches.Select(launch => launch.Process.Id).ToArray();
        var terminatedProcessIds = new List<int>();
        var diagnosticAttempts = 0;

        try
        {
            Xunit.Assert.Equal(2, launches.Count);

            Xunit.Assert.Throws<UnauthorizedAccessException>(() =>
                PlannerSampleDispatcher.ReleaseStartGates(
                    launches,
                    terminateOwned: process =>
                    {
                        terminatedProcessIds.Add(process.Id);
                        WorkerProcessJobs.TryKillOrFallback(process.Id);
                        process.Dispose();
                    },
                    writeLaunchDiagnostic: (_, _) =>
                    {
                        diagnosticAttempts++;
                        throw new UnauthorizedAccessException("fixture diagnostic persistence failure");
                    }));

            Xunit.Assert.Equal([processIds[0]], terminatedProcessIds);
            Xunit.Assert.Equal(1, diagnosticAttempts);
            Xunit.Assert.False(WorkerProcessJobs.HasRegisteredJob(processIds[0]));
            Xunit.Assert.True(File.Exists(artifacts[1].StartGatePath));
        }
        finally
        {
            foreach (var processId in processIds)
                try { WorkerProcessJobs.TryKillOrFallback(processId); } catch { }
            foreach (var launch in launches)
                try { launch.Process.Dispose(); } catch { }
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
    public void TryStartLatestDispatch_SamplePreflightFailure_TerminatesPrimaryOnce()
    {
        var root = CreateSeededDispatchRepository();
        var logRoot = Path.Combine(root, "logs");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Planner sample preflight persistence failure", [
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
            PlannerSampleCount: 2));

        Process? primaryProcess = null;
        int? primaryProcessId = null;
        string? primaryOutputPath = null;
        var killCalls = new List<int>();
        var invocation = 0;
        var runner = new BackgroundDispatchRunner(
            tryKillOwnedProcess: processId =>
            {
                killCalls.Add(processId);
                return WorkerProcessJobs.TryKillOrFallback(processId);
            },
            disableProcessStart: false,
            startProcess: startInfo =>
            {
                invocation++;
                var parameters = DispatchProcessHost.ReadParameters(startInfo.ArgumentList.Last());
                if (invocation == 1)
                {
                    primaryOutputPath = parameters.StdoutPath;
                    primaryProcess = StartSleeper();
                    primaryProcessId = primaryProcess.Id;
                    return primaryProcess;
                }

                var sample = Xunit.Assert.Single(
                    PlannerSampleDispatcher.CreateArtifacts(primaryOutputPath!, 2));
                Directory.CreateDirectory(sample.LaunchDiagnosticPath);
                return null;
            });

        try
        {
            Xunit.Assert.Throws<UnauthorizedAccessException>(() =>
                runner.TryStartLatestDispatch(kernel, goal.Id, task.Id, logRoot));

            Xunit.Assert.NotNull(primaryProcess);
            Xunit.Assert.NotNull(primaryProcessId);
            Xunit.Assert.Equal([primaryProcessId.Value], killCalls);
            Xunit.Assert.False(WorkerProcessJobs.HasRegisteredJob(primaryProcessId.Value));
        }
        finally
        {
            if (primaryProcess is not null && primaryProcessId is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(primaryProcessId.Value); } catch { }
                try { primaryProcess.Dispose(); } catch { }
            }
        }
    }

    [Xunit.Fact]
    public void TryStartLatestDispatch_PrimaryGatePersistenceFailure_TerminatesRegisteredHost()
    {
        var root = CreateSeededDispatchRepository();
        var logRoot = Path.Combine(root, "logs");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Planner primary gate persistence failure", [
            new TaskSpec(TaskId.New(), "Produce a plan.", AgentRole.Planner)
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
            PlannerSampleCount: 1));

        Process? primaryProcess = null;
        int? primaryProcessId = null;
        string? primaryStartGatePath = null;
        var killCalls = new List<int>();
        var runner = new BackgroundDispatchRunner(
            tryKillOwnedProcess: processId =>
            {
                killCalls.Add(processId);
                return WorkerProcessJobs.TryKillOrFallback(processId);
            },
            disableProcessStart: false,
            startProcess: startInfo =>
            {
                primaryStartGatePath = startInfo.Environment[DispatchProcessHost.StartGatePathVariable];
                primaryProcess = StartSleeper();
                primaryProcessId = primaryProcess.Id;
                return primaryProcess;
            });

        try
        {
            Xunit.Assert.Throws<UnauthorizedAccessException>(() =>
                runner.TryStartLatestDispatch(
                    kernel,
                    goal.Id,
                    task.Id,
                    logRoot,
                    (_, _, _, phase) =>
                    {
                        if (phase == DispatchRecordCheckpointPhase.ProcessMayHaveStarted)
                            Directory.CreateDirectory(primaryStartGatePath!);
                    }));

            Xunit.Assert.NotNull(primaryProcessId);
            Xunit.Assert.NotNull(primaryStartGatePath);
            Xunit.Assert.True(Directory.Exists(primaryStartGatePath));
            Xunit.Assert.Equal([primaryProcessId.Value], killCalls);
            Xunit.Assert.False(WorkerProcessJobs.HasRegisteredJob(primaryProcessId.Value));
        }
        finally
        {
            if (primaryProcess is not null && primaryProcessId is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(primaryProcessId.Value); } catch { }
                try { primaryProcess.Dispose(); } catch { }
            }
        }
    }

    [Xunit.Fact]
    public void TryStartLatestDispatch_SampleGatePersistenceFailure_RecordsTerminalAndSettles()
    {
        var root = CreateSeededDispatchRepository();
        var logRoot = Path.Combine(root, "logs");
        SeedFixtureCitationTargets(root);
        var startedAt = DateTimeOffset.Parse("2026-08-22T18:00:00Z");
        var clock = new MutableClock(startedAt);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Planner sample gate diagnostic failure", [
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
            startedAt,
            PlannerSampleCount: 2));

        var processes = new List<Process>();
        var processIds = new List<int>();
        string? primaryOutputPath = null;
        string? primaryStartGatePath = null;
        PlannerSampleArtifacts? failedSample = null;
        var invocation = 0;
        var runner = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => false,
            disableProcessStart: false,
            startProcess: startInfo =>
            {
                invocation++;
                var parameters = DispatchProcessHost.ReadParameters(startInfo.ArgumentList.Last());
                if (invocation == 1)
                {
                    primaryOutputPath = parameters.StdoutPath;
                    primaryStartGatePath = startInfo.Environment[DispatchProcessHost.StartGatePathVariable];
                    File.WriteAllText(parameters.StdoutPath, ReadPlannerFixture());
                    DispatchExitArtifacts.Write(
                        parameters.ExitCodePath,
                        DispatchExitArtifacts.Native(0, "fixture primary completed", startedAt.AddSeconds(1)));
                }

                var process = StartSleeper();
                processes.Add(process);
                processIds.Add(process.Id);
                return process;
            });

        try
        {
            Xunit.Assert.Throws<UnauthorizedAccessException>(() =>
                runner.TryStartLatestDispatch(
                    kernel,
                    goal.Id,
                    task.Id,
                    logRoot,
                    (_, _, _, phase) =>
                    {
                        if (phase != DispatchRecordCheckpointPhase.ProcessMayHaveStarted)
                            return;

                        failedSample = Xunit.Assert.Single(
                            PlannerSampleDispatcher.CreateArtifacts(primaryOutputPath!, 2));
                        Directory.CreateDirectory(failedSample.StartGatePath);
                        Directory.CreateDirectory(failedSample.LaunchDiagnosticPath);
                    }));

            Xunit.Assert.NotNull(task.LastProcess);
            Xunit.Assert.NotNull(primaryStartGatePath);
            Xunit.Assert.True(File.Exists(primaryStartGatePath));
            Xunit.Assert.NotNull(failedSample);
            Xunit.Assert.True(File.Exists(failedSample.TerminalRecordPath));

            clock.Advance(TimeSpan.FromMinutes(2));
            runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

            Xunit.Assert.True(task.LastVerification!.Succeeded, task.LastVerification.StandardError);
            var candidate = PlannerSampleDispatcher.CollectCandidates(
                task.LastProcess.StandardOutputPath,
                2)[1];
            Xunit.Assert.Equal(PlannerCandidateTerminalState.LaunchFailed, candidate.TerminalState);
        }
        finally
        {
            foreach (var processId in processIds)
                try { WorkerProcessJobs.TryKillOrFallback(processId); } catch { }
            foreach (var process in processes)
                try { process.Dispose(); } catch { }
        }
    }

    [Xunit.Fact]
    public void LateFinishingSampleStillReachesSelector()
    {
        var root = CreateSeededDispatchRepository();
        var logRoot = Path.Combine(root, "logs");
        SeedFixtureCitationTargets(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Planner late sample", [
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
            PlannerSampleCount: 2));

        var processes = new List<Process>();
        var startedProcessIds = new List<int>();
        DispatchProcessHost.DispatchRunParameters? sampleParameters = null;
        var invocation = 0;
        var runner = new BackgroundDispatchRunner(
            isStillRunning: processId => startedProcessIds.Skip(1).Contains(processId),
            disableProcessStart: false,
            startProcess: startInfo =>
            {
                invocation++;
                var parameters = DispatchProcessHost.ReadParameters(startInfo.ArgumentList.Last());
                if (invocation == 1)
                {
                    File.WriteAllText(parameters.StdoutPath, ReadPlannerFixture());
                    DispatchExitArtifacts.Write(
                        parameters.ExitCodePath,
                        DispatchExitArtifacts.Native(0, "fixture primary completed", DateTimeOffset.UtcNow));
                }
                else
                {
                    sampleParameters = parameters;
                }

                var process = StartSleeper();
                processes.Add(process);
                startedProcessIds.Add(process.Id);
                return process;
            });

        try
        {
            var start = runner.TryStartLatestDispatch(kernel, goal.Id, task.Id, logRoot);
            var primaryOutputBeforeRefresh = File.ReadAllText(start.ProcessRecord!.StandardOutputPath);

            runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

            Xunit.Assert.Null(task.LastVerification);
            Xunit.Assert.Equal(primaryOutputBeforeRefresh, File.ReadAllText(start.ProcessRecord.StandardOutputPath));

            Xunit.Assert.NotNull(sampleParameters);
            File.WriteAllText(
                sampleParameters.StdoutPath,
                ReadPlannerFixture() + Environment.NewLine + "<!-- late sample output -->");
            DispatchExitArtifacts.Write(
                sampleParameters.ExitCodePath,
                DispatchExitArtifacts.Native(0, "fixture sample completed", DateTimeOffset.UtcNow));

            runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

            var divergence = Xunit.Assert.IsType<PlannerCandidateDivergenceReceipt>(
                task.LastVerification!.PlannerCandidateDivergence);
            Xunit.Assert.True(divergence.CandidateScores[1] > 0);
            var sampleEvidence = Xunit.Assert.Single(
                divergence.Candidates!,
                candidate => candidate.CandidateIndex == 1);
            Xunit.Assert.Equal(PlannerCandidateTerminalState.Succeeded, sampleEvidence.TerminalState);
            Xunit.Assert.Equal(PlannerCandidateContractVerdict.Valid, sampleEvidence.ContractVerdict);
            Xunit.Assert.Equal(64, sampleEvidence.ArtifactSha256.Length);
            var lateCandidate = PlannerSampleDispatcher.CollectCandidates(
                start.ProcessRecord.StandardOutputPath,
                2)[1];
            Xunit.Assert.NotEmpty(lateCandidate.StandardOutput);
            Xunit.Assert.Contains("late sample output", lateCandidate.StandardOutput, StringComparison.Ordinal);
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
    public void UnresolvedSampleTimesOutAndPrimarySucceeds()
    {
        var root = CreateSeededDispatchRepository();
        var logRoot = Path.Combine(root, "logs");
        SeedFixtureCitationTargets(root);
        var startedAt = DateTimeOffset.Parse("2026-08-22T18:00:00Z");
        var clock = new MutableClock(startedAt);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Planner sample timeout", [
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
            startedAt,
            PlannerSampleCount: 2));

        var processes = new List<Process>();
        var startedProcessIds = new List<int>();
        var killedProcessIds = new List<int>();
        DispatchProcessHost.DispatchRunParameters? sampleParameters = null;
        var invocation = 0;
        var runner = new BackgroundDispatchRunner(
            clock,
            isStillRunning: processId => startedProcessIds.Skip(1).Contains(processId),
            tryKillOwnedProcess: processId =>
            {
                killedProcessIds.Add(processId);
                return true;
            },
            disableProcessStart: false,
            startProcess: startInfo =>
            {
                invocation++;
                var parameters = DispatchProcessHost.ReadParameters(startInfo.ArgumentList.Last());
                if (invocation == 1)
                {
                    File.WriteAllText(parameters.StdoutPath, ReadPlannerFixture());
                    DispatchExitArtifacts.Write(
                        parameters.ExitCodePath,
                        DispatchExitArtifacts.Native(
                            0,
                            "fixture primary completed",
                            startedAt.AddMinutes(10)));
                }
                else
                {
                    sampleParameters = parameters;
                }

                var process = StartSleeper();
                processes.Add(process);
                startedProcessIds.Add(process.Id);
                return process;
            });

        try
        {
            var start = runner.TryStartLatestDispatch(kernel, goal.Id, task.Id, logRoot);
            clock.Advance(TimeSpan.FromMinutes(21));

            runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

            Xunit.Assert.True(task.LastVerification!.Succeeded, task.LastVerification.StandardError);
            Xunit.Assert.Contains(startedProcessIds[1], killedProcessIds);
            Xunit.Assert.NotNull(sampleParameters);
            var timeoutDiagnostic = File.ReadAllText(
                PlannerSampleDispatcher.CreateArtifacts(start.ProcessRecord!.StandardOutputPath, 2)[0]
                    .LaunchDiagnosticPath);
            Xunit.Assert.Contains("timed out", timeoutDiagnostic, StringComparison.OrdinalIgnoreCase);
            var sampleCandidate = PlannerSampleDispatcher.CollectCandidates(
                start.ProcessRecord.StandardOutputPath,
                2)[1];
            Xunit.Assert.Empty(sampleCandidate.StandardOutput);
            Xunit.Assert.Contains("timed out", sampleCandidate.StandardError, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.Equal(PlannerCandidateTerminalState.TimedOut, sampleCandidate.TerminalState);
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
    public void SweepExitedProcesses_TimeoutReceiptFailuresDoNotBlockAnyGoal()
    {
        var root = CreateSeededDispatchRepository();
        SeedFixtureCitationTargets(root);
        var startedAt = DateTimeOffset.Parse("2026-08-22T18:00:00Z");
        var kernel = new AgentOrchestratorKernel();
        var planner = new AgentDefinition(
            new AgentId("planner"),
            "Planner",
            AgentRole.Planner,
            new ModelProfile(
                "OpenAI",
                AgentCatalog.OpenAiSubscriptionModelAlias,
                ModelCapability.Text,
                SubscriptionMode.ApiKey));

        (Goal Goal, TaskSpec Task, string OutputPath) CreateCompletedPlanner(
            string objective,
            int processId,
            int sampleCount)
        {
            var goal = kernel.CreateGoal(objective, [
                new TaskSpec(TaskId.New(), "Produce a plan.", AgentRole.Planner)
            ]);
            kernel.ActivateGoal(goal.Id, [planner]);
            var task = goal.Tasks.Single();
            var outputPath = Path.Combine(root, $"planner-{processId}.out.log");
            var errorPath = Path.Combine(root, $"planner-{processId}.err.log");
            var exitPath = Path.Combine(root, $"planner-{processId}.exit.txt");
            File.WriteAllText(outputPath, ReadPlannerFixture());
            DispatchExitArtifacts.Write(
                exitPath,
                DispatchExitArtifacts.Native(0, "fixture completed", startedAt.AddMinutes(10)));
            var dispatch = new TaskDispatchRecord(
                "planner",
                "fixture planner command",
                root,
                startedAt,
                PlannerSampleCount: sampleCount);
            kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(
                    processId,
                    dispatch.Command,
                    root,
                    outputPath,
                    errorPath,
                    exitPath,
                    startedAt,
                    null,
                    null,
                    OwnedProcessIds: [processId]));
            return (goal, task, outputPath);
        }

        var blockedReceiptGoal = CreateCompletedPlanner("Planner timeout receipt failure", 401, 2);
        var unaffectedGoal = CreateCompletedPlanner("Independent completed Planner", 402, 1);
        var sample = Xunit.Assert.Single(
            PlannerSampleDispatcher.CreateArtifacts(blockedReceiptGoal.OutputPath, 2));
        Directory.CreateDirectory(sample.TerminalRecordPath);
        Directory.CreateDirectory(sample.LaunchDiagnosticPath);
        var runner = new BackgroundDispatchRunner(
            new TestClock(startedAt.AddMinutes(21)),
            isStillRunning: _ => false,
            tryKillOwnedProcess: _ => true);

        var reconciled = runner.SweepExitedProcesses(kernel);

        Xunit.Assert.Equal(2, reconciled);
        Xunit.Assert.True(blockedReceiptGoal.Task.LastVerification!.Succeeded);
        Xunit.Assert.True(unaffectedGoal.Task.LastVerification!.Succeeded);
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

    private static string ToCodexJsonl(
        string workerOutput,
        long inputTokens,
        long cachedInputTokens,
        long outputTokens) =>
        JsonSerializer.Serialize(new
        {
            type = "item.completed",
            item = new { type = "agent_message", text = workerOutput }
        }) + Environment.NewLine + JsonSerializer.Serialize(new
        {
            type = "turn.completed",
            usage = new
            {
                input_tokens = inputTokens,
                cached_input_tokens = cachedInputTokens,
                output_tokens = outputTokens
            }
        });

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

    private static TaskDispatchRecord CreateCodexDispatch(string root) => new(
        "planner",
        "codex exec --json",
        root,
        DateTimeOffset.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli,
        PlannerSampleCount: 2);

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

    private static void AssertInvalidCodexSampleState(
        string sampleOutput,
        PlannerCandidateNormalizationState expectedState)
    {
        var root = CreateTempDirectory();
        var primaryPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(primaryPath, ToCodexJsonl(ReadPlannerFixture(), 11, 7, 3));
        var sample = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(primaryPath, 2));
        File.WriteAllText(sample.StandardOutputPath, sampleOutput);
        DispatchExitArtifacts.Write(
            sample.ExitCodePath,
            DispatchExitArtifacts.Native(0, "fixture completed", DateTimeOffset.UtcNow));
        var dispatch = new TaskDispatchRecord(
            "planner",
            "codex exec --json",
            root,
            DateTimeOffset.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli,
            PlannerSampleCount: 2);

        var candidates = PlannerSampleDispatcher.CollectCandidates(primaryPath, 2, dispatch);
        var result = PlannerCandidateSelector.Select(candidates, InfrastructureTestSupport.FindRepositoryRoot());

        var secondary = candidates[1];
        Xunit.Assert.Equal(PlannerCandidateTerminalState.Succeeded, secondary.TerminalState);
        Xunit.Assert.Equal(expectedState, secondary.NormalizationState);
        var evidence = Xunit.Assert.Single(result.Receipt.Candidates!, candidate => candidate.CandidateIndex == 1);
        Xunit.Assert.Equal(PlannerCandidateTerminalState.Succeeded, evidence.TerminalState);
        Xunit.Assert.Equal(expectedState, evidence.NormalizationState);
        Xunit.Assert.Equal(PlannerCandidateContractVerdict.NotEvaluated, evidence.ContractVerdict);
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
