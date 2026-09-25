using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLoopSchedulingPolicy : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsLoopSchedulingPolicy(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact(DisplayName = "ConductorBatchLoop_reason_sanitizer_preserves_dirty_worktree_path")]
    public void ReasonSanitizerPreservesDirtyWorktreePath()
    {
        var reason = "No tasks dispatched; reason=dirty-worktree paths=[src/nested/dirty.cs]";

        var sanitized = ConductorBatchLoop.SanitizeReason(reason);

        Xunit.Assert.Contains("dirty-worktree", sanitized, StringComparison.Ordinal);
        Xunit.Assert.Contains("src/nested/dirty.cs", sanitized, StringComparison.Ordinal);
        Xunit.Assert.True(sanitized.Length > 40);
    }

    [Xunit.Theory(DisplayName = "BatchLoop_self_relaunch_activation_switch_defaults_on_and_only_false_disables")]
    [Xunit.InlineData(null, true)]
    [Xunit.InlineData("", true)]
    [Xunit.InlineData("false", false)]
    [Xunit.InlineData("0", false)]
    [Xunit.InlineData("1", true)]
    [Xunit.InlineData("true", true)]
    public void BatchLoopSelfRelaunchActivationSwitchDefaultsOnAndOnlyFalseDisables(
        string? configuredValue,
        bool expected)
    {
        Assert.True(ConductorBatchLoop.DefaultSelfRelaunchEnabled);
        Assert.Equal(expected, ConductorBatchLoop.ResolveSelfRelaunchEnabled(configuredValue));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_loop_start_reports_policy_worker_caps_and_verified_journal_mode")]
    public void BatchLoopLoopStartReportsPolicyWorkerCapsAndVerifiedJournalMode()
    {
        var (kernel, _) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            Name = "OperatorTuned",
            MaxConcurrentPaidWorkers = ConductorBatchLoop.WorkerAdmissionCapacity + 2
        };
        var driver = MakeDriver(hasGateReadyGoal: () => true);
        var workerAdmission = driver.GetWorkerAdmissionSnapshot(policy);
        var output = CaptureConsole(() =>
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                policy,
                NoStopPath(),
                maxIterations: 0,
                journalMode: "wal"));

        Assert.True(workerAdmission.EffectiveWorkerCap < workerAdmission.ConfiguredWorkerCap);
        Assert.Equal(ConductorBatchLoop.WorkerAdmissionCapacity - 1, workerAdmission.EffectiveWorkerCap);
        Assert.Contains("LOOP_START policy=OperatorTuned", output, StringComparison.Ordinal);
        Assert.Contains("policySource=preset", output, StringComparison.Ordinal);
        Assert.Contains("configuredWorkerCap=11", output, StringComparison.Ordinal);
        Assert.Contains("workerAdmissionCapacity=9", output, StringComparison.Ordinal);
        Assert.Contains("reservedGateSlots=1", output, StringComparison.Ordinal);
        Assert.Contains("effectiveWorkerCap=8", output, StringComparison.Ordinal);
        Assert.Contains("journalMode=wal", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_loop_start_truncates_compact_journal_mode_token")]
    public void BatchLoopLoopStartTruncatesCompactJournalModeToken()
    {
        const string journalMode = "0123456789012345678901234567890123456789DISTINCTIVE_TAIL";
        var (kernel, _) = SimpleGoal();
        var output = CaptureConsole(() =>
            new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(hasGateReadyGoal: () => true),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 0,
                journalMode: journalMode));

        Assert.Contains($"journalMode={journalMode[..40]}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("DISTINCTIVE_TAIL", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorBatchLoop_continues_ticking_while_goal_refinement_is_in_flight")]
    public async Task ConductorBatchLoopContinuesTickingWhileGoalRefinementIsInFlight()
    {
        var root = CreateTempDirectory("mcg-concurrent-goal-intake");
        var stopPath = Path.Combine(root, "stop");
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
                new ModelFunctionBinding(
                    ModelFunctionPurposes.SpecRefiner,
                    ModelLane.CheapApi,
                    new ModelProfile("loop-blocking-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                    Name: ModelFunctionPurposes.SpecRefiner)
            ]));
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var (loopKernel, loopGoal) = SimpleGoal("Keep ticking during concurrent goal intake");
            await repository.SaveAsync(loopKernel);

            var refiner = new LoopBlockingGoalRefinerProvider();
            IReadOnlyList<AgentDefinition> agents = DefaultAgents();
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var intakeTask = Task.Run(() => CliPersistentStateRunner.ExecuteCommand(
                ["goal", "Create a goal while the conductor remains live"],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([refiner]),
                ref profiles,
                ref currentGoal));

            await intakeTask.WaitAsync(TimeSpan.FromSeconds(2));
            Xunit.Assert.NotNull(currentGoal);
            Xunit.Assert.False(refiner.Entered.IsSet);
            var refinementMessage = Xunit.Assert.Single(
                await repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
            Xunit.Assert.Equal(GoalRefinementWorkCoordinator.MessageId(currentGoal!.Id), refinementMessage.Id);

            using var refinementDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var refinementTask = Task.Run(() => GoalRefinementWorkCoordinator.ProcessAsync(
                repository,
                workspace,
                new InMemoryModelProviderRegistry([refiner]),
                profiles,
                currentGoal.Id,
                refinementDeadline.Token));

            var tickCount = 0;
            string loopOutput = string.Empty;
            var loopTask = Task.Run(() =>
                loopOutput = AsyncLocalConsoleRouter.Capture(() =>
                    new ConductorBatchLoop().Run(
                        loopKernel,
                        MakeDriver(),
                        ConductorAutonomyPolicy.Conservative,
                        stopPath,
                        maxIterations: 2,
                        onTick: _ => Interlocked.Increment(ref tickCount),
                        onlyGoalId: loopGoal.Id.Value,
                        persistTick: currentKernel =>
                            repository.TransactAsync(
                                    (stored, _) =>
                                    {
                                        stored.ReplaceGoalWithSnapshot(currentKernel.ExportGoalSnapshot(loopGoal.Id));
                                        return Task.FromResult((true, true));
                                    })
                                .GetAwaiter()
                                .GetResult())));

            try
            {
                Xunit.Assert.True(
                    refiner.Entered.Wait(TimeSpan.FromSeconds(15)),
                    "Durable refinement work did not start.");
                await loopTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch
            {
                refinementDeadline.Cancel();
                throw;
            }
            finally
            {
                refiner.Release.Set();
            }

            var refinementResult = await refinementTask.WaitAsync(TimeSpan.FromSeconds(15));
            Xunit.Assert.True(refinementResult.Attached);
            Assert.True(tickCount >= 2, $"Expected at least two completed ticks, observed {tickCount}.");
            Assert.DoesNotContain("TICK_WRITE_BUSY", loopOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("sqlite-busy-retry-exhausted", loopOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_STOP reason=unintended-exit", loopOutput, StringComparison.Ordinal);
            Assert.Equal(2, (await repository.LoadAsync()).Goals.Count);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private sealed class LoopBlockingGoalRefinerProvider : IModelProvider
    {
        public string ProviderName => "loop-blocking-refiner";
        public ManualResetEventSlim Entered { get; } = new(initialState: false);
        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Entered.Set();
            Release.Wait(cancellationToken);
            const string response = """
                ```json
                {
                  "behavioralContract": "Create the goal without blocking the conductor.",
                  "acceptanceCriteria": ["The conductor completes a later tick."],
                  "verificationClass": "TestVerifiable",
                  "decisions": [],
                  "forks": []
                }
                ```
                """;
            return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
        }
    }

    [Xunit.Fact(DisplayName = "Conductor_policy_resolution_preserves_file_values_and_reports_explicit_preset_divergence")]
    public void ConductorPolicyResolutionPreservesFileValuesAndReportsExplicitPresetDivergence()
    {
        var root = CreateTempDirectory("mcg-conductor-policy-resolution");
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator", "projects", "scoped-project");
            var absent = CliCommandHandlers.ResolveConductorPolicy(null, orchestratorDirectory);
            Assert.Same(ConductorAutonomyPolicy.Default, absent.Policy);
            Assert.Equal("default", absent.Source);

            Directory.CreateDirectory(orchestratorDirectory);
            var policyPath = Path.Combine(orchestratorDirectory, "conductor-policy.json");
            var configured = ConductorAutonomyPolicy.Conservative with
            {
                Name = "OperatorTuned",
                MaxConcurrentPaidWorkers = 8,
                MaxCriterionRetries = 3,
                AutoPromoteRiskThreshold = ChangeRiskTier.Security,
                MaxEmptyOutputDispatchRetries = 9,
                MaxEmptyOutputAutoRecoverCycles = 4,
                EmptyOutputRetryInitialDelaySeconds = 1,
                EmptyOutputRetryBackoffMultiplier = 3,
                EmptyOutputRetryMaxDelaySeconds = 40,
                ReviewAutoRetryWarningRound = 5,
                ReviewAutoRetryStopRound = 8,
                PlannerSampleCount = 2,
                TransitionMap = ConductorAutonomyPolicy.Conservative.TransitionMap
            };
            File.WriteAllText(policyPath, configured.ToJson());

            var fromFile = CliCommandHandlers.ResolveConductorPolicy(null, orchestratorDirectory);
            Assert.Equal(configured.Name, fromFile.Policy.Name);
            Assert.Equal(configured.MaxConcurrentPaidWorkers, fromFile.Policy.MaxConcurrentPaidWorkers);
            Assert.Equal(configured.MaxCriterionRetries, fromFile.Policy.MaxCriterionRetries);
            Assert.Equal(configured.AutoPromoteRiskThreshold, fromFile.Policy.AutoPromoteRiskThreshold);
            Assert.Equal(configured.MaxEmptyOutputDispatchRetries, fromFile.Policy.MaxEmptyOutputDispatchRetries);
            Assert.Equal(configured.MaxEmptyOutputAutoRecoverCycles, fromFile.Policy.MaxEmptyOutputAutoRecoverCycles);
            Assert.Equal(configured.EmptyOutputRetryInitialDelaySeconds, fromFile.Policy.EmptyOutputRetryInitialDelaySeconds);
            Assert.Equal(configured.EmptyOutputRetryBackoffMultiplier, fromFile.Policy.EmptyOutputRetryBackoffMultiplier);
            Assert.Equal(configured.EmptyOutputRetryMaxDelaySeconds, fromFile.Policy.EmptyOutputRetryMaxDelaySeconds);
            Assert.Equal(configured.ReviewAutoRetryWarningRound, fromFile.Policy.ReviewAutoRetryWarningRound);
            Assert.Equal(configured.ReviewAutoRetryStopRound, fromFile.Policy.ReviewAutoRetryStopRound);
            Assert.Equal(configured.PlannerSampleCount, fromFile.Policy.PlannerSampleCount);
            Assert.Equal(configured.TransitionMap, fromFile.Policy.TransitionMap);
            Assert.Equal($"file:{Path.GetFullPath(policyPath)}", fromFile.Source);
            Assert.Contains(fromFile.Warnings, warning =>
                warning.Contains("above the highest preset value 5", StringComparison.Ordinal));

            var explicitPreset = CliCommandHandlers.ResolveConductorPolicy("Permissive", orchestratorDirectory);
            Assert.Same(ConductorAutonomyPolicy.Permissive, explicitPreset.Policy);
            Assert.Equal("preset", explicitPreset.Source);
            var divergenceWarning = Assert.Single(explicitPreset.Warnings);
            Assert.Contains(Path.GetFullPath(policyPath), divergenceWarning, StringComparison.Ordinal);
            Assert.Contains("--policy Permissive", divergenceWarning, StringComparison.Ordinal);
            Assert.Contains("OperatorTuned", divergenceWarning, StringComparison.Ordinal);
            Assert.Contains("MaxConcurrentPaidWorkers: preset=5 file=8", divergenceWarning, StringComparison.Ordinal);
            Assert.Contains("MaxCriterionRetries: preset=2 file=3", divergenceWarning, StringComparison.Ordinal);
            Assert.Contains("TransitionMap[AwaitingClarification]: preset=Auto file=Escalate", divergenceWarning, StringComparison.Ordinal);

            var (_, goal) = SimpleGoal();
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/PresetPolicyTransport.cs"],
                "branch-preset-policy",
                "main-preset-policy");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7000));
            var started = coordinator.Evaluate(
                candidate,
                explicitPreset.Policy,
                (_, _, _, _, _) => throw new InvalidOperationException("The owned-process stub must not run acceptance inline."));
            var roundTripped = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(started.Attempt.MetadataPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(roundTripped);
            Assert.Equal(
                explicitPreset.Policy.ToJson(),
                ConductorParallelAcceptanceAttemptCoordinator.ResolveAttemptPolicy(roundTripped).ToJson());

            var unknown = Assert.Throws<InvalidOperationException>(() =>
                CliCommandHandlers.ResolveConductorPolicy("UnknownName", orchestratorDirectory));
            Assert.Equal(
                "Unknown conductor policy 'UnknownName'. Valid: Conservative, Permissive, Manual",
                unknown.Message);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "Acceptance_owned_process_resolves_the_transported_conductor_policy_snapshot")]
    public void AcceptanceOwnedProcessResolvesTheTransportedConductorPolicySnapshot()
    {
        var attemptRoot = CreateTempDirectory("mcg-acceptance-policy-transport");
        try
        {
            var (_, goal) = SimpleGoal();
            var configured = ConductorAutonomyPolicy.Conservative with
            {
                Name = "OperatorTuned",
                MaxConcurrentPaidWorkers = 6,
                MaxCriterionRetries = 3,
                AutoPromoteRiskThreshold = ChangeRiskTier.Broad,
                MaxEmptyOutputDispatchRetries = 9,
                MaxEmptyOutputAutoRecoverCycles = 4,
                EmptyOutputRetryInitialDelaySeconds = 1,
                EmptyOutputRetryBackoffMultiplier = 3,
                EmptyOutputRetryMaxDelaySeconds = 40,
                ReviewAutoRetryWarningRound = 5,
                ReviewAutoRetryStopRound = 8,
                PlannerSampleCount = 2,
                TransitionMap = ConductorAutonomyPolicy.Permissive.TransitionMap
            };
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/PolicyTransport.cs"],
                "branch-policy",
                "main-policy");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7001));

            var started = coordinator.Evaluate(
                candidate,
                configured,
                (_, _, _, _, _) => throw new InvalidOperationException("The owned-process stub must not run acceptance inline."));
            var roundTripped = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(started.Attempt.MetadataPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.NotNull(roundTripped);
            Assert.Equal(configured.ToJson(), roundTripped.PolicyJson);
            var resolved = ConductorParallelAcceptanceAttemptCoordinator.ResolveAttemptPolicy(roundTripped);
            Assert.Equal(configured.Name, resolved.Name);
            Assert.Equal(configured.MaxConcurrentPaidWorkers, resolved.MaxConcurrentPaidWorkers);
            Assert.Equal(configured.MaxCriterionRetries, resolved.MaxCriterionRetries);
            Assert.Equal(configured.AutoPromoteRiskThreshold, resolved.AutoPromoteRiskThreshold);
            Assert.Equal(configured.MaxEmptyOutputDispatchRetries, resolved.MaxEmptyOutputDispatchRetries);
            Assert.Equal(configured.MaxEmptyOutputAutoRecoverCycles, resolved.MaxEmptyOutputAutoRecoverCycles);
            Assert.Equal(configured.EmptyOutputRetryInitialDelaySeconds, resolved.EmptyOutputRetryInitialDelaySeconds);
            Assert.Equal(configured.EmptyOutputRetryBackoffMultiplier, resolved.EmptyOutputRetryBackoffMultiplier);
            Assert.Equal(configured.EmptyOutputRetryMaxDelaySeconds, resolved.EmptyOutputRetryMaxDelaySeconds);
            Assert.Equal(configured.ReviewAutoRetryWarningRound, resolved.ReviewAutoRetryWarningRound);
            Assert.Equal(configured.ReviewAutoRetryStopRound, resolved.ReviewAutoRetryStopRound);
            Assert.Equal(configured.PlannerSampleCount, resolved.PlannerSampleCount);
            Assert.Equal(configured.TransitionMap, resolved.TransitionMap);

            Assert.Same(
                ConductorAutonomyPolicy.Permissive,
                ConductorParallelAcceptanceAttemptCoordinator.ResolveAttemptPolicy(
                    roundTripped with { PolicyName = "Permissive", PolicyJson = "{ damaged" }));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "Conductor_policy_resolution_prints_worker_admission_clamp_warning")]
    public void ConductorPolicyResolutionPrintsWorkerAdmissionClampWarning()
    {
        var root = CreateTempDirectory("mcg-conductor-policy-cap-warning");
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            var policyPath = Path.Combine(orchestratorDirectory, "conductor-policy.json");
            var configured = ConductorAutonomyPolicy.Conservative with
            {
                Name = "AboveAdmissionCapacity",
                MaxConcurrentPaidWorkers = ConductorBatchLoop.WorkerAdmissionCapacity + 1
            };
            File.WriteAllText(policyPath, configured.ToJson());

            var resolution = CliCommandHandlers.ResolveConductorPolicy(null, orchestratorDirectory);
            var output = CaptureConsole(() => CliCommandHandlers.PrintConductorPolicyWarnings(resolution));

            Assert.Contains(resolution.Warnings, warning =>
                warning.Contains("above worker admission capacity 9", StringComparison.Ordinal) &&
                warning.Contains("clamped to 9 normally and 8 while a gate-ready goal", StringComparison.Ordinal));
            Assert.Contains("[conduct] WARNING:", output, StringComparison.Ordinal);
            Assert.Contains(
                "clamped_to_9_normally_and_8_while_a_gate-ready_goal",
                output.Replace(' ', '_'),
                StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "Conductor_policy_resolution_fails_for_malformed_effective_file_but_preset_warns_and_wins")]
    public void ConductorPolicyResolutionFailsForMalformedEffectiveFileButPresetWarnsAndWins()
    {
        var root = CreateTempDirectory("mcg-conductor-policy-malformed");
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            var policyPath = Path.GetFullPath(Path.Combine(orchestratorDirectory, "conductor-policy.json"));
            File.WriteAllText(policyPath, "{ not-json");

            var failure = Assert.Throws<FormatException>(() =>
                CliCommandHandlers.ResolveConductorPolicy(null, orchestratorDirectory));
            Assert.Contains(policyPath, failure.Message, StringComparison.Ordinal);

            var explicitPreset = CliCommandHandlers.ResolveConductorPolicy("Permissive", orchestratorDirectory);
            Assert.Same(ConductorAutonomyPolicy.Permissive, explicitPreset.Policy);
            var warning = Assert.Single(explicitPreset.Warnings);
            Assert.Contains(policyPath, warning, StringComparison.Ordinal);
            Assert.Contains("not valid JSON", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("disagrees", warning, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_reloads_file_policy_at_tick_boundary_and_applies_new_worker_cap")]
    public void BatchLoopReloadsFilePolicyAtTickBoundaryAndAppliesNewWorkerCap()
    {
        var root = CreateTempDirectory("mcg-conductor-policy-reload");
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            var policyPath = Path.Combine(orchestratorDirectory, "conductor-policy.json");
            var initialPolicy = ConductorAutonomyPolicy.Conservative with
            {
                Name = "Reloadable",
                MaxConcurrentPaidWorkers = 1
            };
            File.WriteAllText(policyPath, initialPolicy.ToJson());
            var initialResolution = CliCommandHandlers.ResolveConductorPolicy(null, orchestratorDirectory);
            var eventLogPath = Path.Combine(orchestratorDirectory, "logs", ConductEventLogWriter.CurrentFileName);
            var (kernel, _) = SimpleGoal();
            var dispatches = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => 1,
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                });
            var sleeps = 0;

            var output = CaptureConsole(() => new ConductorBatchLoop(
                conductEventLogWriter: new ConductEventLogWriter(eventLogPath)).Run(
                kernel,
                driver,
                initialResolution.Policy,
                NoStopPath(),
                maxIterations: 2,
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ =>
                {
                    sleeps++;
                    File.WriteAllText(policyPath, (initialPolicy with { MaxConcurrentPaidWorkers = 8 }).ToJson());
                    return false;
                },
                policySource: initialResolution.Source,
                reloadPolicy: () => CliCommandHandlers.ResolveConductorPolicy(null, orchestratorDirectory)));

            Assert.Equal(1, sleeps);
            Assert.Equal(1, dispatches);
            Assert.Contains("At worker cap (1/1)", output, StringComparison.Ordinal);
            Assert.Contains("POLICY_RELOAD tick=2", output, StringComparison.Ordinal);
            Assert.Contains("oldMaxConcurrentPaidWorkers=1", output, StringComparison.Ordinal);
            Assert.Contains("newMaxConcurrentPaidWorkers=8", output, StringComparison.Ordinal);
            Assert.DoesNotContain("MaxTotalBudget", output, StringComparison.Ordinal);
            Assert.DoesNotContain("PerProviderBudgetCaps", output, StringComparison.Ordinal);
            var events = File.ReadAllText(eventLogPath);
            Assert.Contains("\"eventKind\":\"policy-warning\"", events, StringComparison.Ordinal);
            Assert.Contains("\"eventKind\":\"policy-reload\"", events, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_keeps_current_policy_when_midflight_reload_is_malformed")]
    public void BatchLoopKeepsCurrentPolicyWhenMidflightReloadIsMalformed()
    {
        var root = CreateTempDirectory("mcg-conductor-policy-reload-malformed");
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            var policyPath = Path.GetFullPath(Path.Combine(orchestratorDirectory, "conductor-policy.json"));
            var initialPolicy = ConductorAutonomyPolicy.Conservative with
            {
                Name = "Reloadable",
                MaxConcurrentPaidWorkers = 1
            };
            File.WriteAllText(policyPath, initialPolicy.ToJson());
            var initialResolution = CliCommandHandlers.ResolveConductorPolicy(null, orchestratorDirectory);
            var eventLogPath = Path.Combine(orchestratorDirectory, "logs", ConductEventLogWriter.CurrentFileName);
            var (kernel, _) = SimpleGoal();
            var dispatches = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => 1,
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                });
            var sleeps = 0;

            var output = CaptureConsole(() => new ConductorBatchLoop(
                conductEventLogWriter: new ConductEventLogWriter(eventLogPath)).Run(
                kernel,
                driver,
                initialResolution.Policy,
                NoStopPath(),
                maxIterations: 3,
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ =>
                {
                    sleeps++;
                    File.WriteAllText(
                        policyPath,
                        sleeps == 1
                            ? "{ not-json"
                            : (initialPolicy with { MaxConcurrentPaidWorkers = 2 }).ToJson());
                    return false;
                },
                policySource: initialResolution.Source,
                reloadPolicy: () => CliCommandHandlers.ResolveConductorPolicy(null, orchestratorDirectory)));

            Assert.Equal(2, sleeps);
            Assert.Equal(1, dispatches);
            Assert.Contains("POLICY_RELOAD_FAILED tick=2", output, StringComparison.Ordinal);
            Assert.Contains(policyPath.Replace(' ', '_'), output, StringComparison.Ordinal);
            Assert.Contains("POLICY_RELOAD tick=3", output, StringComparison.Ordinal);
            Assert.Contains(
                "\"eventKind\":\"policy-reload-failed\"",
                File.ReadAllText(eventLogPath),
                StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop keeps early-stage goals schedulable while post-landing canary is Pending")]
    public async Task BatchLoopSchedulesEarlyGoalsWhileCanaryRunsInBackground()
    {
        var root = CreateTempDirectory("mcg-conductor-canary-background");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var engineGoal = CreateVerifiedSimpleGoal(kernel, "Change acceptance engine");
            var earlyGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                DefaultAgents(),
                "Independent early-stage work");
            var landed = false;
            var earlyWorkspaceCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCanary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dbPath = Path.Combine(root, "run-events.db");
            var rawStore = new SqliteRunEventStore(dbPath);
            var events = new PostLandingCanaryEventStore(rawStore, dbPath);
            var circuit = new AcceptanceEngineCircuitBreaker(events);
            var coordinator = new PostLandingCanaryCoordinator(
                new PostLandingCanaryConfiguration(true, 10, []),
                new PostLandingCanaryRunner(
                    root,
                    runOverride: async (_, cancellationToken) =>
                    {
                        canaryStarted.TrySetResult();
                        await releaseCanary.Task.WaitAsync(cancellationToken);
                        return PostLandingCanaryOutcome.Passed(1, "background pass");
                    }),
                events,
                circuit);
            var driver = MakeDriver(
                getFacts: goal =>
                    goal.Id == engineGoal.Id
                        ? landed
                            ? new GoalLifecycleFacts(
                                WorkspaceExists: true,
                                IsMerged: true,
                                IsRecorded: true,
                                IsCleanedUp: true)
                            : new GoalLifecycleFacts(WorkspaceExists: true)
                        : GoalLifecycleFacts.None,
                createWorkspace: goal =>
                {
                    if (goal.Id == earlyGoal.Id)
                    {
                        earlyWorkspaceCreated.TrySetResult();
                    }

                    return "/tmp/workspace";
                },
                land: candidate =>
                {
                    landed = true;
                    return new LandingResult(
                        candidate.Id.Value,
                        candidate.Id.Value[..8],
                        new LandingDecision.Promote(),
                        "integration",
                        true,
                        "Landed",
                        "sha-background-loop");
                },
                getLandingFileScopes: goal =>
                    goal.Id == engineGoal.Id
                        ? ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/TestCoverageInvariant.cs"]
                        : []);
            var loop = new ConductorBatchLoop(postLandingCanary: coordinator);

            var run = Task.Run(() => loop.Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1));

            await canaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await earlyWorkspaceCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
            Assert.Equal(AcceptanceEngineHealth.Pending, circuit.Read().Health);

            releaseCanary.TrySetResult();
            var summary = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, summary.Advanced);
            Assert.Equal(AcceptanceEngineHealth.Healthy, circuit.Read().Health);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    // ── Loop scheduling: loop advances while progress, stops when held ────

    [Xunit.Fact(DisplayName = "BatchLoop_goal_advances_then_stops_when_held")]
    public void BatchLoop_GoalAdvancesThenStopsWhenHeld()
    {
        var (kernel, _) = SimpleGoal();
        var advanceCalls = 0;

        // getFacts controls state: first call → Created (no workspace), subsequent → WorkspaceReady
        // getRunningCount escalates to cap on 3rd advance, causing Held
        var driver = MakeDriver(
            getFacts: _ => advanceCalls < 1 ? GoalLifecycleFacts.None : new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => advanceCalls >= 2 ? ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers : 0,
            createWorkspace: _ => { advanceCalls++; return "/tmp/ws"; },
            dispatchAndStart: _ => { advanceCalls++; return DispatchStartOutcome.Started(); });

        var stopFile = NoStopPath();
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 5);

        // Tick 1: Created → workspace created (Executed), advanceCalls=1
        // Tick 2: WorkspaceReady, runningCount<cap → dispatch (Executed), advanceCalls=2
        // Tick 3: WorkspaceReady, runningCount≥cap → Held → loop stops
        Assert.Equal(3, summary.Ticks);
        Assert.Equal(2, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.False(summary.StopRequested);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_active_goal_advances_alongside_many_completed_goals")]
    public void BatchLoopActiveGoalAdvancesAlongsideManyCompletedGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        var completedGoalIds = new HashSet<GoalId>();
        for (var i = 0; i < 100; i++)
        {
            var completed = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), $"completed goal {i}");
            PassVerification(kernel, completed, completed.Tasks.Single());
            kernel.CompleteGoal(completed.Id, "Test fixture: historical goal already landed, recorded, and cleaned up.");
            completedGoalIds.Add(completed.Id);
        }

        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active conductor goal");
        var createdWorkspaces = new List<GoalId>();
        var driver = MakeDriver(
            getFacts: goal => completedGoalIds.Contains(goal.Id)
                ? new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : GoalLifecycleFacts.None,
            createWorkspace: goal =>
            {
                createdWorkspaces.Add(goal.Id);
                return "/tmp/workspace";
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, summary.Advanced);
        Assert.Contains(active.Id, createdWorkspaces);
        Assert.DoesNotContain(createdWorkspaces, completedGoalIds.Contains);
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_skips_unchanged_terminal_goals_and_reports_cache_hits")]
    public void TerminalGoalSweepSkipsUnchangedTerminalGoalsAndReportsCacheHits()
    {
        var root = CreateTempDirectory("mcg-terminal-sweep-cache");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical cancelled goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal and already clean.");
            var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active conductor goal");
            var cache = new TerminalGoalSweepCache();
            BatchTickSummary? secondTick = null;

            var driver = MakeDriver(
                getFacts: goal => goal.Id == active.Id && goal.Tasks.Single().LastDispatch is not null
                    ? new GoalLifecycleFacts(WorkspaceExists: true)
                    : GoalLifecycleFacts.None,
                createWorkspace: _ => "/tmp/workspace",
                dispatchAndStart: goal =>
                {
                    var task = goal.Tasks.Single(task => task.Status == WorkTaskStatus.Assigned);
                    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                    return DispatchStartOutcome.Started();
                });

            new ConductorBatchLoop(measuredSweep: loopKernel => TerminalGoalSweep.Run(loopKernel, root, cache: cache) with
            {
                GitIndexDurationMs = 11,
                EvidenceDurationMs = 12,
                EphemeralDurationMs = 13,
                AttentionDurationMs = 14,
                MergeEvidenceDurationMs = 15,
                GoalsDurationMs = 16,
                GitSpawnCount = 17,
                GoalsSweptCount = 18
            }).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                onTick: tick =>
                {
                    if (tick.Tick == 2)
                    {
                        secondTick = tick;
                    }
                });

            var sweepLine = Assert.Single(secondTick!.ProgressLines!, line =>
                line.StartsWith("PHASE_TIMING tick=2 phase=sweep ", StringComparison.Ordinal) &&
                line.Contains("sweep_cache_hits=1", StringComparison.Ordinal));
            Assert.Contains("dependency_metadata_ms=", sweepLine, StringComparison.Ordinal);
            Assert.Contains("dependency_journals_read=0", sweepLine, StringComparison.Ordinal);
            Assert.Contains("sweep_cache_hits=1 sweep_cache_misses=0 sweep_git_index_ms=11 sweep_evidence_ms=12 sweep_ephemeral_ms=13 sweep_attention_ms=14 sweep_merge_evidence_ms=15 sweep_goals_ms=16 sweep_git_spawns=17 sweep_goals_swept=18", sweepLine, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_failure_reports_zero_timing_fields")]
    public void TerminalGoalSweep_failure_reports_zero_timing_fields()
    {
        var (kernel, _) = SimpleGoal("sweep failure timing goal");
        BatchTickSummary? tickSummary = null;

        new ConductorBatchLoop(measuredSweep: _ => throw new InvalidOperationException("injected sweep failure")).Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            onTick: tick => tickSummary = tick);

        var sweepLine = Assert.Single(tickSummary!.ProgressLines!, line =>
            line.StartsWith("PHASE_TIMING tick=1 phase=sweep ", StringComparison.Ordinal));
        Assert.Contains("sweep_git_index_ms=0 sweep_evidence_ms=0 sweep_ephemeral_ms=0 sweep_attention_ms=0 sweep_merge_evidence_ms=0 sweep_goals_ms=0 sweep_git_spawns=0 sweep_goals_swept=0", sweepLine, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Sweep_phase_timing_reports_every_attributed_operation")]
    public void SweepPhaseTimingReportsEveryAttributedOperation()
    {
        var (kernel, goal) = SimpleGoal("sweep phase attribution goal");
        var timingLines = new List<string>();
        var fakeTimestamp = 0L;
        var phaseSeconds = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["sweep"] = 9,
            ["persist-sweep-terminalizations"] = 8,
            ["recover-interrupted-dispatches"] = 7,
            ["self-relaunch-drain"] = 1,
            ["count-running-dispatches"] = 5,
            ["await-canary-tasks"] = 4,
            ["readmit-resolved-set-aside-goals"] = 3,
            ["mark-completed-dependency-goals"] = 2,
            ["reconcile-unscoped-dispatchable-goals"] = 1
        };
        var driver = MakeDriver();
        var landingReceiptSent = false;

        new ConductorBatchLoop(
            measuredSweep: _ =>
            {
                if (!landingReceiptSent)
                {
                    driver.SuccessfulLandingSink!(new ConductorLandingReceipt(
                        goal.Id.Value,
                        ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"]));
                    landingReceiptSent = true;
                }
                return null;
            },
            selfRelaunch: _ => new ConductorSelfRelaunchResult(false, "build", "planned rollback"),
            selfRelaunchEnabled: true,
            janitorialPhaseProbe: phase => fakeTimestamp += phaseSeconds.GetValueOrDefault(phase) * Stopwatch.Frequency,
            janitorialTimestamp: () => fakeTimestamp).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            onTick: tick => timingLines.AddRange(tick.ProgressLines ?? []));

        var sweepLine = Assert.Single(timingLines, line =>
            line.StartsWith("PHASE_TIMING ", StringComparison.Ordinal) &&
            line.Contains("phase=sweep ", StringComparison.Ordinal) &&
            line.Contains("self_relaunch_drain_ms=10000", StringComparison.Ordinal));
        var keys = new[]
        {
            "terminal_sweep_ms",
            "persist_terminalizations_ms",
            "recover_dispatches_ms",
            "count_dispatches_ms",
            "self_relaunch_drain_ms",
            "canary_await_ms",
            "readmit_setaside_ms",
            "mark_dependencies_ms",
            "reconcile_unscoped_ms"
        };
        var values = keys.ToDictionary(key => key, key => ReadTimingValue(sweepLine, key), StringComparer.Ordinal);

        Assert.Equal(
            [10_000L, 9_000L, 8_000L, 7_000L, 5_000L, 4_000L, 3_000L, 2_000L, 1_000L],
            [values["self_relaunch_drain_ms"], values["terminal_sweep_ms"], values["persist_terminalizations_ms"],
                values["recover_dispatches_ms"], values["count_dispatches_ms"], values["canary_await_ms"],
                values["readmit_setaside_ms"], values["mark_dependencies_ms"], values["reconcile_unscoped_ms"]]);
        Assert.True(keys.Select(key => sweepLine.IndexOf(key, StringComparison.Ordinal)).SequenceEqual(
            keys.Select(key => sweepLine.IndexOf(key, StringComparison.Ordinal)).Order()));

        static long ReadTimingValue(string line, string key)
        {
            var token = Assert.Single(line.Split(' '), part => part.StartsWith($"{key}=", StringComparison.Ordinal));
            return long.Parse(token[(key.Length + 1)..]);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_goal_write_invalidates_terminal_cache_entry")]
    public void TerminalGoalSweepGoalWriteInvalidatesTerminalCacheEntry()
    {
        var root = CreateTempDirectory("mcg-terminal-sweep-invalidate");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical cancelled goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal and already clean.");
            var cache = new TerminalGoalSweepCache();

            var first = TerminalGoalSweep.Run(kernel, root, cache: cache);
            var second = TerminalGoalSweep.Run(kernel, root, cache: cache);
            kernel.RecordGoalPolicyDecision(cancelled.Id, "Test fixture: goal write invalidates terminal sweep cache.");
            var third = TerminalGoalSweep.Run(kernel, root, cache: cache);

            Assert.Equal(1, first.CacheMissCount);
            Assert.Equal(1, second.CacheHitCount);
            Assert.Equal(0, third.CacheHitCount);
            Assert.Equal(1, third.CacheMissCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_cache_persists_across_cache_instances")]
    public void TerminalGoalSweepCachePersistsAcrossCacheInstances()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical durable cache goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal and already clean.");

            var first = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());
            var second = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());
            var cacheJson = File.ReadAllText(Path.Combine(root, ".orchestrator", "terminal-goal-sweep-cache.json"));

            Assert.Equal(1, first.CacheMissCount);
            Assert.Equal(0, first.CacheHitCount);
            Assert.Equal(0, second.CacheMissCount);
            Assert.Equal(1, second.CacheHitCount);
            Assert.DoesNotContain(Environment.NewLine + "  ", cacheJson, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_durable_cache_invalidates_when_branch_evidence_changes")]
    public void TerminalGoalSweepDurableCacheInvalidatesWhenBranchEvidenceChanges()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical branch evidence goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal and already clean.");

            var first = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());
            var second = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());
            RunGit(root, "branch", GoalWorktrees.BranchName(cancelled.Id));
            var third = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());

            Assert.Equal(1, first.CacheMissCount);
            Assert.Equal(1, second.CacheHitCount);
            Assert.Equal(0, third.CacheHitCount);
            Assert.Equal(1, third.CacheMissCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_corrupt_durable_cache_falls_back_to_rebuild")]
    public void TerminalGoalSweepCorruptDurableCacheFallsBackToRebuild()
    {
        var root = CreateTempDirectory("mcg-terminal-sweep-corrupt-cache");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical corrupt cache goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal and already clean.");
            var cachePath = Path.Combine(root, ".orchestrator", "terminal-goal-sweep-cache.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, "{not-json");

            var first = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());
            var second = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());

            Assert.Equal(1, first.CacheMissCount);
            Assert.Equal(0, first.CacheHitCount);
            Assert.Equal(0, second.CacheMissCount);
            Assert.Equal(1, second.CacheHitCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_pending_cleanup_blocker_is_not_cache_skipped")]
    public void TerminalGoalSweepPendingCleanupBlockerIsNotCacheSkipped()
    {
        var root = CreateTempDirectory("mcg-terminal-sweep-cleanup-blocker");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            _ = StateDbMigrations.EnsureUpToDate(
                OrchestratorWorkspace.ForDirectory(root).SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical cleanup blocker goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal cleanup blocker remains pending.");
            var contextPath = Path.Combine(root, ".orchestrator-context", cancelled.Id.Value);
            Directory.CreateDirectory(contextPath);
            File.WriteAllText(Path.Combine(contextPath, "digest.md"), "digest");
            var attempts = 0;
            var cache = new TerminalGoalSweepCache();

            var hooks = new GoalWorktreeCleanupHooks
            {
                DeleteDirectoryForCleanup = path =>
                {
                    if (path.Equals(contextPath, StringComparison.OrdinalIgnoreCase))
                    {
                        attempts++;
                        return GoalWorktreeDeleteResult.Failed(
                            GoalWorktreeDeleteFailureKind.Unknown,
                            "Directory deletion failed.");
                    }

                    return GoalWorktrees.DeleteDirectoryWithReason(path);
                },
                CleanupWarningSink = _ => { }
            };

            var first = TerminalGoalSweep.Run(kernel, root, cache: cache, cleanupHooks: hooks);
            var second = TerminalGoalSweep.Run(kernel, root, cache: cache, cleanupHooks: hooks);

            Assert.Equal(1, first.CacheMissCount);
            Assert.Equal(0, second.CacheHitCount);
            Assert.Equal(1, second.CacheMissCount);
            Assert.Equal(1, attempts);
            Assert.Contains(first.Goals, goal =>
                goal.GoalId == cancelled.Id &&
                goal.Blockers.Any(blocker => blocker.Kind == "owned-ephemeral-cleanup-needed"));
            Assert.Contains(second.Goals, goal =>
                goal.GoalId == cancelled.Id &&
                goal.Blockers.Any(blocker => blocker.Kind == "owned-ephemeral-cleanup-needed"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_cache_keeps_unintegrated_missing_branch_verified")]
    public void TerminalGoalSweepCacheKeepsUnintegratedMissingBranchVerified()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var verified = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Verified missing branch goal");
            PassVerification(kernel, verified, verified.Tasks.Single());
            var cache = new TerminalGoalSweepCache();

            var sweep = TerminalGoalSweep.Run(kernel, root, cache: cache);

            Assert.Empty(sweep.Goals);
            Assert.Equal(GoalStatus.Verified, verified.Status);
            Assert.False(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, verified.Id)));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    // ── Dynamic goal pickup: a goal ingested mid-run via the sweep is driven ──

    [Xunit.Fact(DisplayName = "BatchLoop_picks_up_a_goal_ingested_mid_run_via_the_sweep")]
    public void BatchLoop_PicksUpGoalIngestedMidRunViaSweep()
    {
        var kernel = new AgentOrchestratorKernel();
        var goalA = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "goal A");

        // A store snapshot carrying a brand-new goal B, simulating a goal submitted after the loop loaded.
        var store = new AgentOrchestratorKernel();
        var goalB = GoalLifecycleCommands.CreateAndActivateSimpleGoal(store, DefaultAgents(), "goal B");
        var snapshot = store.ExportSnapshot();

        var dispatchedIds = new List<string>();
        var ingestedOnce = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                dispatchedIds.Add(g.Id.Value);
                return DispatchStartOutcome.Started();
            });

        // The sweep ingests B on the first tick — exactly how the live --loop pulls in newly-submitted goals.
        Action<AgentOrchestratorKernel> sweep = loopKernel =>
        {
            if (!ingestedOnce) { loopKernel.IngestNewGoals(snapshot); ingestedOnce = true; }
        };

        new ConductorBatchLoop(sweep).Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 3);

        Assert.True(dispatchedIds.Contains(goalA.Id.Value));
        Assert.True(dispatchedIds.Contains(goalB.Id.Value)); // B was picked up mid-run and driven
    }

    [Xunit.Fact(DisplayName = "BatchLoop_refreshes_persisted_completion_and_dispatches_next_assigned_role")]
    public void BatchLoopRefreshesPersistedCompletionAndDispatchesNextAssignedRole()
    {
        var developerId = TaskId.New().Value;
        var testerId = TaskId.New().Value;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-role-handoff",
                    "Dispatch next role after persisted completion",
                    GoalStatus.Active,
                    [
                        new TaskSnapshot(developerId, "Implement.", AgentRole.Developer, WorkTaskStatus.Assigned, null, null, null, [], null, null),
                        new TaskSnapshot(testerId, "Test.", AgentRole.Tester, WorkTaskStatus.Assigned, null, null, null, [], null, null)
                    ],
                    [])
            ],
            []));
        var store = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-role-handoff",
                    "Dispatch next role after persisted completion",
                    GoalStatus.Active,
                    [
                        new TaskSnapshot(developerId, "Implement.", AgentRole.Developer, WorkTaskStatus.Completed, null, null, null, [], null, null),
                        new TaskSnapshot(testerId, "Test.", AgentRole.Tester, WorkTaskStatus.Assigned, null, null, null, [], null, null)
                    ],
                    [])
            ],
            []));
        AgentRole? dispatchedRole = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                var task = goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned);
                dispatchedRole = task.RequiredRole;
                kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("stub-worker", "stub", "C:\\tmp", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        new ConductorBatchLoop(loopKernel => loopKernel.RefreshTrackedGoals(store.ExportSnapshot())).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        var goal = kernel.Goals.Single();
        Assert.Equal(AgentRole.Tester, dispatchedRole);
        Assert.Equal(WorkTaskStatus.Completed, goal.Tasks.Single(task => task.Id.Value == developerId).Status);
        Assert.Equal(WorkTaskStatus.Running, goal.Tasks.Single(task => task.Id.Value == testerId).Status);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_keepAliveWhenIdle_stays_running_and_drives_a_goal_that_arrives_later")]
    public void BatchLoop_KeepAliveStaysRunningAndDrivesLaterGoal()
    {
        var kernel = new AgentOrchestratorKernel(); // starts with NO goals — a one-shot loop would exit immediately

        var store = new AgentOrchestratorKernel();
        var goalB = GoalLifecycleCommands.CreateAndActivateSimpleGoal(store, DefaultAgents(), "goal B");
        var snapshot = store.ExportSnapshot();

        var dispatchedIds = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                dispatchedIds.Add(g.Id.Value);
                return DispatchStartOutcome.Started();
            });

        // The goal arrives on the 2nd sweep — i.e. AFTER the loop has already gone idle at least once.
        var sweeps = 0;
        Action<AgentOrchestratorKernel> sweep = loopKernel =>
        {
            sweeps++;
            if (sweeps == 2) { loopKernel.IngestNewGoals(snapshot); }
        };

        // sleepFunc bounds the test: it returns "stop" after a handful of idle/no-progress sleeps, so the
        // loop terminates even though keep-alive would otherwise poll forever on an empty backlog.
        var sleepCalls = 0;
        Func<TimeSpan, bool> sleepFunc = _ => ++sleepCalls >= 6;

        new ConductorBatchLoop(sweep).Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 10,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: sleepFunc,
            keepAliveWhenIdle: true);

        // Without keep-alive the loop would have exited on the first empty tick and never seen B.
        Assert.True(dispatchedIds.Contains(goalB.Id.Value));
    }

    // ── Loop scheduling: concurrent cap limits active dispatches ─────────

    [Xunit.Fact(DisplayName = "BatchLoop_cap_holds_third_goal_when_two_already_dispatched")]
    public void BatchLoop_CapHoldsThirdGoalWhenTwoAlreadyDispatched()
    {
        var kernel = new AgentOrchestratorKernel();
        // 3 goals all in WorkspaceReady state
        for (var i = 0; i < 3; i++)
            GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), $"goal {i}");

        var dispatched = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => dispatched,
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                dispatched++;
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();
        var policy = ConductorAutonomyPolicy.Conservative;
        var expectedDispatches = Math.Min(3, policy.MaxConcurrentPaidWorkers);
        var summary = new ConductorBatchLoop().Run(kernel, driver, policy, stopFile);

        Assert.True(summary.Advanced >= expectedDispatches);
        Assert.Equal(expectedDispatches, dispatched);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_auto_rescopes_active_dispatchable_goal_after_configured_unscoped_ticks")]
    public void BatchLoopAutoRescopesActiveDispatchableGoalAfterConfiguredUnscopedTicks()
    {
        var (kernel, goal) = SimpleGoal("stale unscoped goal");
        var firstAdvance = true;
        var workspaceCreates = 0;
        var sleeps = 0;
        var driver = MakeDriver(
            createWorkspace: _ =>
            {
                if (firstAdvance)
                {
                    firstAdvance = false;
                    throw new InvalidOperationException("transient stale exclusion");
                }

                workspaceCreates++;
                return "C:\\goal";
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                sleeps++;
                return false;
            },
            keepAliveWhenIdle: true,
            unscopedStallTickThreshold: 2);

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(1, workspaceCreates);
        Assert.Equal(2, sleeps);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("stall reconciliation", StringComparison.Ordinal) &&
            evt.Message.Contains("after 2 unscoped tick(s)", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_unscoped_stall_threshold_controls_detection_tick")]
    public void BatchLoopUnscopedStallThresholdControlsDetectionTick()
    {
        var (kernel, goal) = SimpleGoal("threshold goal");
        var firstAdvance = true;
        var workspaceCreates = 0;
        var sleeps = 0;
        var driver = MakeDriver(
            createWorkspace: _ =>
            {
                if (firstAdvance)
                {
                    firstAdvance = false;
                    throw new InvalidOperationException("transient stale exclusion");
                }

                workspaceCreates++;
                return "C:\\goal";
            });

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                sleeps++;
                return false;
            },
            keepAliveWhenIdle: true,
            unscopedStallTickThreshold: 1);

        Assert.Equal(1, workspaceCreates);
        Assert.Equal(1, sleeps);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("after 1 unscoped tick(s)", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_scoped_goal_does_not_trigger_unscoped_stall_backstop")]
    public void BatchLoopScopedGoalDoesNotTriggerUnscopedStallBackstop()
    {
        var (kernel, goal) = SimpleGoal("held but scoped goal");
        var dispatchAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                dispatchAttempts++;
                return DispatchStartOutcome.EmptyBatch("No ready batch.");
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false,
            unscopedStallTickThreshold: 1);

        Assert.Equal(3, summary.Ticks);
        Assert.Equal(3, dispatchAttempts);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("stall reconciliation", StringComparison.Ordinal));
    }

    // ── Duration cap: loop exits when max-duration is reached ────────────

    [Xunit.Fact(DisplayName = "MaxDuration_ParameterAcceptedAndLoopExitsCleanly")]
    public void MaxDuration_ParameterAcceptedAndLoopExitsCleanly()
    {
        var (kernel, _) = SimpleGoal();

        // Goal is always held (concurrent cap) so without a cap the loop would exit on first no-progress tick.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var stopFile = NoStopPath();

        // maxDuration is generous (won't fire), maxIterations not set.
        // Loop exits on first held tick via the no-watch-mode no-progress path.
        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
            maxDuration: TimeSpan.FromSeconds(60));

        // Loop exits cleanly (not via stop file, not via duration cap — via no-progress exit path)
        Assert.False(summary.StopRequested);
        Assert.Equal(1, summary.Ticks);
        Assert.Equal(0, summary.Advanced);
    }

    [Xunit.Fact(DisplayName = "Spec_refinement_launch_policy_uses_injected_instants_for_cadence")]
    public void SpecRefinementLaunchPolicyUsesInjectedInstantsForCadence()
    {
        var launchedAt = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var attempt = new SpecRefinementLaunchAttempt(
            launchedAt,
            ConsecutiveFailedClaims: 0,
            EscalatedAt: null,
            "goal-spec-refinement:goal",
            "C:\\state.db");

        var deferred = SpecRefinementLaunchPolicy.Decide(
            attempt,
            launchedAt.AddMinutes(14).AddSeconds(59),
            GoalRefinementWorkCoordinator.RecoveryLaunchCadence,
            GoalRefinementWorkCoordinator.ConsecutiveFailedClaimLimit);
        var due = SpecRefinementLaunchPolicy.Decide(
            attempt,
            launchedAt.AddMinutes(15).AddSeconds(1),
            GoalRefinementWorkCoordinator.RecoveryLaunchCadence,
            GoalRefinementWorkCoordinator.ConsecutiveFailedClaimLimit);

        Assert.Equal(SpecRefinementLaunchDecisionKind.DeferCadence, deferred.Kind);
        Assert.Equal(SpecRefinementLaunchDecisionKind.Launch, due.Kind);
        Assert.Equal(1, due.ConsecutiveFailedClaims);
    }

    [Xunit.Fact(DisplayName = "Spec_refinement_failed_claim_count_survives_store_reconstruction")]
    public void SpecRefinementFailedClaimCountSurvivesStoreReconstruction()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory("mcg-refinement-attempt-store"));
        var goalId = GoalId.New();
        var saved = new SpecRefinementLaunchAttempt(
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            ConsecutiveFailedClaims: 2,
            EscalatedAt: null,
            GoalRefinementWorkCoordinator.MessageId(goalId),
            workspace.SqliteStatePath);

        SpecRefinementLaunchAttemptStore.ForWorkspace(workspace).Save(goalId, saved);
        var reloaded = SpecRefinementLaunchAttemptStore.ForWorkspace(workspace).Get(goalId);

        Assert.Equal(saved, reloaded);
    }

    [Xunit.Fact(DisplayName = "Spec_refinement_processing_claim_resets_failures_without_relaunch")]
    public void SpecRefinementProcessingClaimResetsFailuresWithoutRelaunch()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory("mcg-refinement-processing"));
        var goalId = GoalId.New();
        var processingStartedAt = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        SpecRefinementLaunchAttemptStore.ForWorkspace(workspace).Save(
            goalId,
            new SpecRefinementLaunchAttempt(
                processingStartedAt,
                ConsecutiveFailedClaims: 2,
                EscalatedAt: null,
                GoalRefinementWorkCoordinator.MessageId(goalId),
                workspace.SqliteStatePath));
        var launches = 0;
        GoalRefinementWorkCoordinator.UtcNowOverride = () => processingStartedAt.AddMinutes(1);
        GoalRefinementWorkCoordinator.LaunchOverride = (_, _) =>
        {
            launches++;
            return new GoalRefinementWorkLaunchResult(true, launches, "test-launch");
        };
        try
        {
            var result = GoalRefinementWorkCoordinator.TryLaunchIfDue(
                workspace,
                goalId,
                OrchestratorStateOutboxStatus.Processing,
                processingStartedAt);

            Assert.False(result.Started);
            Assert.Equal(GoalRefinementWorkCoordinator.ClaimInProgressDetail, result.Detail);
            Assert.Equal(0, launches);
            Assert.Null(SpecRefinementLaunchAttemptStore.ForWorkspace(workspace).Get(goalId));
        }
        finally
        {
            GoalRefinementWorkCoordinator.LaunchOverride = null;
            GoalRefinementWorkCoordinator.UtcNowOverride = null;
        }
    }

    [Xunit.Fact(DisplayName = "Spec_refinement_expired_processing_lease_escalates_without_resetting_failures")]
    public void SpecRefinementExpiredProcessingLeaseEscalatesWithoutResettingFailures()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory("mcg-refinement-expired-processing"));
        var goalId = GoalId.New();
        var launchedAt = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var now = launchedAt + GoalRefinementWorkCoordinator.RecoveryLaunchCadence + TimeSpan.FromSeconds(1);
        SpecRefinementLaunchAttemptStore.ForWorkspace(workspace).Save(
            goalId,
            new SpecRefinementLaunchAttempt(
                launchedAt,
                ConsecutiveFailedClaims: 2,
                EscalatedAt: null,
                GoalRefinementWorkCoordinator.MessageId(goalId),
                workspace.SqliteStatePath));
        var launches = 0;
        GoalRefinementWorkCoordinator.UtcNowOverride = () => now;
        GoalRefinementWorkCoordinator.LaunchOverride = (_, _) =>
        {
            launches++;
            return new GoalRefinementWorkLaunchResult(true, launches, "test-launch");
        };
        try
        {
            var result = GoalRefinementWorkCoordinator.TryLaunchIfDue(
                workspace,
                goalId,
                OrchestratorStateOutboxStatus.Processing,
                launchedAt);

            Assert.False(result.Started);
            Assert.Contains("SPEC_REFINEMENT_OPERATOR_RECOVERY", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, launches);
            Assert.Equal(3, GoalRefinementWorkCoordinator.GetConsecutiveFailedClaims(workspace, goalId));
        }
        finally
        {
            GoalRefinementWorkCoordinator.LaunchOverride = null;
            GoalRefinementWorkCoordinator.UtcNowOverride = null;
        }
    }

    [Xunit.Fact(DisplayName = "Spec_refinement_third_failed_claim_escalates_and_stops_launching")]
    public void SpecRefinementThirdFailedClaimEscalatesAndStopsLaunching()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory("mcg-refinement-escalation"));
        var goalId = GoalId.New();
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var launches = 0;
        GoalRefinementWorkCoordinator.UtcNowOverride = () => now;
        GoalRefinementWorkCoordinator.LaunchOverride = (_, _) =>
        {
            launches++;
            return new GoalRefinementWorkLaunchResult(true, launches, "test-launch");
        };
        try
        {
            Assert.True(GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId).Started);
            now += GoalRefinementWorkCoordinator.RecoveryLaunchCadence.Add(TimeSpan.FromSeconds(1));
            Assert.True(GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId).Started);
            now += GoalRefinementWorkCoordinator.RecoveryLaunchCadence.Add(TimeSpan.FromSeconds(1));
            Assert.True(GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId).Started);
            now += GoalRefinementWorkCoordinator.RecoveryLaunchCadence.Add(TimeSpan.FromSeconds(1));

            var escalated = GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId);
            var stillEscalated = GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId);

            Assert.False(escalated.Started);
            Assert.False(stillEscalated.Started);
            Assert.Equal(3, launches);
            Assert.Contains("SPEC_REFINEMENT_OPERATOR_RECOVERY", escalated.Detail, StringComparison.Ordinal);
            Assert.Contains($"message_id={GoalRefinementWorkCoordinator.MessageId(goalId)}", escalated.Detail, StringComparison.Ordinal);
            Assert.Contains($"store_path={Path.GetFullPath(workspace.SqliteStatePath)}", escalated.Detail, StringComparison.Ordinal);
            Assert.Contains("consecutive_failed_claims=3", escalated.Detail, StringComparison.Ordinal);
        }
        finally
        {
            GoalRefinementWorkCoordinator.LaunchOverride = null;
            GoalRefinementWorkCoordinator.UtcNowOverride = null;
        }
    }

    [Xunit.Fact(DisplayName = "Spec_refinement_claim_miss_is_stderr_failure_and_nonzero_exit")]
    public void SpecRefinementClaimMissIsStderrFailureAndNonzeroExit()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory("mcg-refinement-claim-miss"));
        var goalId = GoalId.New();
        string stdout = string.Empty;
        CliExitException? failure = null;

        var stderr = AsyncLocalConsoleRouter.CaptureError(() =>
            stdout = AsyncLocalConsoleRouter.Capture(() =>
                failure = Assert.Throws<CliExitException>(() =>
                    GoalRefinementWorkOutcomeReporter.Report(
                        new GoalRefinementWorkProcessResult(goalId.Value, Claimed: false, Attached: false),
                        workspace))));

        Assert.NotNull(failure);
        Assert.NotEqual(0, failure.ExitCode);
        Assert.DoesNotContain("SPEC_REFINEMENT_WORK_COMPLETE", stdout, StringComparison.Ordinal);
        Assert.Contains("SPEC_REFINEMENT_WORK_FAILED", stderr, StringComparison.Ordinal);
        Assert.Contains($"message_id={GoalRefinementWorkCoordinator.MessageId(goalId)}", stderr, StringComparison.Ordinal);
        Assert.Contains($"store_path={Path.GetFullPath(workspace.SqliteStatePath)}", stderr, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Spec_refinement_pending_hold_reports_pending_age")]
    public async Task SpecRefinementPendingHoldReportsPendingAge()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory("mcg-refinement-pending-age"));
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            DefaultAgents(),
            "Research and plan a focused implementation with tests.");
        GoalRefinementWorkCoordinator.RecordPending(kernel, goal.Id);
        await repository.SaveAsync(kernel);
        var message = GoalRefinementWorkCoordinator.CreateMessage(goal.Id) with
        {
            CreatedAt = new DateTimeOffset(2026, 9, 21, 1, 0, 0, TimeSpan.Zero)
        };
        await repository.EnsureOutboxMessageAsync(message);
        GoalRefinementWorkCoordinator.UtcNowOverride = () =>
            new DateTimeOffset(2026, 9, 21, 15, 2, 0, TimeSpan.Zero);
        GoalRefinementWorkCoordinator.LaunchOverride = (_, _) =>
            new GoalRefinementWorkLaunchResult(true, 7, "test-launch");
        try
        {
            var pending = Assert.Throws<InvalidOperationException>(() =>
                GoalDispatchOperations.EnsureRefinedForSpecConsumer(
                    kernel,
                    workspace,
                    providers: null,
                    kernel.GetGoal(goal.Id)));

            Assert.Contains("pending_age=14h02m", pending.Message, StringComparison.Ordinal);
        }
        finally
        {
            GoalRefinementWorkCoordinator.LaunchOverride = null;
            GoalRefinementWorkCoordinator.UtcNowOverride = null;
        }
    }
}
