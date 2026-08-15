using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
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

    [Xunit.Theory(DisplayName = "BatchLoop_self_relaunch_activation_switch_defaults_off_and_requires_true")]
    [Xunit.InlineData(null, false)]
    [Xunit.InlineData("", false)]
    [Xunit.InlineData("false", false)]
    [Xunit.InlineData("1", false)]
    [Xunit.InlineData("true", true)]
    public void BatchLoopSelfRelaunchActivationSwitchDefaultsOffAndRequiresTrue(
        string? configuredValue,
        bool expected)
    {
        Assert.False(ConductorBatchLoop.DefaultSelfRelaunchEnabled);
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

            var firstPersistEntered = new ManualResetEventSlim(initialState: false);
            var allowFirstPersist = new ManualResetEventSlim(initialState: false);
            var tickCount = 0;
            var persistCount = 0;
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
                        {
                            if (Interlocked.Increment(ref persistCount) == 1)
                            {
                                firstPersistEntered.Set();
                                allowFirstPersist.Wait(TimeSpan.FromSeconds(15));
                            }

                            repository.TransactAsync(
                                    (stored, _) =>
                                    {
                                        stored.ReplaceGoalWithSnapshot(currentKernel.ExportGoalSnapshot(loopGoal.Id));
                                        return Task.FromResult((true, true));
                                    })
                                .GetAwaiter()
                                .GetResult();
                        })));

            Xunit.Assert.True(firstPersistEntered.Wait(TimeSpan.FromSeconds(15)), "Conductor did not reach its first tick write.");
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

            try
            {
                Xunit.Assert.True(refiner.Entered.Wait(TimeSpan.FromSeconds(15)), "Goal intake did not reach refinement.");
                allowFirstPersist.Set();
                await loopTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                allowFirstPersist.Set();
                refiner.Release.Set();
            }

            await intakeTask.WaitAsync(TimeSpan.FromSeconds(15));
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

    [Xunit.Fact(DisplayName = "Conductor_policy_resolution_uses_file_default_and_explicit_preset_precedence")]
    public void ConductorPolicyResolutionUsesFileDefaultAndExplicitPresetPrecedence()
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
                MaxConcurrentPaidWorkers = 8
            };
            File.WriteAllText(policyPath, configured.ToJson());

            var fromFile = CliCommandHandlers.ResolveConductorPolicy(null, orchestratorDirectory);
            Assert.Equal(8, fromFile.Policy.MaxConcurrentPaidWorkers);
            Assert.Equal($"file:{Path.GetFullPath(policyPath)}", fromFile.Source);
            Assert.Contains(fromFile.Warnings, warning =>
                warning.Contains("above the highest preset value 5", StringComparison.Ordinal));

            var explicitPreset = CliCommandHandlers.ResolveConductorPolicy("Permissive", orchestratorDirectory);
            Assert.Same(ConductorAutonomyPolicy.Permissive, explicitPreset.Policy);
            Assert.Equal("preset", explicitPreset.Source);

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
            Assert.Contains(explicitPreset.Warnings, warning =>
                warning.Contains(policyPath, StringComparison.Ordinal) &&
                warning.Contains("not valid JSON", StringComparison.Ordinal));
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

            new ConductorBatchLoop(measuredSweep: loopKernel => TerminalGoalSweep.Run(loopKernel, root, cache: cache)).Run(
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

            Assert.Contains(secondTick!.ProgressLines!, line =>
                line.StartsWith("PHASE_TIMING tick=2 phase=sweep ", StringComparison.Ordinal) &&
                line.Contains("sweep_cache_hits=1", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
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
        var originalDeleteDirectoryForCleanup = GoalWorktrees.DeleteDirectoryForCleanup;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
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

            GoalWorktrees.DeleteDirectoryForCleanup = path =>
            {
                if (path.Equals(contextPath, StringComparison.OrdinalIgnoreCase))
                {
                    attempts++;
                    return GoalWorktreeDeleteResult.Failed(
                        GoalWorktreeDeleteFailureKind.Unknown,
                        "Directory deletion failed.");
                }

                return originalDeleteDirectoryForCleanup(path);
            };
            GoalWorktrees.CleanupWarningSink = _ => { };

            var first = TerminalGoalSweep.Run(kernel, root, cache: cache);
            var second = TerminalGoalSweep.Run(kernel, root, cache: cache);

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
            GoalWorktrees.DeleteDirectoryForCleanup = originalDeleteDirectoryForCleanup;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
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
}
