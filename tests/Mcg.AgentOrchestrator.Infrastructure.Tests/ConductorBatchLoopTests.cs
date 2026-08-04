using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Unit tests for ConductorBatchLoop covering: loop scheduling (cap, hold/advance),
/// auto-retry-recover, auto-retry-escalate, and kill-switch.
/// END-marker parsing tests live in ChaosGateTests.
/// </summary>
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTests
{
    private readonly ITestOutputHelper _output;

    public ConductorBatchLoopTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<AgentDefinition> DefaultAgents() => AgentCatalog.Default().Agents;

    private static (AgentOrchestratorKernel Kernel, Goal Goal) SimpleGoal(string objective = "Test goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), objective);
        return (kernel, goal);
    }

    private static void PassVerification(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        PassVerificationAt(kernel, goal, task, DateTimeOffset.UtcNow);
    }

    private static void PassVerificationAt(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, DateTimeOffset completedAt)
    {
        var dispatch = new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        var verification = new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", "", completedAt);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
    }

    private static Exception SqliteBusy() =>
        new InvalidOperationException("SQLite Error 5: 'database is locked'.");

    private static GoalWorktreeRebaseResult DefaultRebaseSuccess() =>
        new(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "OK", [], null);

    private static ConductorDriver MakeDriver(
        Func<Goal, GoalLifecycleFacts>? getFacts = null,
        Func<int>? getRunningCount = null,
        Func<Goal, string>? createWorkspace = null,
        Func<Goal, DispatchStartOutcome>? dispatchAndStart = null,
        Func<Goal, bool>? runAcceptance = null,
        Func<Goal, int?, AcceptanceVerificationSummary>? runAcceptanceWithSlot = null,
        Func<Goal, GoalWorktreeRebaseResult>? rebaseOntoMain = null,
        Func<Goal, LandingEscalationRecheckResult>? recheckPreLandingRebaseConflict = null,
        Func<Goal, LandingResult>? land = null,
        Action<Goal>? record = null,
        Func<Goal, GoalWorktreeRemoveResult>? cleanup = null,
        Action<Goal, GoalLifecycleState, string>? writeEscalation = null,
        Func<Goal, ChangeRiskTier?>? classifyRisk = null,
        Func<GoalId, TaskId, string, TaskSpec>? retryTask = null,
        Func<GoalId, TaskId, IReadOnlyList<string>, int>? recordCriterionRetryFeedback = null,
        Action<Goal, string>? recordMissingBranchRetirement = null,
        Func<Goal, IReadOnlyList<string>>? getLandingFileScopes = null,
        ConductorParallelAcceptanceAttemptCoordinator? parallelAcceptanceAttemptCoordinator = null,
        Func<Goal, int>? getAcceptanceSlotCount = null) =>
        new ConductorDriver(
            getFacts ?? (_ => GoalLifecycleFacts.None),
            getRunningCount ?? (() => 0),
            createWorkspace ?? (_ => "/tmp/workspace"),
            dispatchAndStart ?? (_ => DispatchStartOutcome.Started()),
            null,
            null,
            goal => (runAcceptance ?? (_ => true))(goal)
                ? AcceptanceVerificationSummary.PassedWithNoUnmetCriteria
                : AcceptanceVerificationSummary.Failed,
            null,
            retryTask,
            null,
            recordCriterionRetryFeedback,
            null,
            rebaseOntoMain ?? (_ => DefaultRebaseSuccess()),
            land is null
                ? ((g, _) => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"))
                : ((g, _) => land(g)),
            null,
            record ?? (_ => { }),
            cleanup ?? (_ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null)),
            writeEscalation ?? ((_, _, _) => { }),
            classifyRisk ?? (_ => null),
            recordMissingBranchRetirement: recordMissingBranchRetirement,
            getLandingFileScopes: getLandingFileScopes,
            runAcceptanceVerificationWithSlot: runAcceptanceWithSlot,
            parallelAcceptanceAttemptCoordinator: parallelAcceptanceAttemptCoordinator,
            getAcceptanceSlotCount: getAcceptanceSlotCount,
            recheckPreLandingRebaseConflict: recheckPreLandingRebaseConflict);

    // Returns a path to a stop file that does NOT exist yet.
    private static string NoStopPath() =>
        Path.Combine(Path.GetTempPath(), $"conduct-stop-{Guid.NewGuid():N}.txt");

    private static SqliteOrchestratorStateRepository OpenStateRepository(string dbPath)
        => CreateMigratedStateRepository(dbPath);

    private static Goal CreateVerifiedSimpleGoal(AgentOrchestratorKernel kernel, string objective)
    {
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), objective);
        PassVerification(kernel, goal, goal.Tasks.Single());
        return goal;
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

    [Xunit.Theory(DisplayName = "StateDb_startup_migrations_require_conductor_authority_or_explicit_backlog_bootstrap")]
    [Xunit.InlineData("conduct", "--loop", true)]
    [Xunit.InlineData("conduct", "--watch", true)]
    [Xunit.InlineData("backlog-intake", "queued item", true)]
    [Xunit.InlineData("status", null, false)]
    [Xunit.InlineData("backlog-show", null, false)]
    [Xunit.InlineData("operator-intent-status", "intent-id", false)]
    public void StateDbStartupMigrationsRequireConductorAuthorityOrExplicitBacklogBootstrap(
        string command,
        string? argument,
        bool expected)
    {
        var args = argument is null ? [command] : new[] { command, argument };

        Assert.Equal(expected, CliPersistentStateRunner.HasStateDbMigrationAuthority(args));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_loop_start_reports_verified_journal_mode")]
    public void BatchLoopLoopStartReportsVerifiedJournalMode()
    {
        var (kernel, _) = SimpleGoal();
        var output = CaptureConsole(() =>
            new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 0,
                journalMode: "wal"));

        Assert.Contains("LOOP_START policy=Conservative", output, StringComparison.Ordinal);
        Assert.Contains("journalMode=wal", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_default_self_relaunch_activation_does_not_schedule_or_execute")]
    public void BatchLoopDefaultSelfRelaunchActivationDoesNotScheduleOrExecute()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update conductor loop");
        var landed = false;
        var relaunchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: candidate =>
            {
                landed = true;
                return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ =>
                ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"]);

        var output = AsyncLocalConsoleRouter.Capture(() =>
            new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    return new ConductorSelfRelaunchResult(false, "build", "must not run");
                }).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 3));

        Assert.Equal(0, relaunchCalls);
        Assert.DoesNotContain("LOOP_RELAUNCH_SCHEDULED", output, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "BatchLoop_infrastructure_landing_drains_before_self_handoff")]
    public void BatchLoopInfrastructureLandingDrainsBeforeSelfHandoff()
    {
        var kernel = new AgentOrchestratorKernel();
        var infraGoal = CreateVerifiedSimpleGoal(kernel, "Update conductor loop");
        var runningGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Existing worker");
        var runningTask = runningGoal.Tasks.Single();
        StartProcess(kernel, runningGoal, runningTask, DateTimeOffset.UtcNow, "abc123");
        var landed = false;
        var order = new List<string>();
        Process? successor = null;
        var handoffRoot = CreateTempDirectory("mcg-conduct-loop-drain-real-handoff");
        var loopStartedPath = Path.Combine(handoffRoot, "loop-started");
        var driver = MakeDriver(
            getFacts: goal => goal.Id == infraGoal.Id && landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: goal =>
            {
                landed = true;
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ =>
                ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"]);

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var summary = new ConductorBatchLoop(
                    selfRelaunch: request =>
                    {
                        order.Add("handoff");
                        var handoff = ConductorLoopHandoff.TryStartSuccessor(
                            HandoffOptions(
                                handoffRoot,
                                release: () => order.Add("authority-release"),
                                verificationTimeout: TimeSpan.FromMilliseconds(50),
                                loopStartProbe: (_, _) => File.Exists(loopStartedPath),
                                useProtocolReady: true),
                            new ConductorLoopHandoffRequest(request.Tick, TimeSpan.Zero, 0, 1),
                            launchRequest =>
                            {
                                successor = Process.Start(new ProcessStartInfo("powershell")
                                {
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    ArgumentList =
                                    {
                                        "-NoProfile",
                                        "-Command",
                                        $"Set-Content -LiteralPath '{launchRequest.ReadyFilePath!.Replace("'", "''", StringComparison.Ordinal)}' " +
                                        $"-Value ('LOOP_HANDOFF_READY token={launchRequest.HandoffToken} pid=' + $PID); " +
                                        $"while (-not (Test-Path -LiteralPath '{launchRequest.ActivationFilePath!.Replace("'", "''", StringComparison.Ordinal)}')) {{ Start-Sleep -Milliseconds 25 }}; " +
                                        $"Set-Content -LiteralPath '{loopStartedPath.Replace("'", "''", StringComparison.Ordinal)}' -Value started; Start-Sleep -Seconds 10"
                                    }
                                })!;
                                return new ConductLoopLaunchResult(
                                    successor.Id,
                                    launchRequest.StdoutPath,
                                    launchRequest.StderrPath,
                                    "spawnPath=test-real-process");
                            });
                        return new ConductorSelfRelaunchResult(
                            handoff.Started,
                            handoff.Started ? null : "handoff",
                            handoff.Reason,
                            handoff);
                    },
                    selfRelaunchEnabled: true).Run(
                        kernel,
                        driver,
                        ConductorAutonomyPolicy.Conservative,
                        NoStopPath(),
                        maxIterations: 5,
                        watchInterval: TimeSpan.FromMilliseconds(1),
                        sleepFunc: _ =>
                        {
                            order.Add("terminal-receipt");
                            CompleteDispatchedTask(kernel, runningGoal, runningTask, DateTimeOffset.UtcNow, "def456");
                            return false;
                        });

                Assert.True(summary.Handoff?.Started);
            });

            Assert.True(order.IndexOf("terminal-receipt") < order.IndexOf("handoff"));
            Assert.True(order.IndexOf("handoff") < order.IndexOf("authority-release"));
            Assert.Contains("LOOP_RELAUNCH_DRAIN", output, StringComparison.Ordinal);
            Assert.Contains("active=1 admitting=false", output, StringComparison.Ordinal);
            Assert.Contains("LOOP_HANDOFF", output, StringComparison.Ordinal);
            Assert.False(successor!.HasExited);
        }
        finally
        {
            if (successor is { HasExited: false })
            {
                successor.Kill(entireProcessTree: true);
                successor.WaitForExit(5000);
            }
            successor?.Dispose();
            TryDeleteDirectory(handoffRoot);
        }
    }

    [Xunit.Theory(DisplayName = "BatchLoop_self_relaunch_failure_rolls_back_and_continues")]
    [Xunit.InlineData("self-check", "LOOP_RELAUNCH_ROLLBACK", true)]
    [Xunit.InlineData("handoff", "LOOP_HANDOFF_FAILED", true)]
    [Xunit.InlineData("handoff", "LOOP_HANDOFF_FAILED", false)]
    public void BatchLoopSelfRelaunchFailureRollsBackAndContinues(
        string failedPhase,
        string expectedEvent,
        bool incumbentCanContinue)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update verifier");
        var landed = false;
        var relaunchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: candidate =>
            {
                landed = true;
                return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ =>
                ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"]);

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var loop = new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    return new ConductorSelfRelaunchResult(
                        false,
                        failedPhase,
                        "forced failure",
                        IncumbentCanContinue: incumbentCanContinue);
                },
                selfRelaunchEnabled: true);
            if (incumbentCanContinue)
            {
                var summary = loop.Run(
                        kernel,
                        driver,
                        ConductorAutonomyPolicy.Conservative,
                        NoStopPath(),
                        maxIterations: 4);
                Assert.Null(summary.Handoff);
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => loop.Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 4));
                Assert.Contains("refusing to continue", error.Message, StringComparison.OrdinalIgnoreCase);
            }
        });

        Assert.Equal(1, relaunchCalls);
        Assert.Contains(expectedEvent, output, StringComparison.Ordinal);
        Assert.Contains($"phase={failedPhase}", output, StringComparison.Ordinal);
        Assert.Contains(
            incumbentCanContinue
                ? "rolledBack=true continuing=true"
                : "rolledBack=false continuing=false",
            output,
            StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_non_infrastructure_landing_does_not_self_relaunch")]
    public void BatchLoopNonInfrastructureLandingDoesNotSelfRelaunch()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update dashboard rendering");
        var landed = false;
        var relaunchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: candidate =>
            {
                landed = true;
                return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ =>
                ["src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.cs"]);

        new ConductorBatchLoop(
            selfRelaunch: _ =>
            {
                relaunchCalls++;
                return new ConductorSelfRelaunchResult(false, "build", "must not run");
            },
            selfRelaunchEnabled: true).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 3);

        Assert.Equal(0, relaunchCalls);
    }

    [Xunit.Fact]
    public void BatchLoopDocIntersectionAdmitsConcurrentlyWithEvidence()
    {
        var kernel = new AgentOrchestratorKernel();
        var goalA = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/A.cs");
        var goalB = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/B.cs");
        for (var attempt = 1;
             attempt < 128 && BuildPermitIndex(goalA) == BuildPermitIndex(goalB);
             attempt++)
        {
            kernel = new AgentOrchestratorKernel();
            goalA = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/A.cs");
            goalB = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/B.cs");
        }
        if (BuildPermitIndex(goalA) == BuildPermitIndex(goalB))
        {
            throw new InvalidOperationException(
                $"Could not generate goals on distinct build permits after 128 attempts; " +
                $"goalA={goalA.Id.Value}, goalB={goalB.Id.Value}, permit={BuildPermitIndex(goalA)}.");
        }

        using var release = new ManualResetEventSlim(false);
        using var bothStarted = new CountdownEvent(2);
        using var bothFinished = new CountdownEvent(2);
        var running = 0;
        var maxRunning = 0;
        var slots = new ConcurrentDictionary<string, int?>();
        var rebaseCounts = new ConcurrentDictionary<string, int>();
        var landOrder = new List<string>();
        var landed = new HashSet<string>(StringComparer.Ordinal);
        object gate = new();
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        Action waitForAttempts = () => { };

        try
        {
            var driver = MakeDriver(
                getFacts: goal => landed.Contains(goal.Id.Value)
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                rebaseOntoMain: goal =>
                {
                    rebaseCounts.AddOrUpdate(goal.Id.Value, 1, (_, count) => count + 1);
                    return DefaultRebaseSuccess();
                },
                runAcceptanceWithSlot: (goal, slot) =>
                {
                    slots[goal.Id.Value] = slot;
                    lock (gate)
                    {
                        running++;
                        maxRunning = Math.Max(maxRunning, running);
                    }

                    bothStarted.Signal();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                    lock (gate)
                    {
                        running--;
                    }

                    bothFinished.Signal();
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                land: goal =>
                {
                    lock (landOrder)
                    {
                        landOrder.Add(goal.Id.Value);
                        landed.Add(goal.Id.Value);
                    }

                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok");
                },
                getLandingFileScopes: goal => goal.Id == goalA.Id
                    ? ["docs/test-design-discipline.md", "src/Mcg.AgentOrchestrator.App/Orchestration/A.cs"]
                    : ["docs\\test-design-discipline.md", "src/Mcg.AgentOrchestrator.App/Orchestration/B.cs"],
                parallelAcceptanceAttemptCoordinator: ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts));

            BatchTickSummary? startTick = null;
            var startClock = Stopwatch.StartNew();
            var startSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: t => startTick = t);
            startClock.Stop();

            Assert.Equal(0, startSummary.Advanced);
            Assert.Equal(2, startSummary.Held);
            Assert.Equal(GoalStatus.Verifying, goalA.Status);
            Assert.Equal(GoalStatus.Verifying, goalB.Status);
            Assert.True(bothStarted.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            Assert.True(bothFinished.Wait(TimeSpan.FromSeconds(5)));

            BatchTickSummary? reconcileTick = null;
            var totalAdvanced = 0;
            for (var tick = 0; tick < 8 && totalAdvanced < 2; tick++)
            {
                var reconcileSummary = new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1,
                    onTick: t => reconcileTick = t);
                totalAdvanced += reconcileSummary.Advanced;
                Thread.Sleep(50);
            }

            Assert.Equal(2, totalAdvanced);
            Assert.Equal(2, maxRunning);
            Assert.Equal(2, slots.Values.Where(slot => slot.HasValue).Select(slot => slot!.Value).Distinct().Count());
            Assert.Equal(2, rebaseCounts[goalA.Id.Value]);
            Assert.Equal(2, rebaseCounts[goalB.Id.Value]);
            Assert.Equal(2, landOrder.Count);
            Assert.Contains(goalA.Id.Value, landOrder);
            Assert.Contains(goalB.Id.Value, landOrder);
            Assert.All(
                new[] { ReadLatestAttempt(attemptRoot, goalA), ReadLatestAttempt(attemptRoot, goalB) },
                attempt =>
                {
                    Assert.True(attempt.OwnerProcessId > 0);
                    Assert.Equal(ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind, attempt.Kind);
                    Assert.Contains("docs/test-design-discipline.md", attempt.ScopePaths ?? []);
                    using var heartbeat = JsonDocument.Parse(File.ReadAllText(attempt.HeartbeatPath));
                    Assert.Equal(ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind, heartbeat.RootElement.GetProperty("kind").GetString());
                    Assert.Equal(attempt.OwnerProcessId, heartbeat.RootElement.GetProperty("childPid").GetInt32());
                    Assert.Contains(
                        heartbeat.RootElement.GetProperty("ownedPids").EnumerateArray(),
                        pid => pid.GetInt32() == attempt.OwnerProcessId);
                });
            Assert.Contains(startTick!.ProgressLines!, line =>
                line.Contains("ADMISSION", StringComparison.Ordinal) &&
                line.Contains("result=admitted", StringComparison.Ordinal) &&
                line.Contains("reason=documentation-exclusion", StringComparison.Ordinal) &&
                line.Contains("excludedPathCount=1", StringComparison.Ordinal) &&
                line.Contains("excludedPathSample=docs/test-design-discipline.md", StringComparison.Ordinal));
            Assert.Contains(startTick!.ProgressLines!, line => line.Contains("ACCEPTANCE", StringComparison.Ordinal) && line.Contains("result=started", StringComparison.Ordinal));
            Assert.Contains(reconcileTick!.ProgressLines!, line => line.Contains("ACCEPTANCE", StringComparison.Ordinal) && line.Contains("result=passed", StringComparison.Ordinal));
        }
        finally
        {
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_live_oldest_attempt_does_not_block_second_slot_across_ticks")]
    public void BatchLoopLiveOldestAttemptDoesNotBlockSecondSlotAcrossTicks()
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var running = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/RunningAcrossTicks.cs");
        var primer = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/PrimerAcrossTicks.cs");
        var waiting = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/WaitingAcrossTicks.cs");
        for (var attempt = 1;
             attempt < 128 &&
             (BuildPermitIndex(running) == BuildPermitIndex(primer) ||
              BuildPermitIndex(running) == BuildPermitIndex(waiting));
             attempt++)
        {
            kernel = new AgentOrchestratorKernel();
            running = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                DefaultAgents(),
                "Update src/Mcg.AgentOrchestrator.App/Orchestration/RunningAcrossTicks.cs");
            primer = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                DefaultAgents(),
                "Update src/Mcg.AgentOrchestrator.App/Orchestration/PrimerAcrossTicks.cs");
            waiting = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                DefaultAgents(),
                "Update src/Mcg.AgentOrchestrator.App/Orchestration/WaitingAcrossTicks.cs");
        }
        if (BuildPermitIndex(running) == BuildPermitIndex(primer) ||
            BuildPermitIndex(running) == BuildPermitIndex(waiting))
        {
            throw new InvalidOperationException(
                $"Could not generate a running goal with a permit distinct from both later goals after 128 attempts; " +
                $"running={BuildPermitIndex(running)}, primer={BuildPermitIndex(primer)}, waiting={BuildPermitIndex(waiting)}.");
        }

        var now = DateTimeOffset.UtcNow;
        PassVerificationAt(kernel, running, running.Tasks.Single(), now);
        using var releaseGates = new ManualResetEventSlim(false);
        using var runningEntered = new ManualResetEventSlim(false);
        using var primerEntered = new ManualResetEventSlim(false);
        using var waitingEntered = new ManualResetEventSlim(false);
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var rejectRunningCandidateRebuild = false;
        Action waitForAttempts = () => { };

        try
        {
            var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    if (goal.Id == running.Id)
                    {
                        runningEntered.Set();
                        Assert.True(releaseGates.Wait(TimeSpan.FromSeconds(5)));
                    }
                    else if (goal.Id == primer.Id)
                    {
                        primerEntered.Set();
                    }
                    else if (goal.Id == waiting.Id)
                    {
                        waitingEntered.Set();
                        Assert.True(releaseGates.Wait(TimeSpan.FromSeconds(5)));
                    }

                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                land: goal => new LandingResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "ok"),
                getLandingFileScopes: goal =>
                {
                    if (goal.Id == running.Id && rejectRunningCandidateRebuild)
                    {
                        throw new IOException("running candidate scope temporarily unavailable");
                    }

                    return [$"src/Mcg.AgentOrchestrator.App/Orchestration/{goal.Id.Value}.cs"];
                },
                parallelAcceptanceAttemptCoordinator: coordinator);

            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
            Assert.True(runningEntered.Wait(TimeSpan.FromSeconds(5)));

            rejectRunningCandidateRebuild = true;
            PassVerificationAt(kernel, primer, primer.Tasks.Single(), now.AddMinutes(1));
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
            Assert.True(primerEntered.Wait(TimeSpan.FromSeconds(5)));

            PassVerificationAt(kernel, waiting, waiting.Tasks.Single(), now.AddMinutes(2));
            BatchTickSummary? admissionTick = null;
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: current => admissionTick = current);

            Assert.Contains(admissionTick!.ProgressLines!, line =>
                line.Contains($"ACCEPTANCE goal={waiting.Id.Value[..8]}", StringComparison.Ordinal) &&
                line.Contains("result=started", StringComparison.Ordinal));
            Assert.DoesNotContain(admissionTick.ProgressLines!, line =>
                line.Contains($"goal={waiting.Id.Value[..8]}", StringComparison.Ordinal) &&
                line.Contains("result=deferred", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-fairness", StringComparison.Ordinal));
            Assert.True(waitingEntered.Wait(TimeSpan.FromSeconds(5)));

            var runningAttempt = ReadLatestAttempt(attemptRoot, running);
            var waitingAttempt = ReadLatestAttempt(attemptRoot, waiting);
            var heldPermits = new[]
            {
                ReadAcquirePermit(runningAttempt),
                ReadAcquirePermit(waitingAttempt)
            };
            var configuredPermits = Enumerable
                .Range(0, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount)
                .Select(index => $"build-{index}")
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(coordinator.HasLiveAttempt(running.Id.Value));
            Assert.True(coordinator.HasLiveAttempt(waiting.Id.Value));
            Assert.Equal(2, heldPermits.Distinct(StringComparer.Ordinal).Count());
            Assert.All(heldPermits, permit => Assert.Contains(permit, configuredPermits));
        }
        finally
        {
            releaseGates.Set();
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_candidate_manifest_slot_count_limits_own_parallel_acceptance")]
    public void BatchLoopCandidateManifestSlotCountLimitsOwnParallelAcceptance()
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var goalA = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/A.cs");
        var goalB = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/B.cs");
        using var release = new ManualResetEventSlim();
        using var started = new CountdownEvent(1);
        var slots = new ConcurrentDictionary<string, int?>();
        var attemptRoot = CreateTempDirectory("mcg-conductor-manifest-slot-count");
        Action waitForAttempts = () => { };

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, slot) =>
                {
                    slots[goal.Id.Value] = slot;
                    started.Signal();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: goal => goal.Id == goalA.Id
                    ? ["src/Mcg.AgentOrchestrator.App/Orchestration/A.cs"]
                    : ["src/Mcg.AgentOrchestrator.App/Orchestration/B.cs"],
                parallelAcceptanceAttemptCoordinator: ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts),
                getAcceptanceSlotCount: goal =>
                    goal.Id == goalA.Id
                        ? ConductorBatchLoop.DefaultParallelAcceptanceCapacity
                        : 1);

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, summary.Held);
            Assert.Equal(0, slots[goalA.Id.Value]);
            Assert.False(slots.ContainsKey(goalB.Id.Value));
            Assert.Equal(GoalStatus.Verifying, goalA.Status);
            Assert.Equal(GoalStatus.Verified, goalB.Status);
        }
        finally
        {
            release.Set();
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_serializes_overlapping_gate_ready_acceptance")]
    public void BatchLoopSerializesOverlappingGateReadyAcceptance()
    {
        var kernel = new AgentOrchestratorKernel();
        var goalA = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/Same.cs");
        var goalB = CreateVerifiedSimpleGoal(kernel, "Also update src/Mcg.AgentOrchestrator.App/Orchestration/Same.cs");
        using var releaseAcceptance = new SemaphoreSlim(0);
        using var acceptanceStarted = new SemaphoreSlim(0);
        using var acceptanceFinished = new SemaphoreSlim(0);
        var running = 0;
        var overlapped = false;
        var slots = new ConcurrentQueue<int?>();
        var landed = new HashSet<string>(StringComparer.Ordinal);
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        Action waitForAttempts = () => { };

        try
        {
            var driver = MakeDriver(
                getFacts: goal => landed.Contains(goal.Id.Value)
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, slot) =>
                {
                    slots.Enqueue(slot);
                    if (Interlocked.Increment(ref running) > 1)
                    {
                        overlapped = true;
                    }

                    acceptanceStarted.Release();
                    try
                    {
                        Assert.True(releaseAcceptance.Wait(TimeSpan.FromSeconds(5)));
                        return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref running);
                        acceptanceFinished.Release();
                    }
                },
                land: goal =>
                {
                    landed.Add(goal.Id.Value);
                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok");
                },
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/Same.cs"],
                parallelAcceptanceAttemptCoordinator: ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts));

            BatchLoopSummary RunSingleTick() =>
                new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1);

            void ReleaseStartedAcceptance()
            {
                Assert.True(acceptanceStarted.Wait(TimeSpan.FromSeconds(5)));
                releaseAcceptance.Release();
                Assert.True(acceptanceFinished.Wait(TimeSpan.FromSeconds(5)));
                waitForAttempts();
            }

            var firstSummary = RunSingleTick();
            ReleaseStartedAcceptance();

            var secondSummary = RunSingleTick();
            ReleaseStartedAcceptance();

            var thirdSummary = RunSingleTick();

            var totalAdvanced = firstSummary.Advanced + secondSummary.Advanced + thirdSummary.Advanced;
            var totalHeld = firstSummary.Held + secondSummary.Held + thirdSummary.Held;
            var observedSlots = slots.ToArray();

            Assert.Equal(2, totalAdvanced);
            Assert.True(totalHeld >= 2);
            Assert.False(overlapped);
            Assert.Equal(2, observedSlots.Length);
            Assert.All(observedSlots, slot => Assert.True(slot.HasValue));
        }
        finally
        {
            releaseAcceptance.Release(2);
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_failing_parallel_acceptance_does_not_block_sibling_landing")]
    public void BatchLoopFailingParallelAcceptanceDoesNotBlockSiblingLanding()
    {
        var kernel = new AgentOrchestratorKernel();
        var failing = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/Failing.cs");
        var passing = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/Passing.cs");
        var landed = new List<string>();

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (goal, _) => goal.Id == failing.Id
                ? AcceptanceVerificationSummary.Failed
                : AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            land: goal =>
            {
                landed.Add(goal.Id.Value);
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok");
            },
            writeEscalation: (_, _, _) => { },
            getLandingFileScopes: goal => goal.Id == failing.Id
                ? ["src/Mcg.AgentOrchestrator.App/Orchestration/Failing.cs"]
                : ["src/Mcg.AgentOrchestrator.App/Orchestration/Passing.cs"]);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            maxVerifyRetries: 0);

        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal([passing.Id.Value], landed);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_slot_path_unmet_acceptance_retries_with_concrete_feedback")]
    public void BatchLoopSlotPathUnmetAcceptanceRetriesWithConcreteFeedback()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/RetryEvidence.cs");
        var passing = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/PassingRetryEvidence.cs");
        var task = goal.Tasks.Single();
        string? retryMessage = null;
        int? observedSlot = null;
        var unmet = new AcceptanceCheckResult(
            "command-exit dotnet test --filter SlotRetryEvidence",
            false,
            1,
            string.Join(Environment.NewLine,
            [
                "src/RetryEvidence.cs(4,5): error CS0103: The name 'missing' does not exist in the current context",
                "[xUnit.net 00:00:02.00]     Mcg.AgentOrchestrator.Tests.SlotRetryEvidenceTests.ReportsFailingTest [FAIL]",
            ]),
            ResultSummary: "slot acceptance failed");

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (candidate, slot) =>
            {
                if (candidate.Id == goal.Id)
                {
                    observedSlot = slot;
                    return new AcceptanceVerificationSummary(true, [unmet]);
                }

                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            getLandingFileScopes: candidate => candidate.Id == goal.Id
                ? ["src/Mcg.AgentOrchestrator.App/Orchestration/RetryEvidence.cs"]
                : ["src/Mcg.AgentOrchestrator.App/Orchestration/PassingRetryEvidence.cs"]);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

        Assert.Equal(2, summary.Advanced);
        Assert.NotNull(observedSlot);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(WorkTaskStatus.Completed, passing.Tasks.Single().Status);
        Assert.Contains("src/RetryEvidence.cs(4,5): error CS0103", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("SlotRetryEvidenceTests.ReportsFailingTest [FAIL]", brief.Content, StringComparison.Ordinal);
        Assert.Contains("failed check: command-exit dotnet test --filter SlotRetryEvidence (exit code 1)", brief.Content, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parallel_acceptance_slot_exhaustion_queues_extra_goal")]
    public void BatchLoopParallelAcceptanceSlotExhaustionQueuesExtraGoal()
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, ConductorBatchLoop.DefaultParallelAcceptanceCapacity + 1)
            .Select(index => CreateVerifiedSimpleGoal(
                kernel,
                $"Update src/Mcg.AgentOrchestrator.App/Orchestration/Slot{index}.cs"))
            .ToArray();
        for (var attempt = 1;
             attempt < 128 &&
             goals
                 .Take(ConductorBatchLoop.DefaultParallelAcceptanceCapacity)
                 .Select(BuildPermitIndex)
                 .Distinct()
                 .Count() < DotnetBuildEnvironmentManager.BuildConcurrencySlotCount;
             attempt++)
        {
            kernel = new AgentOrchestratorKernel();
            goals = Enumerable.Range(0, ConductorBatchLoop.DefaultParallelAcceptanceCapacity + 1)
                .Select(index => CreateVerifiedSimpleGoal(
                    kernel,
                    $"Update src/Mcg.AgentOrchestrator.App/Orchestration/Slot{index}.cs"))
                .ToArray();
        }
        if (goals
            .Take(ConductorBatchLoop.DefaultParallelAcceptanceCapacity)
            .Select(BuildPermitIndex)
            .Distinct()
            .Count() < DotnetBuildEnvironmentManager.BuildConcurrencySlotCount)
        {
            throw new InvalidOperationException(
                $"Could not generate {ConductorBatchLoop.DefaultParallelAcceptanceCapacity} acceptance goals " +
                $"covering {DotnetBuildEnvironmentManager.BuildConcurrencySlotCount} build permits after 128 attempts.");
        }

        using var release = new ManualResetEventSlim(false);
        using var firstWaveStarted = new CountdownEvent(ConductorBatchLoop.DefaultParallelAcceptanceCapacity);
        var running = 0;
        var maxRunning = 0;
        var slots = new ConcurrentQueue<int?>();
        object gate = new();
        var landed = new HashSet<string>(StringComparer.Ordinal);
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        Action waitForAttempts = () => { };

        try
        {
            var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts);
            var driver = MakeDriver(
                getFacts: goal => landed.Contains(goal.Id.Value)
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, slot) =>
                {
                    slots.Enqueue(slot);
                    lock (gate)
                    {
                        running++;
                        maxRunning = Math.Max(maxRunning, running);
                    }

                    if (slot.HasValue && !release.IsSet)
                    {
                        firstWaveStarted.Signal();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                    }

                    lock (gate)
                    {
                        running--;
                    }

                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: goal =>
                {
                    var index = Array.FindIndex(goals, candidate => candidate.Id == goal.Id);
                    return [$"src/Mcg.AgentOrchestrator.App/Orchestration/Slot{index}.cs"];
                },
                land: goal =>
                {
                    landed.Add(goal.Id.Value);
                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok");
                },
                parallelAcceptanceAttemptCoordinator: coordinator);

            BatchTickSummary? firstTick = null;
            var firstSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: t => firstTick = t);

            Assert.Equal(0, firstSummary.Advanced);
            Assert.Equal(ConductorBatchLoop.DefaultParallelAcceptanceCapacity + 1, firstSummary.Held);
            Assert.True(firstWaveStarted.Wait(TimeSpan.FromSeconds(5)));

            var inFlightAttempts = goals
                .Take(ConductorBatchLoop.DefaultParallelAcceptanceCapacity)
                .Select(goal => ReadLatestAttempt(attemptRoot, goal))
                .ToArray();
            var heldPermits = inFlightAttempts
                .Select(ReadAcquirePermit)
                .ToArray();
            var configuredPermits = Enumerable
                .Range(0, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount)
                .Select(index => $"build-{index}")
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(inFlightAttempts, attempt => Assert.True(coordinator.HasLiveAttempt(attempt.GoalId)));
            Assert.Equal(ConductorBatchLoop.DefaultParallelAcceptanceCapacity, heldPermits.Distinct(StringComparer.Ordinal).Count());
            Assert.All(heldPermits, permit => Assert.Contains(permit, configuredPermits));

            BatchTickSummary? saturatedTick = null;
            var saturatedSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: current => saturatedTick = current);
            var deferredPrefix = goals[^1].Id.Value[..8];

            Assert.Equal(0, saturatedSummary.Advanced);
            Assert.Equal(ConductorBatchLoop.DefaultParallelAcceptanceCapacity + 1, saturatedSummary.Held);
            Assert.Contains(saturatedTick!.ProgressLines!, line =>
                line.Contains("ADMISSION", StringComparison.Ordinal) &&
                line.Contains("result=deferred", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal));
            Assert.DoesNotContain(saturatedTick.ProgressLines!, line =>
                line.Contains("result=deferred", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-fairness", StringComparison.Ordinal));
            Assert.DoesNotContain(saturatedTick.ProgressLines!, line =>
                line.Contains($"ACCEPTANCE goal={deferredPrefix}", StringComparison.Ordinal) &&
                line.Contains("result=started", StringComparison.Ordinal));

            release.Set();
            waitForAttempts();

            var totalAdvanced = 0;
            var deferredGoalEventuallyStarted = false;
            for (var tick = 0; tick < 10 && totalAdvanced < goals.Length; tick++)
            {
                BatchTickSummary? retryTick = null;
                var retrySummary = new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1,
                    onTick: current => retryTick = current);
                totalAdvanced += retrySummary.Advanced;
                deferredGoalEventuallyStarted |= retryTick?.ProgressLines?.Any(line =>
                    line.Contains($"ACCEPTANCE goal={deferredPrefix}", StringComparison.Ordinal) &&
                    line.Contains("result=started", StringComparison.Ordinal)) == true;
                waitForAttempts();
            }

            Assert.Equal(ConductorBatchLoop.DefaultParallelAcceptanceCapacity + 1, totalAdvanced);
            Assert.True(deferredGoalEventuallyStarted);
            Assert.Equal(ConductorBatchLoop.DefaultParallelAcceptanceCapacity, maxRunning);
            Assert.DoesNotContain(slots, slot => !slot.HasValue);
            Assert.Equal(
                ConductorBatchLoop.DefaultParallelAcceptanceCapacity,
                slots
                    .Where(slot => slot.HasValue)
                    .Select(slot => slot!.Value)
                    .Distinct()
                    .Count());
            Assert.Contains(firstTick!.ProgressLines!, line =>
                line.Contains("ADMISSION", StringComparison.Ordinal) &&
                line.Contains("result=deferred", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal));
            Assert.DoesNotContain(firstTick.ProgressLines!, line =>
                line.Contains("reason=reserved-gate-slot", StringComparison.Ordinal));
        }
        finally
        {
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_invalid_parallel_acceptance_slot_settings_escalate_without_retry")]
    public void BatchLoopInvalidParallelAcceptanceSlotSettingsEscalateWithoutRetry()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(
            kernel,
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/InvalidSlotSettings.cs");
        var slotReads = 0;
        var escalationReasons = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            writeEscalation: (_, _, reason) => escalationReasons.Add(reason),
            getAcceptanceSlotCount: _ =>
            {
                slotReads++;
                throw new InvalidDataException("slotCount must be between 1 and 4");
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(1, slotReads);
        Assert.Equal(0, summary.Held);
        Assert.Equal(1, summary.Escalated);
        Assert.Contains(
            escalationReasons,
            reason => reason.Contains("invalid parallel acceptance slot settings", StringComparison.Ordinal));
        Assert.DoesNotContain(escalationReasons, reason => reason.Contains("retry", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_slots_busy_gate_retries_and_lands_on_later_tick")]
    public void BatchLoopSlotsBusyGateRetriesAndLandsOnLaterTick()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/SlotsBusy.cs");
        var attempts = 0;
        var escalations = 0;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy(
                        "goal-slots-busy",
                        Enumerable.Range(0, ConductorBatchLoop.DefaultParallelAcceptanceCapacity)
                            .Select(slot => new DotnetBuildStableSlotWait(slot, 1000 + slot))
                            .ToArray()));
                }

                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            writeEscalation: (_, _, _) => escalations++);

        var ticks = new List<BatchTickSummary>();
        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        Assert.Equal(2, attempts);
        Assert.Equal(1, summary.Held);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(0, escalations);
        Assert.Contains(ticks[0].ProgressLines!, line =>
            line.Contains("GOAL", StringComparison.Ordinal) &&
            line.Contains("result=held", StringComparison.Ordinal));
        Assert.Contains(ticks[1].ProgressLines!, line =>
            line.Contains("GOAL", StringComparison.Ordinal) &&
            line.Contains("result=executed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_build_lock_blocked_gate_retries_and_lands_on_later_tick")]
    public void BatchLoopBuildLockBlockedGateRetriesAndLandsOnLaterTick()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/BuildLock.cs");
        var attempts = 0;
        var escalations = 0;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new BuildLockBlockedException(new BuildLockAttribution(
                        @"C:\mcg-dotnet-isolated\goals\deadbeef\artifacts\bin\Mcg.AgentOrchestrator.Core.dll",
                        [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
                        "handle64-timeout",
                        "acceptance-output",
                        "classify-build-lock"));
                }

                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            writeEscalation: (_, _, _) => escalations++);

        var ticks = new List<BatchTickSummary>();
        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        Assert.Equal(2, attempts);
        Assert.Equal(1, summary.Held);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(0, escalations);
        Assert.Contains(ticks[0].ProgressLines!, line =>
            line.Contains("GOAL", StringComparison.Ordinal) &&
            line.Contains("result=held", StringComparison.Ordinal));
        Assert.Contains(ticks[1].ProgressLines!, line =>
            line.Contains("GOAL", StringComparison.Ordinal) &&
            line.Contains("result=executed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_stale_running_attempt_cannot_overwrite_terminal_stale_outcome")]
    public void ParallelAcceptanceStaleRunningAttemptCannotOverwriteTerminalStaleOutcome()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Stale.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launches = new ConcurrentDictionary<string, ConductorParallelAcceptanceOwnedProcessLaunch>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                launches[launch.Attempt.AttemptId] = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7001 + launches.Count);
            });
        var candidateA = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Stale.cs"], "branch-a", "main-a");
        var candidateB = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Stale.cs"], "branch-b", "main-a");

        try
        {
            var first = coordinator.Evaluate(candidateA, ConductorAutonomyPolicy.Conservative, PassingRun);
            var second = coordinator.Evaluate(candidateB, ConductorAutonomyPolicy.Conservative, PassingRun);
            launches[first.Attempt.AttemptId].ExecuteInCurrentProcess(first.Attempt.OwnerProcessId);
            var moved = coordinator.Evaluate(candidateB, ConductorAutonomyPolicy.Conservative, PassingRun);
            var stale = ReadAttempt(first.Attempt.MetadataPath);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, first.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, second.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, moved.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.StaleCandidate, stale.Outcome);
            Assert.Contains("candidate branch/main SHA moved", stale.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_invalidated_attempt_blocks_replacement_until_exit_or_stale_heartbeat")]
    public void ParallelAcceptanceInvalidatedAttemptBlocksReplacementUntilExitOrStaleHeartbeat()
    {
        var (_, goal) = SimpleGoal("Retry acceptance without overlapping the old gate");
        var attemptRoot = CreateTempDirectory("mcg-conductor-invalidated-acceptance-attempts");
        var now = DateTimeOffset.Parse("2026-07-30T05:00:00Z");
        var launchAttempts = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            utcNow: () => now,
            isProcessAlive: _ => true,
            launchOwnedProcess: _ =>
            {
                launchAttempts++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7200 + launchAttempts);
            },
            recentHeartbeatGrace: TimeSpan.FromSeconds(30));
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/Retry.cs"],
            "branch-a",
            "main-a");

        try
        {
            var first = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.True(coordinator.InvalidateCurrent(goal.Id.Value, "retry invalidated first attempt"));

            var firstHeld = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, firstHeld.Kind);
            Assert.Equal(first.Attempt.AttemptId, firstHeld.Attempt.AttemptId);
            Assert.Equal(1, launchAttempts);

            File.WriteAllText(first.Attempt.ExitCodePath, "1");
            now = now.AddSeconds(1);
            var second = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, second.Kind);
            Assert.Equal(2, launchAttempts);

            Assert.True(coordinator.InvalidateCurrent(goal.Id.Value, "retry invalidated second attempt"));
            var secondHeld = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, secondHeld.Kind);
            Assert.Equal(second.Attempt.AttemptId, secondHeld.Attempt.AttemptId);

            now = now.AddMinutes(1);
            var third = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, third.Kind);
            Assert.Equal(3, launchAttempts);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_rebased_attempt_updates_candidate_key_before_reconciliation")]
    public void ParallelAcceptanceRebasedAttemptUpdatesCandidateKeyBeforeReconciliation()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Rebased.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launches = new ConcurrentDictionary<string, ConductorParallelAcceptanceOwnedProcessLaunch>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                launches[launch.Attempt.AttemptId] = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7005 + launches.Count);
            });
        var preRebase = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Rebased.cs"], "branch-before", "main-a");
        var postRebase = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Rebased.cs"], "branch-after", "main-a");

        try
        {
            var started = coordinator.Evaluate(
                preRebase,
                ConductorAutonomyPolicy.Conservative,
                (candidate, _) => ConductorParallelAcceptanceRunResult.Accepted(
                    postRebase,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria));
            launches[started.Attempt.AttemptId].ExecuteInCurrentProcess(started.Attempt.OwnerProcessId);
            var completed = coordinator.Evaluate(postRebase, ConductorAutonomyPolicy.Conservative, PassingRun);
            var persisted = ReadAttempt(started.Attempt.MetadataPath);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, completed.Attempt.Outcome);
            Assert.Equal("branch-after", completed.Attempt.BranchHeadSha);
            Assert.Equal("main-a", completed.Attempt.MainHeadSha);
            Assert.Equal("branch-after", completed.Run!.Candidate.BranchHeadSha);
            Assert.Null(persisted.ReconciledAt);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, persisted.Outcome);
            Assert.Equal("branch-after", persisted.BranchHeadSha);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_stale_terminal_without_run_attempt_does_not_block_moved_candidate")]
    public void ParallelAcceptanceStaleTerminalWithoutRunAttemptDoesNotBlockMovedCandidate()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/StaleTerminal.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launchAttempts = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            launchOwnedProcess: _ =>
            {
                launchAttempts++;
                if (launchAttempts == 1)
                {
                    throw new InvalidOperationException("spawn failed");
                }

                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7010);
            });
        var candidateA = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/StaleTerminal.cs"],
            "branch-a",
            "main-a");
        var candidateB = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/StaleTerminal.cs"],
            "branch-b",
            "main-a");

        try
        {
            var failed = coordinator.Evaluate(candidateA, ConductorAutonomyPolicy.Conservative, PassingRun);
            var moved = coordinator.Evaluate(candidateB, ConductorAutonomyPolicy.Conservative, PassingRun);
            var stale = ReadAttempt(failed.Attempt.MetadataPath);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, failed.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.LaunchFailed, failed.Attempt.Outcome);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, moved.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.StaleCandidate, stale.Outcome);
            Assert.Contains("candidate branch/main SHA moved", stale.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_running_attempt_without_live_process_records_process_died")]
    public void ParallelAcceptanceRunningAttemptWithoutLiveProcessRecordsProcessDied()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Dead.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var now = new DateTimeOffset(2026, 7, 23, 4, 0, 0, TimeSpan.Zero);
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            utcNow: () => now,
            isProcessAlive: _ => false,
            recentHeartbeatGrace: TimeSpan.FromMinutes(5),
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7010));
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Dead.cs"], "branch", "main");

        try
        {
            coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            var recent = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            now = now.AddMinutes(6);
            var terminal = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, recent.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, terminal.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, terminal.Attempt.Outcome);
            Assert.Equal(1, terminal.Attempt.TransientFailureCount);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_process_died_acceptance_relaunches_without_exposing_verified")]
    public void BatchLoopProcessDiedAcceptanceRelaunchesWithoutExposingVerified()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/DeadRelaunch.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var now = new DateTimeOffset(2026, 7, 23, 4, 0, 0, TimeSpan.Zero);
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            utcNow: () => now,
            isProcessAlive: _ => false,
            recentHeartbeatGrace: TimeSpan.FromMinutes(5),
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(8100 + ++launches));
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/DeadRelaunch.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);

        try
        {
            new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Equal(1, launches);

            now = now.AddMinutes(6);
            var staleSummary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Equal(1, staleSummary.Held);
            Assert.Equal(1, launches);

            var relaunchSummary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Equal(1, relaunchSummary.Held);
            Assert.Equal(2, launches);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_running_attempt_writes_periodic_heartbeat")]
    public void ParallelAcceptanceRunningAttemptWritesPeriodicHeartbeat()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Heartbeat.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        using var secondRunningHeartbeat = new ManualResetEventSlim(false);
        var runningHeartbeats = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            runInline: true,
            heartbeatInterval: TimeSpan.FromMilliseconds(1),
            heartbeatWritten: (_, state) =>
            {
                if (state == "running" && Interlocked.Increment(ref runningHeartbeats) >= 2)
                {
                    secondRunningHeartbeat.Set();
                }
            });
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Heartbeat.cs"], "branch", "main");

        try
        {
            var completed = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, attemptPolicy, _, _) =>
                {
                    Assert.True(secondRunningHeartbeat.Wait(TimeSpan.FromSeconds(5)));
                    return PassingRun(attemptCandidate, attemptPolicy);
                });

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            Assert.True(runningHeartbeats >= 2);
            Assert.True(File.Exists(completed.Attempt.HeartbeatPath));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_reconciliation_preserves_typed_terminal_outcome")]
    public void ParallelAcceptanceReconciliationPreservesTypedTerminalOutcome()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Reconciled.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launches = new ConcurrentDictionary<string, ConductorParallelAcceptanceOwnedProcessLaunch>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                launches[launch.Attempt.AttemptId] = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7025 + launches.Count);
            });
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Reconciled.cs"], "branch", "main");

        try
        {
            var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            launches[started.Attempt.AttemptId].ExecuteInCurrentProcess(started.Attempt.OwnerProcessId);
            var completed = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            coordinator.MarkReconciled(completed.Attempt);
            var reconciled = ReadAttempt(completed.Attempt.MetadataPath);
            var next = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, reconciled.Outcome);
            Assert.NotNull(reconciled.ReconciledAt);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, next.Kind);
            Assert.NotEqual(completed.Attempt.AttemptId, next.Attempt.AttemptId);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_corrupt_result_artifact_records_corrupt_outcome")]
    public void ParallelAcceptanceCorruptResultArtifactRecordsCorruptOutcome()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Corrupt.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => false,
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7020));
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Corrupt.cs"], "branch", "main");

        try
        {
            var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            File.WriteAllText(started.Attempt.ResultPath, "{ not-json");
            var terminal = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, terminal.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts, terminal.Attempt.Outcome);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_launch_failure_records_typed_terminal_outcome")]
    public void ParallelAcceptanceLaunchFailureRecordsTypedTerminalOutcome()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/LaunchFailed.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            launchOwnedProcess: _ => throw new InvalidOperationException("spawn failed"));
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/LaunchFailed.cs"], "branch", "main");

        try
        {
            var terminal = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, terminal.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.LaunchFailed, terminal.Attempt.Outcome);
            Assert.Contains("spawn failed", terminal.Attempt.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_owned_process_start_info_redirects_stdio")]
    public void ParallelAcceptanceOwnedProcessStartInfoRedirectsStdio()
    {
        var root = CreateTempDirectory("mcg-conductor-acceptance-start-info");
        try
        {
            var metadataPath = Path.Combine(root, "attempt.json");
            var attempt = new ConductorParallelAcceptanceAttempt(
                "attempt-1",
                GoalId.New().Value,
                "attempt1",
                0,
                "branch",
                "main",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                ConductorParallelAcceptanceAttemptOutcome.Running,
                Path.Combine(root, "attempt.out.log"),
                Path.Combine(root, "attempt.err.log"),
                Path.Combine(root, "attempt.exit.txt"),
                Path.Combine(root, "attempt.heartbeat.json"),
                Path.Combine(root, "attempt.result.json"),
                metadataPath,
                ExecutionDirectory: root);

            var startInfo = ConductorParallelAcceptanceAttemptCoordinator.BuildOwnedProcessStartInfo(
                attempt,
                "dotnet",
                ["Mcg.AgentOrchestrator.App.dll"]);

            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.CreateNoWindow);
            Assert.True(startInfo.RedirectStandardInput);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.Equal(root, startInfo.WorkingDirectory);
            Assert.Equal(root, startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable]);
            Assert.Equal(
                ["Mcg.AgentOrchestrator.App.dll", ConductorParallelAcceptanceAttemptCoordinator.OwnedProcessSubcommandName, metadataPath],
                startInfo.ArgumentList.ToArray());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_durable_passed_attempt_survives_locked_receipt_artifact")]
    public void ParallelAcceptanceDurablePassedAttemptSurvivesLockedReceiptArtifact()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/LockedReceipt.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launchAttempts = new ConcurrentDictionary<string, ConductorParallelAcceptanceOwnedProcessLaunch>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                launchAttempts[launch.Attempt.AttemptId] = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7031);
            });
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/LockedReceipt.cs"], "branch", "main");

        try
        {
            var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            launchAttempts[started.Attempt.AttemptId].ExecuteInCurrentProcess(started.Attempt.OwnerProcessId);
            using var lockedResult = new FileStream(started.Attempt.ResultPath, FileMode.Open, FileAccess.Read, FileShare.None);

            var completed = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, completed.Attempt.Outcome);
            Assert.True(completed.Run!.Acceptance!.Passed);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_records_and_prunes_attempt_trx_with_attempt_artifacts")]
    public void ParallelAcceptanceRecordsAndPrunesAttemptTrxWithAttemptArtifacts()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/AttemptTrx.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var tick = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            utcNow: () => now.AddMinutes(tick++),
            runInline: true);
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/AttemptTrx.cs"], "branch", "main");
        string? firstAttemptPath = null;
        string? firstTrxPath = null;
        ConductorParallelAcceptanceAttempt? latestAttempt = null;

        try
        {
            for (var index = 0; index < 22; index++)
            {
                var completed = coordinator.Evaluate(
                    candidate,
                    ConductorAutonomyPolicy.Conservative,
                    (runCandidate, _) =>
                    {
                        var prefix = GoalAcceptanceVerifier.AcceptanceAttemptResultsPrefixForTests;
                        Assert.False(string.IsNullOrWhiteSpace(prefix));
                        var trxPath = $"{prefix}.dotnet-test.trx";
                        File.WriteAllText(trxPath, "trx");
                        return ConductorParallelAcceptanceRunResult.Accepted(
                            runCandidate,
                            new AcceptanceVerificationSummary(true, [], TestResultPaths: [trxPath]));
                    });

                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
                var persisted = ReadAttempt(completed.Attempt.MetadataPath);
                Assert.NotNull(persisted.TestResultPaths);
                Assert.Single(persisted.TestResultPaths!);
                Assert.True(File.Exists(persisted.TestResultPaths![0]));
                Assert.False(File.Exists(AcceptanceAttemptArtifactCustody.MarkerPath(
                    DotnetBuildEnvironmentManager.GoalArtifactsPath(goal.Id))));
                using var resultJson = JsonDocument.Parse(File.ReadAllText(completed.Attempt.ResultPath));
                Assert.Equal(
                    persisted.TestResultPaths![0],
                    resultJson.RootElement.GetProperty("acceptance").GetProperty("testResultPaths")[0].GetString());

                firstAttemptPath ??= completed.Attempt.MetadataPath;
                firstTrxPath ??= persisted.TestResultPaths![0];
                latestAttempt = completed.Attempt;
                coordinator.MarkReconciled(completed.Attempt);
            }

            Assert.NotNull(firstAttemptPath);
            Assert.NotNull(firstTrxPath);
            Assert.False(File.Exists(firstAttemptPath!));
            Assert.False(File.Exists(firstTrxPath!));
            Assert.NotNull(latestAttempt);
            Assert.True(File.Exists(latestAttempt!.MetadataPath));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal.Id);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_transient_launch_failure_retries_before_escalating_at_cap")]
    public void BatchLoopTransientLaunchFailureRetriesBeforeEscalatingAtCap()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/TransientLaunch.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var escalations = new List<string>();
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            launchOwnedProcess: _ =>
            {
                launches++;
                throw new IOException("The process cannot access the file 'attempt.out.log' because it is being used by another process");
            });
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            writeEscalation: (_, _, reason) => escalations.Add(reason),
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/TransientLaunch.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: ConductorBatchLoop.ParallelAcceptanceTransientFailureCap,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ => false);
            var latest = ReadLatestAttempt(attemptRoot, goal);

            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap - 1, summary.Held);
            Assert.Equal(1, summary.Escalated);
            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, latest.TransientFailureCount);
            Assert.Single(escalations);
            Assert.Contains("background acceptance launch-failed", escalations.Single(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, launches);

            var restartSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(1, restartSummary.Escalated);
            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, launches);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_verified_goal_with_running_task_does_not_start_acceptance_attempt")]
    public void BatchLoopVerifiedGoalWithRunningTaskDoesNotStartAcceptanceAttempt()
    {
        var taskId = TaskId.New().Value;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-running-task",
                    "Update src/Mcg.AgentOrchestrator.App/Orchestration/RunningTask.cs",
                    GoalStatus.Verified,
                    [
                        new TaskSnapshot(taskId, "Still running.", AgentRole.Reviewer, WorkTaskStatus.Running, null, null, null, [], null, null)
                    ],
                    [])
            ],
            []));
        var goal = kernel.Goals.Single();
        var launches = 0;
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            launchOwnedProcess: _ =>
            {
                launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7032);
            });
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException("acceptance should not run for an incomplete task"),
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/RunningTask.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(0, launches);
            Assert.Equal(1, summary.Held);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_named_failing_checks_route_parallel_acceptance_to_retry_without_rerun")]
    public void BatchLoopNamedFailingChecksRouteParallelAcceptanceToRetryWithoutRerun()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/NamedFailure.cs");
        var task = goal.Tasks.Single();
        var attempts = 0;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                attempts++;
                return new AcceptanceVerificationSummary(
                    false,
                    [],
                    "WorkerSandboxPreparer_second_round_reuses_prep_receipt failed",
                    ["WorkerSandboxPreparer_second_round_reuses_prep_receipt"]);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/NamedFailure.cs"]);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, attempts);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Null(goal.LatestAcceptanceFailure);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));
        Assert.Contains("WorkerSandboxPreparer_second_round_reuses_prep_receipt", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("acceptance failed checks", retryMessage!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_failed_parallel_acceptance_artifact_reconciles_goal_to_AcceptanceFailed")]
    public void BatchLoopFailedParallelAcceptanceArtifactReconcilesGoalToAcceptanceFailed()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/FailedGate.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var policy = ConductorAutonomyPolicy.Conservative with { MaxCriterionRetries = 0 };
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => new AcceptanceVerificationSummary(
                false,
                [],
                "Acceptance failed.",
                ["FailedGateTests.Fails"]),
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/FailedGate.cs"],
            parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                runInline: true));

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                policy,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(1, summary.Escalated);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.NotNull(goal.LatestAcceptanceFailure);
            Assert.Contains("FailedGateTests.Fails", goal.LatestAcceptanceFailure!.FailedChecks);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_environmental_interference_restores_Verified_for_regate")]
    public void BatchLoopEnvironmentalInterferenceRestoresVerifiedForRegate()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/Regate.cs");
        var task = goal.Tasks.Single();
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var interference = new AcceptanceCheckResult(
            "structural test coverage: core tests",
            false,
            1,
            "classification: gate-environment-interference",
            FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => new AcceptanceVerificationSummary(false, [interference]),
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/Regate.cs"],
            parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                runInline: true));

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(1, summary.Held);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Equal(0, task.CriterionRetryCount);
            Assert.Null(goal.LatestAcceptanceFailure);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_fast_child_terminal_outcome_survives_parent_pid_update")]
    public void ParallelAcceptanceFastChildTerminalOutcomeSurvivesParentPidUpdate()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/FastCancel.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            launchOwnedProcess: launch =>
            {
                launch.ExecuteInCurrentProcess(7030);
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7030);
            });
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/FastCancel.cs"], "branch", "main");

        try
        {
            var started = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                (_, _) => throw new OperationCanceledException("operator cancelled"));
            var persisted = ReadAttempt(started.Attempt.MetadataPath);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled, persisted.Outcome);
            Assert.Equal(7030, persisted.OwnerProcessId);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_background_cancellation_and_build_blocks_are_typed")]
    public void ParallelAcceptanceBackgroundCancellationAndBuildBlocksAreTyped()
    {
        AssertBackgroundOutcome(
            "Cancel.cs",
            (_, _) => throw new OperationCanceledException("operator cancelled"),
            ConductorParallelAcceptanceAttemptOutcome.Cancelled);
        AssertBackgroundOutcome(
            "SlotsBusyTyped.cs",
            (_, _) => throw new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy(
                "typed-test",
                [new DotnetBuildStableSlotWait(0, 7100)])),
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot);
        AssertBackgroundOutcome(
            "BuildLockTyped.cs",
            (_, _) => throw new BuildLockBlockedException(new BuildLockAttribution(
                @"C:\mcg-dotnet-isolated\goals\deadbeef\artifacts\bin\Mcg.AgentOrchestrator.Core.dll",
                [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
                "handle64-timeout",
                "acceptance-output",
                "classify-build-lock")),
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parallel_acceptance_serves_oldest_verified_goal_first")]
    public void BatchLoopParallelAcceptanceServesOldestVerifiedGoalFirst()
    {
        using var _ = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var newer = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/Newer.cs");
        var older = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/Older.cs");
        var now = DateTimeOffset.UtcNow;
        PassVerificationAt(kernel, newer, newer.Tasks.Single(), now.AddMinutes(1));
        PassVerificationAt(kernel, older, older.Tasks.Single(), now);
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var acceptanceOrder = new List<string>();

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    acceptanceOrder.Add(goal.Id.Value);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                land: goal => new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok"),
                getLandingFileScopes: goal => goal.Id == older.Id
                    ? ["src/Mcg.AgentOrchestrator.App/Orchestration/Older.cs"]
                    : ["src/Mcg.AgentOrchestrator.App/Orchestration/Newer.cs"],
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot,
                    runInline: true));

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(2, summary.Advanced);
            Assert.Equal([older.Id.Value, newer.Id.Value], acceptanceOrder);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void ParallelAcceptance_waiter_skips_persisted_running_attempt()
    {
        using var _ = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var running = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/Running.cs");
        var waiting = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/Waiting.cs");
        var now = DateTimeOffset.UtcNow;
        PassVerificationAt(kernel, running, running.Tasks.Single(), now);
        PassVerificationAt(kernel, waiting, waiting.Tasks.Single(), now.AddMinutes(1));
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");

        try
        {
            var processAlive = true;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                utcNow: () => now,
                isProcessAlive: _ => processAlive,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(8701),
                recentHeartbeatGrace: TimeSpan.FromMinutes(1));
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                running,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/Running.cs"],
                "branch",
                "main");

            var decision = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            var liveGoalIds = coordinator.GetLiveAttemptGoalIds([running.Id.Value, waiting.Id.Value]);
            var oldestWaiter = ConductorBatchLoop.SelectOldestParallelAcceptanceWaiter(
                [running, waiting],
                liveGoalIds);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, decision.Kind);
            Assert.True(coordinator.HasLiveAttempt(running.Id.Value));
            Assert.Equal(waiting.Id, oldestWaiter!.Id);
            Assert.False(ConductorBatchLoop.ShouldDeferForParallelAcceptanceFairness(oldestWaiter.Id.Value));

            now = now.AddMinutes(2);
            Assert.False(coordinator.HasLiveAttempt(running.Id.Value));
            now = now.AddMinutes(-2);
            processAlive = false;
            Assert.False(coordinator.HasLiveAttempt(running.Id.Value));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parallel_acceptance_bounded_overtake_defers_newer_after_cap")]
    public void BatchLoopParallelAcceptanceBoundedOvertakeDefersNewerAfterCap()
    {
        Assert.Equal(1, ConductorBatchLoop.ParallelAcceptanceBoundedOvertakeLimit);
        using var _ = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var older = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/OldestBlocked.cs");
        var newer = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/NewerOvertake.cs");
        var now = DateTimeOffset.UtcNow;
        PassVerificationAt(kernel, older, older.Tasks.Single(), now);
        PassVerificationAt(kernel, newer, newer.Tasks.Single(), now.AddMinutes(1));
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launched = 0;
        var livePids = new ConcurrentDictionary<int, byte>();
        var nextPid = 8600;

        try
        {
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: pid => livePids.ContainsKey(pid),
                launchOwnedProcess: _ =>
                {
                    var pid = Interlocked.Increment(ref nextPid);
                    livePids[pid] = 0;
                    launched++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(pid);
                });
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                getLandingFileScopes: goal =>
                {
                    if (goal.Id == older.Id)
                    {
                        throw new IOException("oldest scope temporarily unavailable");
                    }

                    return ["src/Mcg.AgentOrchestrator.App/Orchestration/NewerOvertake.cs"];
                },
                parallelAcceptanceAttemptCoordinator: coordinator);

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var summary = new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 2);

                Assert.Equal(0, summary.Advanced);
                Assert.True(summary.Held >= 2);
            });

            Assert.Equal(1, launched);
            Assert.Contains("reason=parallel-acceptance-fairness", output, StringComparison.Ordinal);
            Assert.Contains($"oldest={older.Id.Value[..8]}", output, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parallel_acceptance_replays_lease_receipts_to_conduct_events")]
    public void BatchLoopParallelAcceptanceReplaysLeaseReceiptsToConductEvents()
    {
        using var _ = IsolatedDotnetRootScope();
        var root = CreateTempDirectory("mcg-conduct-events-acceptance-lease");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/LeaseEvents.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/LeaseEvents.cs"],
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot,
                    runInline: true));

            new ConductorBatchLoop(conductEventLogWriter: writer).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();

            Assert.Contains(records, record =>
                record.EventKind == "acceptance-lease" &&
                record.GoalId == goal.Id.Value[..8] &&
                record.Detail.Contains("ACCEPTANCE_LEASE_ACQUIRE", StringComparison.Ordinal));
            Assert.Contains(records, record =>
                record.EventKind == "acceptance-lease" &&
                record.GoalId == goal.Id.Value[..8] &&
                record.Detail.Contains("ACCEPTANCE_LEASE_RELEASE", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parallel_acceptance_cancelled_terminal_attempt_is_held_not_escalated")]
    public void BatchLoopParallelAcceptanceCancelledTerminalAttemptIsHeldNotEscalated()
    {
        using var _ = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/CancelReplay.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var escalated = false;

        try
        {
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                launchOwnedProcess: launch =>
                {
                    launch.ExecuteInCurrentProcess(8701);
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(8701);
                });
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => throw new OperationCanceledException("goal parked"),
                writeEscalation: (_, _, _) => escalated = true,
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/CancelReplay.cs"],
                parallelAcceptanceAttemptCoordinator: coordinator);

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var summary = new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 2);

                Assert.Equal(0, summary.Escalated);
                Assert.True(summary.Held >= 1);
            });

            Assert.False(escalated);
            Assert.Contains("result=cancelled", output, StringComparison.Ordinal);
            Assert.DoesNotContain("result=escalated", output, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_attempt_holds_stable_slot_lease_until_terminal")]
    public void ParallelAcceptanceAttemptHoldsStableSlotLeaseUntilTerminal()
    {
        using var _ = IsolatedDotnetRootScope();
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/HoldLease.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/HoldLease.cs"], "branch", "main");
        DotnetBuildLeaseAcquisition? reacquireWhileRunning = null;

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var decision = coordinator.Evaluate(
                    candidate,
                    ConductorAutonomyPolicy.Conservative,
                    (attemptCandidate, _, stableSlotLease, _) =>
                    {
                        Assert.NotNull(stableSlotLease);
                        reacquireWhileRunning = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                            stableSlotLease.Environment,
                            TimeSpan.Zero);
                        return PassingRun(attemptCandidate, ConductorAutonomyPolicy.Conservative);
                    });

                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, decision.Attempt.Outcome);
            });

            Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(reacquireWhileRunning);
            Assert.Contains("ACCEPTANCE_LEASE_ACQUIRE", output);
            Assert.Contains("ACCEPTANCE_LEASE_HANDOFF", output);
            Assert.Contains("ACCEPTANCE_LEASE_RELEASE", output);
            Assert.Equal(1, CountOccurrences(output, "ACCEPTANCE_LEASE_RELEASE"));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_second_attempt_yields_without_running_while_first_holds_slot")]
    public void ParallelAcceptanceSecondAttemptYieldsWithoutRunningWhileFirstHoldsSlot()
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var (_, goalA) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/HoldA.cs");
        var (_, goalB) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/HoldB.cs");
        for (var attempt = 1;
             attempt < 128 && BuildPermitIndex(goalA) != BuildPermitIndex(goalB);
             attempt++)
        {
            (_, goalB) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/HoldB.cs");
        }
        if (BuildPermitIndex(goalA) != BuildPermitIndex(goalB))
        {
            throw new InvalidOperationException(
                $"Could not generate goals on the same build permit after 128 attempts; " +
                $"goalA={goalA.Id.Value} permit={BuildPermitIndex(goalA)}, " +
                $"goalB={goalB.Id.Value} permit={BuildPermitIndex(goalB)}.");
        }

        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        using var releaseFirst = new ManualResetEventSlim(false);
        using var firstHasLease = new ManualResetEventSlim(false);
        using var secondReachedPreSlot = new ManualResetEventSlim(false);
        var preSlotRuns = 0;
        var coordinator = ThreadedAcceptanceAttemptCoordinator(
            attemptRoot,
            out var waitForAttempts,
            (_, _) =>
            {
                if (Interlocked.Increment(ref preSlotRuns) == 2)
                {
                    secondReachedPreSlot.Set();
                }

                return null;
            });
        var candidateA = ConductorParallelAcceptanceCandidate.Create(goalA, 0, ["src/HoldA.cs"], "branch-a", "main");
        var candidateB = ConductorParallelAcceptanceCandidate.Create(goalB, 0, ["src/HoldB.cs"], "branch-b", "main");
        var secondRan = false;
        DotnetBuildEnvironment? firstLeaseEnvironment = null;

        try
        {
            var first = coordinator.Evaluate(
                candidateA,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, _, stableSlotLease, _) =>
                {
                    Assert.NotNull(stableSlotLease);
                    firstLeaseEnvironment = stableSlotLease.Environment;
                    firstHasLease.Set();
                    releaseFirst.Wait();
                    return PassingRun(attemptCandidate, ConductorAutonomyPolicy.Conservative);
                });
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, first.Kind);
            Assert.True(firstHasLease.Wait(TimeSpan.FromSeconds(5)));
            var secondEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(goalB.Id, "contention-probe");
            Assert.Equal(firstLeaseEnvironment!.ExecutionLockPath, secondEnvironment.ExecutionLockPath);
            Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(secondEnvironment, TimeSpan.Zero));

            var second = coordinator.Evaluate(
                candidateB,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, attemptPolicy, _, _) =>
                {
                    secondRan = true;
                    return PassingRun(attemptCandidate, attemptPolicy);
                });
            Assert.True(secondReachedPreSlot.Wait(TimeSpan.FromSeconds(5)));
            var blocked = WaitForAttemptOutcome(coordinator, candidateB, ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, second.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, blocked.Attempt.Outcome);
            Assert.False(secondRan);
            Assert.Equal(2, Volatile.Read(ref preSlotRuns));
            releaseFirst.Set();
        }
        finally
        {
            releaseFirst.Set();
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_attempt_yields_to_live_cli_holder_and_reclaims_dead_holder")]
    public void ParallelAcceptanceAttemptYieldsToLiveCliHolderAndReclaimsDeadHolder()
    {
        using var _ = IsolatedDotnetRootScope();
        var (_, liveGoal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/LiveCli.cs");
        var (_, deadGoal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/DeadCli.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var liveCandidate = ConductorParallelAcceptanceCandidate.Create(liveGoal, 0, ["src/LiveCli.cs"], "branch-live", "main");
        var deadCandidate = ConductorParallelAcceptanceCandidate.Create(deadGoal, 0, ["src/DeadCli.cs"], "branch-dead", "main");
        var liveEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(liveGoal.Id, "live-cli");
        var deadEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(deadGoal.Id, "dead-cli");
        var liveRan = false;
        var deadRan = false;

        try
        {
            using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(liveEnvironment))
            {
                var liveCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
                var liveBlocked = liveCoordinator.Evaluate(
                    liveCandidate,
                    ConductorAutonomyPolicy.Conservative,
                    (attemptCandidate, attemptPolicy, _, _) =>
                    {
                        liveRan = true;
                        return PassingRun(attemptCandidate, attemptPolicy);
                    });
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, liveBlocked.Attempt.Outcome);
                Assert.False(liveRan);
            }

            File.WriteAllText(deadEnvironment.ExecutionLockPath, "999999");
            var deadCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
            var reclaimed = deadCoordinator.Evaluate(
                deadCandidate,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, attemptPolicy, stableSlotLease, _) =>
                {
                    Assert.NotNull(stableSlotLease);
                    deadRan = true;
                    return PassingRun(attemptCandidate, attemptPolicy);
                });

            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, reclaimed.Attempt.Outcome);
            Assert.True(deadRan);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_cancelled_attempt_records_cancelled_and_releases_lease")]
    public void ParallelAcceptanceCancelledAttemptRecordsCancelledAndReleasesLease()
    {
        using var _ = IsolatedDotnetRootScope();
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/CancelAttempt.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/CancelAttempt.cs"], "branch", "main");
        DotnetBuildEnvironment? leasedEnvironment = null;

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var decision = coordinator.Evaluate(
                    candidate,
                    ConductorAutonomyPolicy.Conservative,
                    (_, _, stableSlotLease, _) =>
                    {
                        leasedEnvironment = stableSlotLease?.Environment;
                        throw new OperationCanceledException("goal parked");
                    });

                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled, decision.Attempt.Outcome);
            });

            Assert.NotNull(leasedEnvironment);
            Assert.Contains("ACCEPTANCE_LEASE_RELEASE", output);
            using var reacquired = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(leasedEnvironment, TimeSpan.Zero);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_parked_goal_cancels_at_next_target_boundary_and_releases_lease")]
    public void ParallelAcceptanceParkedGoalCancelsAtNextTargetBoundaryAndReleasesLease()
    {
        using var _ = IsolatedDotnetRootScope();
        var root = CreateSeededGitRepository();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(
            Path.Combine(root, "config", "acceptance-manifest.json"),
            AcceptanceManifestTestDefaults.WithEngine(
                """
                {
                  "version": 1,
                  "checks": [
                    { "name": "first target", "type": "command", "command": "first-target", "arguments": ["--ok"] },
                    { "name": "second target", "type": "command", "command": "second-target", "arguments": ["--should-not-run"] }
                  ],
                  "forbiddenChangedPathGlobs": []
                }
                """));
        RunGit(root, "add", "config/acceptance-manifest.json");
        RunGit(root, "commit", "-m", "Seed acceptance manifest");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/ParkBoundary.cs");
        var stateRepository = OpenStateRepository(workspace.SqliteStatePath);
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var calls = new List<string[]>();
        var firstTargetCompleted = false;

        try
        {
            var worktree = GoalWorktrees.Ensure(root, goal.Id);
            Directory.CreateDirectory(Path.Combine(worktree, "config"));
            Directory.CreateDirectory(Path.Combine(worktree, "src", "Mcg.AgentOrchestrator.App", "Orchestration"));
            File.WriteAllText(
                Path.Combine(worktree, "src", "Mcg.AgentOrchestrator.App", "Orchestration", "ParkBoundary.cs"),
                "namespace Mcg.AgentOrchestrator.App.Orchestration; internal static class ParkBoundary { }");
            RunGit(worktree, "add", "-A");
            RunGit(worktree, "commit", "-m", "Goal work");
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.Length > 0 && args[0] == "first-target")
                {
                    firstTargetCompleted = true;
                    kernel.ParkGoal(goal.Id, "operator parked during acceptance attempt");
                    stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "ok"));
            });
            var driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                DefaultAgents(),
                WorkerProfileCatalog.Default());
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/ParkBoundary.cs"]);

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var decision = coordinator.Evaluate(
                    candidate,
                    ConductorAutonomyPolicy.Conservative,
                    driver.RunParallelLandingAcceptance);

                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled, decision.Attempt.Outcome);
            });

            Assert.True(firstTargetCompleted);
            Assert.DoesNotContain(calls, call => call.Length > 0 && call[0] == "second-target");
            Assert.Contains("ACCEPTANCE_LEASE_RELEASE", output);
            Assert.Equal(1, CountOccurrences(output, "ACCEPTANCE_LEASE_RELEASE"));
            using var reacquired = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0),
                TimeSpan.Zero);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_serialized_early_done_replays_parent_missing_branch_retirement")]
    public void ParallelAcceptanceSerializedEarlyDoneReplaysParentMissingBranchRetirement()
    {
        var root = CreateSeededGitRepository();
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/MissingReplay.cs");
            var policy = ConductorAutonomyPolicy.Conservative;
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/MissingReplay.cs"]);
            var startCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7101));
            var started = startCoordinator.Evaluate(candidate, policy, PassingRun);
            var detail = "child observed missing branch before landing";
            var childCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false);
            childCoordinator.RunAttemptForTests(
                started.Attempt,
                candidate,
                policy,
                (attemptCandidate, attemptPolicy) => ConductorParallelAcceptanceRunResult.Early(
                    attemptCandidate,
                    new ConductorAdvanceResult(
                        attemptCandidate.Goal.Id.Value,
                        attemptCandidate.GoalPrefix,
                        attemptPolicy.Name,
                        new ConductorAdvanceOutcome.Done(GoalLifecycleState.CleanedUp)),
                    ConductorParallelAcceptanceEarlyOutcome.MissingBranchRetired(GoalLifecycleState.CleanedUp, detail)));

            var retiredDetails = new List<string>();
            var escalationReasons = new List<string>();
            var parentCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("parent should reconcile, not launch"));
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                writeEscalation: (_, _, reason) => escalationReasons.Add(reason),
                recordMissingBranchRetirement: (retiredGoal, retirementDetail) =>
                {
                    retiredDetails.Add(retirementDetail);
                    GoalOperationJournal.RecordTerminalDisposition(
                        root,
                        retiredGoal,
                        new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, retirementDetail));
                    kernel.CompleteGoal(retiredGoal.Id, retirementDetail);
                },
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/MissingReplay.cs"],
                parallelAcceptanceAttemptCoordinator: parentCoordinator);

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                policy,
                NoStopPath(),
                maxIterations: 1);
            var reconciled = ReadAttempt(started.Attempt.MetadataPath);

            Assert.Equal(1, summary.Done);
            Assert.Empty(escalationReasons);
            Assert.Equal([detail], retiredDetails);
            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, goal.Id)));
            Assert.NotNull(reconciled.ReconciledAt);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_serialized_early_escalation_replays_parent_escalation_record")]
    public void ParallelAcceptanceSerializedEarlyEscalationReplaysParentEscalationRecord()
    {
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/EscalateReplay.cs");
            var policy = ConductorAutonomyPolicy.Conservative;
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/EscalateReplay.cs"]);
            var startCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7102));
            var started = startCoordinator.Evaluate(candidate, policy, PassingRun);
            var detail = "pre-landing rebase conflict (src/EscalateReplay.cs); use 'workspace rebase' to resolve";
            var childCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false);
            childCoordinator.RunAttemptForTests(
                started.Attempt,
                candidate,
                policy,
                (attemptCandidate, attemptPolicy) => ConductorParallelAcceptanceRunResult.Early(
                    attemptCandidate,
                    new ConductorAdvanceResult(
                        attemptCandidate.Goal.Id.Value,
                        attemptCandidate.GoalPrefix,
                        attemptPolicy.Name,
                        new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, detail)),
                    ConductorParallelAcceptanceEarlyOutcome.PreLandingEscalated(GoalLifecycleState.Verified, detail)));

            var retired = false;
            var escalationReasons = new List<string>();
            var parentCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("parent should reconcile, not launch"));
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                writeEscalation: (_, state, reason) =>
                {
                    Assert.Equal(GoalLifecycleState.Verified, state);
                    escalationReasons.Add(reason);
                },
                recordMissingBranchRetirement: (_, _) => retired = true,
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/EscalateReplay.cs"],
                parallelAcceptanceAttemptCoordinator: parentCoordinator);

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                policy,
                NoStopPath(),
                maxIterations: 1);
            var reconciled = ReadAttempt(started.Attempt.MetadataPath);

            Assert.Equal(1, summary.Escalated);
            Assert.False(retired);
            Assert.Equal([detail], escalationReasons);
            Assert.NotNull(reconciled.ReconciledAt);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    // Creates a stop file and returns its path.
    private static string ExistingStopPath()
    {
        var path = NoStopPath();
        File.WriteAllText(path, "stop");
        return path;
    }

    private static string CreateSeededGitRepository()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-batch-loop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RunGit(path, "init");
        RunGit(path, "checkout", "-b", "main");
        RunGit(path, "config", "user.email", "tests@example.com");
        RunGit(path, "config", "user.name", "Batch Loop Tests");
        File.WriteAllText(Path.Combine(path, "seed.txt"), "seed");
        RunGit(path, "add", "-A");
        RunGit(path, "commit", "-m", "Seed");
        return path;
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static IDisposable IsolatedDotnetRootScope() =>
        new EnvVarScope(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
            Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-batch-loop-{Guid.NewGuid():N}"));

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;
        private readonly string? _value;

        public EnvVarScope(string name, string? value)
        {
            _name = name;
            _value = value;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previousValue);
            if (!string.IsNullOrWhiteSpace(_value) && Directory.Exists(_value))
            {
                try { Directory.Delete(_value, recursive: true); }
                catch { }
            }
        }
    }

    private static ConductorParallelAcceptanceAttemptCoordinator ThreadedAcceptanceAttemptCoordinator(
        string attemptRoot,
        out Action waitForAttempts,
        ConductorParallelAcceptanceTryRunPreSlot? tryRunPreSlot = null)
    {
        var nextPid = 8000;
        var alive = new ConcurrentDictionary<int, byte>();
        var threads = new ConcurrentBag<Thread>();
        waitForAttempts = () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            foreach (var thread in threads)
            {
                var remaining = deadline - DateTime.UtcNow;
                Assert.True(
                    remaining > TimeSpan.Zero && thread.Join(remaining),
                    $"acceptance attempt thread {thread.Name} did not finish before cleanup");
            }
        };

        return new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: pid => alive.ContainsKey(pid),
            tryRunPreSlot: tryRunPreSlot,
            launchOwnedProcess: launch =>
            {
                var pid = Interlocked.Increment(ref nextPid);
                var thread = new Thread(() =>
                {
                    alive[pid] = 0;
                    try
                    {
                        launch.ExecuteInCurrentProcess(pid);
                    }
                    finally
                    {
                        alive.TryRemove(pid, out _);
                    }
                })
                {
                    IsBackground = true,
                    Name = $"acceptance-attempt-test-{pid}"
                };
                threads.Add(thread);
                thread.Start();
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(pid);
            });
    }

    private static ConductorParallelAcceptanceRunResult PassingRun(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy _) =>
        ConductorParallelAcceptanceRunResult.Accepted(candidate, AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);

    private static ConductorParallelAcceptanceAttempt ReadLatestAttempt(string attemptRoot, Goal goal) =>
        Directory.EnumerateFiles(Path.Combine(attemptRoot, goal.Id.Value), "*.attempt.json")
            .Select(ReadAttempt)
            .OrderByDescending(attempt => attempt.StartedAt)
            .First();

    private static ConductorParallelAcceptanceAttempt ReadAttempt(string path) =>
        JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string ReadAcquirePermit(ConductorParallelAcceptanceAttempt attempt)
    {
        var receipt = Assert.Single(
            attempt.LeaseReceipts ?? [],
            line => line.Contains("ACCEPTANCE_LEASE_ACQUIRE", StringComparison.Ordinal));
        var permitToken = Assert.Single(
            receipt.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            token => token.StartsWith("permit=", StringComparison.Ordinal));
        return permitToken["permit=".Length..];
    }

    private static AcceptanceMakespanSample MeasureFixedGateMakespan(
        int parallelCapacity,
        int fixedGateDurationMs)
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 2)
            .Select(index => CreateVerifiedSimpleGoal(
                kernel,
                $"Update src/Mcg.AgentOrchestrator.App/Orchestration/Makespan{index}.cs"))
            .ToArray();
        for (var attempt = 1;
             attempt < 128 && goals.Select(BuildPermitIndex).Distinct().Count() < goals.Length;
             attempt++)
        {
            kernel = new AgentOrchestratorKernel();
            goals = Enumerable.Range(0, 2)
                .Select(index => CreateVerifiedSimpleGoal(
                    kernel,
                    $"Update src/Mcg.AgentOrchestrator.App/Orchestration/Makespan{index}.cs"))
                .ToArray();
        }
        Assert.Equal(goals.Length, goals.Select(BuildPermitIndex).Distinct().Count());

        var logicalTimeMs = 0;
        var timings = new ConcurrentQueue<(int Started, int Completed)>();
        using var gateStarted = new SemaphoreSlim(0);
        var gates = new ConcurrentQueue<AcceptanceMeasurementGate>();
        var landed = new HashSet<string>(StringComparer.Ordinal);
        var attemptRoot = CreateTempDirectory("mcg-conductor-measured-makespan");
        Action waitForAttempts = () => { };

        try
        {
            var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts);
            var driver = MakeDriver(
                getFacts: goal => landed.Contains(goal.Id.Value)
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, slot) =>
                {
                    var gate = new AcceptanceMeasurementGate(Volatile.Read(ref logicalTimeMs), slot);
                    gates.Enqueue(gate);
                    gateStarted.Release();
                    Assert.True(gate.Release.Wait(TimeSpan.FromSeconds(5)));
                    timings.Enqueue((gate.StartedAtMs, Volatile.Read(ref logicalTimeMs)));
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: goal =>
                {
                    var index = Array.FindIndex(goals, candidate => candidate.Id == goal.Id);
                    return [$"src/Mcg.AgentOrchestrator.App/Orchestration/Makespan{index}.cs"];
                },
                land: goal =>
                {
                    landed.Add(goal.Id.Value);
                    return new LandingResult(
                        goal.Id.Value,
                        goal.Id.Value[..8],
                        new LandingDecision.Promote(),
                        "integration",
                        true,
                        "ok");
                },
                parallelAcceptanceAttemptCoordinator: coordinator,
                getAcceptanceSlotCount: _ => parallelCapacity);

            var releasedGateCount = 0;
            for (var tick = 0; tick < 6 && landed.Count < goals.Length; tick++)
            {
                new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1);

                var expectedWaveCount = Math.Min(parallelCapacity, goals.Length - releasedGateCount);
                for (var index = 0; index < expectedWaveCount; index++)
                {
                    Assert.True(
                        gateStarted.Wait(TimeSpan.FromSeconds(5)),
                        $"Expected {expectedWaveCount} acceptance gates to start in tick {tick}.");
                }

                var wave = gates.ToArray()
                    .Skip(releasedGateCount)
                    .Take(expectedWaveCount)
                    .ToArray();
                Assert.Equal(expectedWaveCount, wave.Length);
                if (expectedWaveCount > 1)
                {
                    Assert.All(wave, gate => Assert.True(gate.PermitIndex.HasValue));
                    Assert.Equal(expectedWaveCount, wave.Select(gate => gate.PermitIndex).Distinct().Count());
                }

                if (wave.Length > 0)
                {
                    Interlocked.Add(ref logicalTimeMs, fixedGateDurationMs);
                    foreach (var gate in wave)
                    {
                        gate.Release.Set();
                    }

                    releasedGateCount += wave.Length;
                }

                waitForAttempts();
            }

            Assert.Equal(goals.Length, landed.Count);
            var completedTimings = timings.ToArray();
            Assert.Equal(goals.Length, completedTimings.Length);
            var firstStarted = completedTimings.Min(timing => timing.Started);
            var lastCompleted = completedTimings.Max(timing => timing.Completed);
            var gateDurationsMs = completedTimings
                .Select(timing => (double)(timing.Completed - timing.Started))
                .Order()
                .ToArray();
            var permits = goals
                .Select(goal => ReadAcquirePermit(ReadLatestAttempt(attemptRoot, goal)))
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (parallelCapacity > 1)
            {
                Assert.Equal(goals.Length, permits.Distinct(StringComparer.Ordinal).Count());
            }

            return new AcceptanceMakespanSample(
                lastCompleted - firstStarted,
                gateDurationsMs,
                permits);
        }
        finally
        {
            foreach (var gate in gates)
            {
                gate.Release.Set();
            }

            waitForAttempts();
            foreach (var gate in gates)
            {
                gate.Dispose();
            }

            TryDeleteDirectory(attemptRoot);
        }
    }

    private static double Median(IReadOnlyList<AcceptanceMakespanSample> samples) =>
        samples.Count % 2 == 0
            ? (samples[(samples.Count / 2) - 1].MakespanMs + samples[samples.Count / 2].MakespanMs) / 2
            : samples[samples.Count / 2].MakespanMs;

    private static string FormatRange(IReadOnlyList<AcceptanceMakespanSample> samples) =>
        $"{samples.Min(sample => sample.MakespanMs):F1}-{samples.Max(sample => sample.MakespanMs):F1}";

    private static string FormatGateDurations(IEnumerable<AcceptanceMakespanSample> samples) =>
        string.Join('|', samples.Select(sample => string.Join(',', sample.GateDurationsMs.Select(duration => $"{duration:F1}"))));

    private static string FormatPermits(IEnumerable<AcceptanceMakespanSample> samples) =>
        string.Join('|', samples.Select(sample => string.Join(',', sample.Permits)));

    private sealed record AcceptanceMakespanSample(
        double MakespanMs,
        IReadOnlyList<double> GateDurationsMs,
        IReadOnlyList<string> Permits);

    private sealed class AcceptanceMeasurementGate(int startedAtMs, int? permitIndex) : IDisposable
    {
        public int StartedAtMs { get; } = startedAtMs;

        public int? PermitIndex { get; } = permitIndex;

        public ManualResetEventSlim Release { get; } = new(false);

        public void Dispose() => Release.Dispose();
    }

    private static int BuildPermitIndex(Goal goal) =>
        goal.Id.Value[..8]
            .ToLowerInvariant()
            .Sum(ch => (int)ch) %
        DotnetBuildEnvironmentManager.BuildConcurrencySlotCount;

    private static void AssertBackgroundOutcome(
        string fileName,
        Func<ConductorParallelAcceptanceCandidate, ConductorAutonomyPolicy, ConductorParallelAcceptanceRunResult> run,
        ConductorParallelAcceptanceAttemptOutcome expected)
    {
        var (_, goal) = SimpleGoal($"Update src/Mcg.AgentOrchestrator.App/Orchestration/{fileName}");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out var waitForAttempts);
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            [$"src/Mcg.AgentOrchestrator.App/Orchestration/{fileName}"],
            "branch",
            "main");

        try
        {
            var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, run);
            var terminal = WaitForAttemptOutcome(coordinator, candidate, expected);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            Assert.Equal(expected, terminal.Attempt.Outcome);
        }
        finally
        {
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    private static ConductorParallelAcceptanceAttemptDecision WaitForAttemptOutcome(
        ConductorParallelAcceptanceAttemptCoordinator coordinator,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorParallelAcceptanceAttemptOutcome expected)
    {
        // Failsafe bound, NOT an assertion about speed: the attempt runs on a dedicated thread doing real
        // lease-file I/O, so this must be generous enough that only a genuine hang trips it. The former
        // fixed 50 x 20ms (~1s) poll made machine speed the pass condition and failed under concurrent
        // acceptance lanes with "Expected: BlockedBuildSlot, Actual: Running".
        var waitBudget = Stopwatch.StartNew();
        while (waitBudget.Elapsed < TimeSpan.FromSeconds(30))
        {
            var decision = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            if (decision.Attempt.Outcome == expected)
            {
                return decision;
            }

            Thread.Sleep(20);
        }

        var latest = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
        Assert.Equal(expected, latest.Attempt.Outcome);
        return latest;
    }

    private static ConductLoopHandoffOptions HandoffOptions(
        string root,
        IReadOnlyList<string>? args = null,
        string? stopFilePath = null,
        string? runEventStorePath = null,
        int renewalCount = 0,
        int maxRenewals = ConductorLoopHandoff.DefaultMaxRenewalsWithoutLanding,
        Action? release = null,
        TimeSpan verificationTimeout = default,
        Func<ConductLoopHandoffOptions, long, bool>? loopStartProbe = null,
        bool useProtocolReady = false) =>
        new(
            Args: args ?? ["conduct", "--loop", "--watch", "--max-duration", "14400"],
            ExecutionDirectory: root,
            OrchestratorDirectory: Path.Combine(root, ".orchestrator"),
            LogDirectory: Path.Combine(root, ".orchestrator", "logs"),
            RunEventStorePath: runEventStorePath ?? Path.Combine(root, ".orchestrator", "run-events.db"),
            StopFilePath: stopFilePath ?? Path.Combine(root, ConductorBatchLoop.StopFileName),
            RenewalCount: renewalCount,
            MaxRenewals: maxRenewals,
            ReleaseCurrentLease: release ?? (() => { }),
            VerificationTimeout: verificationTimeout,
            LoopStartProbe: loopStartProbe,
            SuccessorReadyProbe: useProtocolReady ? null : (_, _) => true);

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var result = GitCli.Run(workingDirectory, args);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static AgentDefinition[] BuildAgents(params AgentRole[] roles)
    {
        var capability = ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse;
        return roles
            .Select(role => new AgentDefinition(
                AgentId.New(),
                role.ToString(),
                role,
                new ModelProfile("OpenAI", "test", capability, SubscriptionMode.ApiKey)))
            .ToArray();
    }

    [Xunit.Fact(DisplayName = "BatchLoop_max_duration_starts_handoff")]
    public void BatchLoopMaxDurationStartsHandoff()
    {
        var (kernel, _) = SimpleGoal();
        ConductorLoopHandoffRequest? request = null;
        var summary = new ConductorBatchLoop(
            handoffOnMaxDuration: handoffRequest =>
            {
                request = handoffRequest;
                return ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log");
            }).Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxDuration: TimeSpan.Zero);

        Assert.NotNull(request);
        Assert.True(summary.Handoff?.Started);
        Assert.Equal(1234, summary.Handoff.ProcessId);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_stop_file_does_not_handoff")]
    public void BatchLoopStopFileDoesNotHandoff()
    {
        var (kernel, _) = SimpleGoal();
        var called = false;

        var summary = new ConductorBatchLoop(
            handoffOnMaxDuration: _ =>
            {
                called = true;
                return ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log");
            }).Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            ExistingStopPath(),
            maxDuration: TimeSpan.FromSeconds(1));

        Assert.True(summary.StopRequested);
        Assert.False(called);
        Assert.Null(summary.Handoff);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_no_progress_exit_does_not_handoff")]
    public void BatchLoopNoProgressExitDoesNotHandoff()
    {
        var (kernel, _) = SimpleGoal();
        var called = false;

        new ConductorBatchLoop(
            handoffOnMaxDuration: _ =>
            {
                called = true;
                return ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log");
            }).Run(
            kernel,
            MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath());

        Assert.False(called);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_max_duration_detaches_running_dispatches_before_handoff")]
    public void BatchLoopMaxDurationDetachesRunningDispatchesBeforeHandoff()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        StartProcess(kernel, goal, task, DateTimeOffset.UtcNow, "base");
        var detached = 0;
        var cancelled = 0;

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (_, _) => cancelled++,
            detachGoalRunningDispatches: (_, _) => detached++,
            handoffOnMaxDuration: _ => ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log")).Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxDuration: TimeSpan.Zero);

        Assert.True(summary.Handoff?.Started);
        Assert.Equal(1, detached);
        Assert.Equal(0, cancelled);
    }

    [Xunit.Fact(DisplayName = "ConductorLoopLease_refuses_second_loop")]
    public void ConductorLoopLeaseRefusesSecondLoop()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-lease");
        try
        {
            var orchestrator = Path.Combine(root, ".orchestrator");
            using var lease = ConductorLoopLease.Acquire(orchestrator);

            Assert.Throws<InvalidOperationException>(() => ConductorLoopLease.Acquire(orchestrator));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_stop_file_suppresses_successor")]
    public void ConductorLoopHandoffStopFileSuppressesSuccessor()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-stop-handoff");
        try
        {
            var stopFile = Path.Combine(root, ConductorBatchLoop.StopFileName);
            File.WriteAllText(stopFile, "stop");
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(root, stopFilePath: stopFile),
                new ConductorLoopHandoffRequest(0, TimeSpan.Zero, 0),
                _ => throw new InvalidOperationException("launch should not run"));

            Assert.False(result.Started);
            Assert.Equal("stop-file", result.Reason);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_stops_at_renewal_cap_without_landing")]
    public void ConductorLoopHandoffStopsAtRenewalCapWithoutLanding()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-renewal-cap");
        try
        {
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(root, renewalCount: 6, maxRenewals: 6),
                new ConductorLoopHandoffRequest(0, TimeSpan.Zero, 0),
                _ => throw new InvalidOperationException("launch should not run"));

            Assert.False(result.Started);
            Assert.Contains("renewal-cap", result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_launches_successor_with_incremented_batch_fresh_logs_and_renewal_arg")]
    public void ConductorLoopHandoffLaunchesSuccessorWithIncrementedBatchFreshLogsAndRenewalArg()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-happy-handoff");
        var previousName = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", "batch25");
            var released = false;
            ConductLoopLaunchRequest? launchRequest = null;
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(
                    root,
                    args: ["conduct", "--loop", "--watch", "--max-duration", "14400", ConductorLoopHandoff.RenewalCountFlag, "4"],
                    renewalCount: 4,
                    release: () => released = true),
                new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                request =>
                {
                    launchRequest = request;
                    return new ConductLoopLaunchResult(4567, request.StdoutPath, request.StderrPath);
                },
                (_, _) =>
                {
                    File.WriteAllText(launchRequest!.StdoutPath, "LOOP_START");
                    return new ConductLoopHandoffVerification(true, true, true, "processAlive=true stdoutLogExists=true loopStartJournaled=true");
                });

            Assert.True(result.Started);
            Assert.True(released);
            Assert.Equal("batch26", launchRequest!.Name);
            Assert.Contains("operator-batch26-", Path.GetFileName(launchRequest.StdoutPath), StringComparison.Ordinal);
            Assert.DoesNotContain(ConductorLoopHandoff.RenewalCountFlag, launchRequest.Args);
            Assert.Equal(5, launchRequest.RenewalCount);
            Assert.Equal(4567, result.ProcessId);
            Assert.Contains("guard=incumbent-held-until-successor-ready", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("spawnPath=injected", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("loopStartJournaled=true", result.VerificationOutcome, StringComparison.Ordinal);

            var records = new SqliteRunEventStore(Path.Combine(root, ".orchestrator", "run-events.db"))
                .ReadSinceAsync()
                .GetAwaiter()
                .GetResult();
            var handoffEvent = Assert.Single(records.Where(record => record.Operation == "LOOP_HANDOFF"));
            Assert.Equal("Started", handoffEvent.Status);
            Assert.Contains(Path.GetFullPath(launchRequest.StdoutPath), handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(launchRequest.StderrPath), handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains("guard=incumbent-held-until-successor-ready", handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains("spawnPath=injected", handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains("loopStartJournaled=true", handoffEvent.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", previousName);
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_handoff_renewal_cap_resets_for_landing_adopted_by_reconcile")]
    public void BatchLoopHandoffRenewalCapResetsForLandingAdoptedByReconcile()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-adopted-landing");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Adopted landing");
            var start = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
            var nowCalls = 0;
            DateTimeOffset UtcNow() => nowCalls++ switch
            {
                0 => start,
                1 => start,
                _ => start.AddSeconds(2)
            };

            var reconciled = false;
            ConductLoopLaunchRequest? launchRequest = null;
            ConductorLoopHandoffRequest? handoffRequest = null;
            var summary = new ConductorBatchLoop(
                measuredSweep: loopKernel =>
                {
                    if (!reconciled)
                    {
                        reconciled = true;
                        loopKernel.CompleteGoal(goal.Id, "Test fixture: landing adopted during first reconcile.");
                    }

                    return null;
                },
                handoffOnMaxDuration: request =>
                {
                    handoffRequest = request;
                    return ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(root, renewalCount: 6, maxRenewals: 6),
                        request,
                        launch =>
                        {
                            launchRequest = launch;
                            return new ConductLoopLaunchResult(4567, launch.StdoutPath, launch.StderrPath);
                        },
                        (_, _) =>
                        {
                            File.WriteAllText(launchRequest!.StdoutPath, "LOOP_START");
                            return new ConductLoopHandoffVerification(true, true, true, "processAlive=true stdoutLogExists=true loopStartJournaled=true");
                        });
                },
                utcNow: UtcNow).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                Path.Combine(root, ConductorBatchLoop.StopFileName),
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => false,
                maxDuration: TimeSpan.FromSeconds(1),
                keepAliveWhenIdle: true);

            Assert.True(summary.Handoff?.Started);
            Assert.NotNull(handoffRequest);
            Assert.Equal(0, handoffRequest!.Done);
            Assert.Equal(1, handoffRequest.LandedGoalDelta);
            Assert.DoesNotContain(ConductorLoopHandoff.RenewalCountFlag, launchRequest!.Args);
            Assert.Equal(0, launchRequest.RenewalCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_journal_failure_is_loud_and_still_launches_successor")]
    public void ConductorLoopHandoffJournalFailureIsLoudAndStillLaunchesSuccessor()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-journal-failure");
        try
        {
            string outText = "";
            var errorText = CaptureConsoleError(() =>
            {
                outText = CaptureConsole(() =>
                {
                    var launched = false;

                    var result = ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(root, runEventStorePath: root),
                        new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                        request =>
                        {
                            launched = true;
                            return new ConductLoopLaunchResult(4567, request.StdoutPath, request.StderrPath);
                        },
                        (_, _) =>
                        {
                            return new ConductLoopHandoffVerification(true, true, true, "processAlive=true stdoutLogExists=true loopStartJournaled=true");
                        });

                    Assert.True(result.Started);
                    Assert.True(launched);
                });
            });

            Assert.Contains("LOOP_HANDOFF_JOURNAL_FAILED", outText, StringComparison.Ordinal);
            Assert.Contains("LOOP_HANDOFF_JOURNAL_FAILED", errorText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_does_not_retry_while_successor_is_alive")]
    public void ConductorLoopHandoffDoesNotRetryWhileSuccessorIsAlive()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-slow-handoff");
        try
        {
            string outText = "";
            var errorText = CaptureConsoleError(() =>
            {
                outText = CaptureConsole(() =>
                {
                    var scaledOldTimeout = TimeSpan.FromMilliseconds(500);
                    var scaledLoopStartDelay = TimeSpan.FromMilliseconds(750);
                    var stopwatch = Stopwatch.StartNew();
                    var attempts = 0;
                    var result = ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(
                            root,
                            verificationTimeout: scaledOldTimeout,
                            loopStartProbe: (_, _) => stopwatch.Elapsed >= scaledLoopStartDelay),
                        new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                        request =>
                        {
                            attempts++;
                            File.WriteAllText(request.StdoutPath, "successor booting");
                            return new ConductLoopLaunchResult(Environment.ProcessId, request.StdoutPath, request.StderrPath);
                        });

                    Assert.True(result.Started);
                    Assert.Equal(1, attempts);
                    Assert.Contains("processAlive=true", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("loopStartJournaled=true", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("terminalReason=loop-start", result.VerificationOutcome, StringComparison.Ordinal);
                });
            });

            Assert.Contains("LOOP_HANDOFF_PENDING", outText, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_HANDOFF_FAILED", outText, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_HANDOFF_FAILED", errorText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_slow_boot_successor_succeeds_without_retry")]
    public void ConductorLoopHandoffSlowBootSuccessorSucceedsWithoutRetry()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-delayed-handoff");
        try
        {
            var oldFixedStartupWindow = TimeSpan.FromSeconds(10);
            var loopStartDelay = TimeSpan.FromMilliseconds(10250);
            var stopwatch = Stopwatch.StartNew();
            var attempts = 0;
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(
                    root,
                    verificationTimeout: TimeSpan.FromSeconds(12),
                    loopStartProbe: (_, _) => stopwatch.Elapsed >= loopStartDelay),
                new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                request =>
                {
                    attempts++;
                    File.WriteAllText(request.StdoutPath, "successor booting");
                    return new ConductLoopLaunchResult(Environment.ProcessId, request.StdoutPath, request.StderrPath);
                });

            Assert.True(result.Started);
            Assert.Equal(1, attempts);
            Assert.Equal(Environment.ProcessId, result.ProcessId);
            Assert.True(stopwatch.Elapsed > oldFixedStartupWindow);
            Assert.Contains("loopStartJournaled=true", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("terminalReason=loop-start", result.VerificationOutcome, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_dead_successor_fails_with_own_evidence")]
    public void ConductorLoopHandoffDeadSuccessorFailsWithOwnEvidence()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-failed-handoff");
        try
        {
            string outText = "";
            var errorText = CaptureConsoleError(() =>
            {
                outText = CaptureConsole(() =>
                {
                    var attempts = 0;
                    var result = ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(root),
                        new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                        request =>
                        {
                            attempts++;
                            return new ConductLoopLaunchResult(int.MaxValue, request.StdoutPath, request.StderrPath);
                        });

                    Assert.False(result.Started);
                    Assert.True(result.Failed);
                    Assert.Equal(1, attempts);
                    Assert.Equal("successor-child-dead", result.Reason);
                    Assert.Contains("attempt=1", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains($"pid={int.MaxValue}", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("guard=incumbent-held-until-successor-ready", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("spawnPath=injected", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("loopStartJournaled=false", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("terminalReason=child-dead", result.VerificationOutcome, StringComparison.Ordinal);
                });
            });

            Assert.Contains("LOOP_HANDOFF_FAILED", outText, StringComparison.Ordinal);
            Assert.Contains("LOOP_HANDOFF_FAILED", errorText, StringComparison.Ordinal);

            var records = new SqliteRunEventStore(Path.Combine(root, ".orchestrator", "run-events.db"))
                .ReadSinceAsync()
                .GetAwaiter()
                .GetResult();
            Assert.Equal(1, records.Count(record => record.Operation == "LOOP_HANDOFF" && record.Status == "Failed"));
            Assert.Contains(records, record =>
                record.Operation == "LOOP_HANDOFF" &&
                record.Status == "Failed" &&
                record.Detail is not null &&
                record.Detail.Contains($"pid={int.MaxValue}", StringComparison.Ordinal) &&
                record.Detail.Contains("terminalReason=child-dead", StringComparison.Ordinal));
            Assert.Contains(records, record =>
                record.Operation == "LOOP_HANDOFF" &&
                record.Status == "Escalated" &&
                record.Detail is not null &&
                record.Detail.Contains("successor-child-dead", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_handoff_failure_emits_failed_event_not_skipped")]
    public void BatchLoopHandoffFailureEmitsFailedEventNotSkipped()
    {
        var (kernel, _) = SimpleGoal();
        var root = CreateTempDirectory("mcg-conduct-loop-failed-event");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        try
        {
            var summary = new ConductorBatchLoop(
                handoffOnMaxDuration: _ => ConductorLoopHandoffResult.FailedStart(
                    "successor-verification-failed",
                    Path.Combine(root, "successor.out.log"),
                    Path.Combine(root, "successor.err.log"),
                    "processAlive=false stdoutLogExists=false loopStartJournaled=false"),
                conductEventLogWriter: writer)
                .Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxDuration: TimeSpan.Zero);

            Assert.True(summary.Handoff?.Failed);
            var log = File.ReadAllText(logPath);
            Assert.Contains("LOOP_HANDOFF_FAILED", log, StringComparison.Ordinal);
            Assert.Contains("successor.out.log", log, StringComparison.Ordinal);
            Assert.Contains("loopStartJournaled=false", log, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_HANDOFF_SKIPPED", log, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_windows_launcher_uses_direct_process_with_explicit_log_handles")]
    public void ConductorLoopHandoffWindowsLauncherUsesDirectProcessWithExplicitLogHandles()
    {
        var commandLine = ConductorLoopHandoff.BuildWindowsProcessCommandLine(
            ["dotnet", @"C:\repo\src\Mcg.AgentOrchestrator.App.dll", "conduct", "--loop", "--watch"]);

        Assert.Contains(@"""dotnet"" ""C:\repo\src\Mcg.AgentOrchestrator.App.dll"" ""conduct"" ""--loop"" ""--watch""", commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", commandLine, StringComparison.OrdinalIgnoreCase);

        var source = File.ReadAllText(Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "src", "Mcg.AgentOrchestrator.App", "Orchestration", "ConductorLoopHandoff.cs"));
        Assert.Contains("CreateBreakawayFromJob", source, StringComparison.Ordinal);
        Assert.Contains("CreateInheritedOutputFile", source, StringComparison.Ordinal);
        Assert.Contains("UseStdHandles", source, StringComparison.Ordinal);
        Assert.Contains("bInheritHandles: true", source, StringComparison.Ordinal);
        Assert.Contains("ProcThreadAttributeHandleList", source, StringComparison.Ordinal);
        Assert.Contains("ExtendedStartupInfoPresent", source, StringComparison.Ordinal);
        Assert.Equal(0x01080600u, ConductorLoopHandoff.WindowsSuccessorCreationFlags);
        Assert.DoesNotContain("WindowsCreationFlags.CreateNoWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsCreationFlags.DetachedProcess", source, StringComparison.Ordinal);

        var jobSource = File.ReadAllText(Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "src", "Mcg.AgentOrchestrator.Infrastructure", "Processes", "OwnedProcessGroup.cs"));
        Assert.Contains("JobObjectLimitBreakawayOk", jobSource, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_windows_launcher_inherits_redirected_stdout_handle")]
    public void ConductorLoopHandoffWindowsLauncherInheritsRedirectedStdoutHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory("mcg-conduct-loop-stdout-handoff");
        int? processId = null;
        var suppressionScopeActive = false;
        try
        {
            var stdoutPath = Path.Combine(root, "successor.out.log");
            var stderrPath = Path.Combine(root, "successor.err.log");
            var stdoutMarker = "handoff-stdout-marker-" + Guid.NewGuid().ToString("N");
            var stderrMarker = "handoff-stderr-marker-" + Guid.NewGuid().ToString("N");
            var scriptPath = Path.Combine(root, "write-marker.cmd");
            File.WriteAllText(scriptPath, $"@echo {stdoutMarker}{Environment.NewLine}@echo {stderrMarker} 1>&2{Environment.NewLine}");
            var cmdPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            var result = ConductorLoopHandoff.LaunchDetachedWindows(
                new ConductLoopLaunchRequest("batch1", [], stdoutPath, stderrPath, root, 0),
                [
                    cmdPath,
                    "/d",
                    "/c",
                    scriptPath
                ],
                acquireConsoleSuppression: () =>
                {
                    suppressionScopeActive = true;
                    return new ProcessTreeGuiSuppression.ConsoleSpawnScope(
                        hiddenConsoleAcquired: true,
                        onDispose: () => suppressionScopeActive = false);
                },
                beforeCreateProcess: () => Assert.True(
                    suppressionScopeActive,
                    "Hidden-console suppression was disposed before CreateProcessW."));

            Assert.True(result.ProcessId > 0);
            Assert.False(suppressionScopeActive);
            processId = result.ProcessId;
            var conductEventsPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
            Assert.True(File.Exists(conductEventsPath), "Windows handoff did not journal its pre-spawn diagnostic.");
            var conductEvents = File.ReadAllText(conductEventsPath);
            Assert.Contains("\"eventKind\":\"loop-handoff-spawn\"", conductEvents, StringComparison.Ordinal);
            Assert.Contains("spawnPath=windows-createprocess", conductEvents, StringComparison.Ordinal);
            Assert.Matches("incumbentConsole=(present|absent)", conductEvents);
            Assert.Matches("incumbentConsoleAttached=(true|false)", conductEvents);
            Assert.Contains("suppression=hidden-console-acquired", conductEvents, StringComparison.Ordinal);
            Assert.True(WaitUntil(
                () => File.Exists(stdoutPath) && ReadAllTextShared(stdoutPath).Contains(stdoutMarker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
                $"stdout did not contain marker. child={DescribeProcess(processId.Value)} stdout={TryReadAllTextShared(stdoutPath)} stderr={TryReadAllTextShared(stderrPath)}");
            Assert.True(WaitUntil(
                () => File.Exists(stderrPath) && ReadAllTextShared(stderrPath).Contains(stderrMarker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
                $"stderr did not contain marker. child={DescribeProcess(processId.Value)} stdout={TryReadAllTextShared(stdoutPath)} stderr={TryReadAllTextShared(stderrPath)}");
        }
        finally
        {
            if (processId is { } pid)
            {
                TryKillProcess(pid);
            }

            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void ConductorLoopHandoffSuppressionFailureStillStartsSuccessor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory("mcg-conduct-loop-suppression-failure");
        int? processId = null;
        try
        {
            var stdoutPath = Path.Combine(root, "successor.out.log");
            var stderrPath = Path.Combine(root, "successor.err.log");
            var marker = "handoff-fail-open-marker-" + Guid.NewGuid().ToString("N");
            var scriptPath = Path.Combine(root, "write-marker.cmd");
            File.WriteAllText(scriptPath, $"@echo {marker}{Environment.NewLine}");
            var cmdPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");

            var result = ConductorLoopHandoff.LaunchDetachedWindows(
                new ConductLoopLaunchRequest("batch1", [], stdoutPath, stderrPath, root, 0),
                [cmdPath, "/d", "/c", scriptPath],
                acquireConsoleSuppression: () => throw new System.ComponentModel.Win32Exception(5, "synthetic suppression failure"));

            Assert.True(result.ProcessId > 0);
            processId = result.ProcessId;
            Assert.True(WaitUntil(
                () => File.Exists(stdoutPath) && ReadAllTextShared(stdoutPath).Contains(marker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
                $"successor did not start after suppression failure. stdout={TryReadAllTextShared(stdoutPath)} stderr={TryReadAllTextShared(stderrPath)}");

            var conductEvents = File.ReadAllText(Path.Combine(root, ConductEventLogWriter.CurrentFileName));
            Assert.Contains("\"eventKind\":\"loop-handoff-console-suppression-failed\"", conductEvents, StringComparison.Ordinal);
            Assert.Contains("error=Win32Exception", conductEvents, StringComparison.Ordinal);
            Assert.Contains("nativeError=5", conductEvents, StringComparison.Ordinal);
            Assert.Contains("suppression=acquisition-failed", conductEvents, StringComparison.Ordinal);
        }
        finally
        {
            if (processId is { } pid)
            {
                TryKillProcess(pid);
            }

            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_successor_survives_parent_job_exit_and_emits_loop_start")]
    public void ConductorLoopHandoffSuccessorSurvivesParentJobExitAndEmitsLoopStart()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The production failure mode is Windows job-object kill-on-close inheritance.
            return;
        }

        var root = CreateTempDirectory("mcg-conduct-loop-runtime-handoff");
        var appAssembly = typeof(ConductorBatchLoop).Assembly.Location;
        Process? parent = null;
        int? successorPid = null;
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = root
            };
            startInfo.ArgumentList.Add(appAssembly);
            startInfo.ArgumentList.Add("conduct");
            startInfo.ArgumentList.Add("--loop");
            startInfo.ArgumentList.Add("--daemon");
            startInfo.ArgumentList.Add("--watch");
            startInfo.ArgumentList.Add("--poll-seconds");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--max-duration");
            startInfo.ArgumentList.Add("3");
            startInfo.ArgumentList.Add("--quiet");
            startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = root;
            startInfo.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Path.Combine(root, "project-registry");
            startInfo.Environment["MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME"] = "batch98";
            foreach (var key in startInfo.Environment.Keys
                         .Where(key => key.StartsWith("MCG_ORCHESTRATOR_HANDOFF_", StringComparison.Ordinal))
                         .ToArray())
            {
                // The acceptance worker may itself be a conductor successor. This child is the
                // incumbent under test and must not inherit that outer authority-transfer request.
                startInfo.Environment.Remove(key);
            }

            var bootstrapToken = Guid.NewGuid().ToString("N");
            var bootstrapActivationPath = Path.Combine(root, "bootstrap.activate");
            File.WriteAllText(bootstrapActivationPath, bootstrapToken);
            startInfo.Environment["MCG_ORCHESTRATOR_HANDOFF_READY_PATH"] = Path.Combine(root, "bootstrap.ready");
            startInfo.Environment["MCG_ORCHESTRATOR_HANDOFF_ACTIVATE_PATH"] = bootstrapActivationPath;
            startInfo.Environment["MCG_ORCHESTRATOR_HANDOFF_TOKEN"] = bootstrapToken;
            startInfo.Environment["MCG_ORCHESTRATOR_HANDOFF_INCUMBENT_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["MCG_ORCHESTRATOR_HANDOFF_WAIT_SECONDS"] = "5";

            parent = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start parent conductor process.");
            var stdoutTask = parent.StandardOutput.ReadToEndAsync();
            var stderrTask = parent.StandardError.ReadToEndAsync();
            using (var parentJob = OwnedProcessGroup.Attach(parent))
            {
                Assert.True(parent.WaitForExit(30000), "Parent conductor did not reach max-duration handoff.");
                parentJob.Dispose();
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            Assert.True(parent.ExitCode == 0, $"Parent conductor exited {parent.ExitCode}. stdout={stdout} stderr={stderr}");
            successorPid = ParseHandoffProcessId(stdout);

            Assert.True(IsProcessRunning(successorPid.Value), $"Successor pid {successorPid.Value} did not survive parent job close. stdout={stdout} stderr={stderr}");
            Assert.Contains("guard=incumbent-held-until-successor-ready", stdout, StringComparison.Ordinal);
            Assert.Contains("spawnPath=windows-createprocess", stdout, StringComparison.Ordinal);
            Assert.Contains("breakawayRequested=true", stdout, StringComparison.Ordinal);
            Assert.Contains("breakawaySucceeded=true", stdout, StringComparison.Ordinal);

            var logDirectory = Path.Combine(root, ".orchestrator", "logs");
            var conductEventsPath = Path.Combine(logDirectory, ConductEventLogWriter.CurrentFileName);
            Assert.True(WaitUntil(() =>
                File.Exists(conductEventsPath) &&
                ReadAllTextShared(conductEventsPath).Contains("LOOP_START", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15)), $"Successor did not journal LOOP_START. stdout={stdout} stderr={stderr}");

            Assert.True(WaitUntil(
                () => Directory.GetFiles(logDirectory, "operator-batch99-*.out.log").Length > 0,
                TimeSpan.FromSeconds(10)),
                $"Successor did not create batch99 stdout log. stdout={stdout} stderr={stderr}");
        }
        finally
        {
            File.WriteAllText(Path.Combine(root, ConductorBatchLoop.StopFileName), "stop");
            if (successorPid is { } pid && !WaitUntil(() => !IsProcessRunning(pid), TimeSpan.FromSeconds(10)))
            {
                TryKillProcess(pid);
            }

            if (parent is not null)
            {
                TryKillProcess(parent.Id);
                parent.Dispose();
            }

            TryDeleteDirectory(root);
        }
    }

    private static int ParseHandoffProcessId(string output)
    {
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("LOOP_HANDOFF tick=", StringComparison.Ordinal) ||
                !TryReadTokenValue(line, "pid=", out var pidText) ||
                !int.TryParse(pidText, out var pid))
            {
                continue;
            }

            return pid;
        }

        throw new InvalidOperationException("Parent conductor output did not include a LOOP_HANDOFF pid.");
    }

    private static bool TryReadTokenValue(string line, string token, out string value)
    {
        var start = line.IndexOf(token, StringComparison.Ordinal);
        if (start < 0)
        {
            value = string.Empty;
            return false;
        }

        start += token.Length;
        var end = line.IndexOf(' ', start);
        value = end < 0 ? line[start..] : line[start..end];
        return value.Length > 0;
    }

    private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return predicate();
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string TryReadAllTextShared(string path)
    {
        try
        {
            return File.Exists(path) ? ReadAllTextShared(path) : "<missing>";
        }
        catch (Exception ex)
        {
            return $"<{ex.GetType().Name}:{ex.Message}>";
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string DescribeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(100))
            {
                return $"pid={processId} running";
            }

            return $"pid={processId} exit={process.ExitCode}";
        }
        catch (ArgumentException)
        {
            return $"pid={processId} missing";
        }
        catch (InvalidOperationException)
        {
            return $"pid={processId} unavailable";
        }
    }

    private static void StartProcess(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        DateTimeOffset dispatchedAt,
        string baseCommit,
        int processId = 111)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-watch-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var command = $"worker {task.RequiredRole}";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", command, root, dispatchedAt, BaseCommit: baseCommit));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
            processId,
            command,
            root,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "exit.txt"),
            dispatchedAt,
            null,
            null,
            OwnedProcessIds: [processId]));
    }

    private static void CompleteDispatchedTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        DateTimeOffset completedAt,
        string resultCommit)
    {
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, resultCommit);
        var process = kernel.GetTask(goal.Id, task.Id).LastProcess!;
        kernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            process with { CompletedAt = completedAt, ExitCode = 0 },
            null);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("manual", process.WorkingDirectory, 0, "ok", "", completedAt));
    }

    private static ConductorAutonomyPolicy PolicyEscalatingAtVerified()
    {
        var transitionMap = ConductorAutonomyPolicy.Conservative.TransitionMap.ToDictionary();
        transitionMap[GoalLifecycleState.Verified] = ConductorTransitionDecision.Escalate;
        return ConductorAutonomyPolicy.Conservative with
        {
            Name = "VerifiedEscalates",
            TransitionMap = transitionMap
        };
    }

    private static ConductorWatchProgressReporter FakeWatchReporter(
        DateTimeOffset now,
        long stdoutBytes,
        long stderrBytes,
        TimeSpan idle,
        IReadOnlyList<int> ownedPids,
        IReadOnlyCollection<int> alivePids,
        IReadOnlyList<string> files) =>
        new(
            readHeartbeat: (process, observedAt) => Heartbeat(process, observedAt, stdoutBytes, stderrBytes, idle, ownedPids),
            readChanges: (_, _) => new DispatchLiveChangeSnapshot(files, files.Take(3).ToArray(), Math.Max(0, files.Count - 3)),
            isProcessAlive: alivePids.Contains,
            now: () => now);

    private static DispatchHeartbeatStatus Heartbeat(
        TaskProcessRecord process,
        DateTimeOffset observedAt,
        long stdoutBytes,
        long stderrBytes,
        TimeSpan idle,
        IReadOnlyList<int> ownedPids) =>
        new(
            BackgroundDispatchRunner.GetHeartbeatPath(process),
            true,
            null,
            process.ProcessId,
            null,
            ownedPids,
            "running",
            observedAt,
            observedAt - idle,
            TimeSpan.Zero,
            idle,
            stdoutBytes,
            stderrBytes);

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

    [Xunit.Fact(DisplayName = "BatchLoop_dependent_goal_advances_when_completed_dependency_is_metadata_only")]
    public void BatchLoopDependentGoalAdvancesWhenCompletedDependencyIsMetadataOnly()
    {
        var completedDependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active dependent goal");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [completedDependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownCompletedDependencyGoals([completedDependencyId]);

        var createdWorkspaces = new List<GoalId>();
        var driver = MakeDriver(createWorkspace: goal =>
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
        Assert.Equal(0, summary.Held);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_dependent_goal_holds_when_parked_dependency_is_metadata_only")]
    public void BatchLoopDependentGoalHoldsWhenParkedDependencyIsMetadataOnly()
    {
        var parkedDependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active dependent goal");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [parkedDependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(parkedDependencyId, GoalStatus.Parked.ToString())
        ]);

        var createdWorkspaces = new List<GoalId>();
        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(createWorkspace: goal =>
            {
                createdWorkspaces.Add(goal.Id);
                return "/tmp/workspace";
            }),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.DoesNotContain(kernel.Goals, goal => goal.Id == parkedDependencyId);
        Assert.Equal(1, summary.Held);
        Assert.Empty(createdWorkspaces);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_dependent_goal_holds_when_dependency_is_completed_without_landing")]
    public void BatchLoopDependentGoalHoldsWhenDependencyIsCompletedWithoutLanding()
    {
        var dependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active dependent goal");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [dependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(dependencyId, GoalStatus.Completed.ToString())
        ]);

        var createdWorkspaces = new List<GoalId>();
        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(createWorkspace: goal =>
            {
                createdWorkspaces.Add(goal.Id);
                return "/tmp/workspace";
            }),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.DoesNotContain(kernel.Goals, goal => goal.Id == dependencyId);
        Assert.False(kernel.IsKnownCompletedDependencyGoal(dependencyId));
        Assert.Equal(0, summary.Advanced);
        Assert.Equal(1, summary.Held);
        Assert.Empty(createdWorkspaces);
    }

    [Xunit.Theory(DisplayName = "BatchLoop_terminal_unlanded_dependency_escalates_without_worker_start")]
    [Xunit.InlineData("Failed")]
    [Xunit.InlineData("Retired")]
    public void BatchLoopTerminalUnlandedDependencyEscalatesWithoutWorkerStart(string terminalStatus)
    {
        var dependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "dependent");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [dependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(dependencyId, terminalStatus)
        ]);
        var workerStarts = 0;
        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(
                createWorkspace: _ => throw new Xunit.Sdk.XunitException("held goal must not create a workspace"),
                dispatchAndStart: _ =>
                {
                    workerStarts++;
                    return DispatchStartOutcome.Started();
                }),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(0, workerStarts);
        Assert.Equal(1, summary.Escalated);
        Assert.Contains(
            kernel.GetGoal(active.Id).Timeline,
            progress => progress.Message.Contains(
                $"dependency-terminal-without-landing: {dependencyId.Value[..8]} state={terminalStatus}",
                StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_dependency_added_after_goal_start_does_not_retroactively_hold_goal")]
    public void BatchLoopDependencyAddedAfterGoalStartDoesNotRetroactivelyHoldGoal()
    {
        var dependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "already started dependent");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [dependencyId.Value] }
                    : goal)
                .ToArray()
        });
        var rehydrated = kernel.GetGoal(active.Id);
        StartProcess(
            kernel,
            rehydrated,
            rehydrated.Tasks.Single(),
            DateTimeOffset.Parse("2026-07-30T00:00:00Z"),
            "base");
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(dependencyId, GoalStatus.Completed.ToString())
        ]);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(createWorkspace: _ => throw new Xunit.Sdk.XunitException("running goal must not recreate a workspace")),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(0, summary.Escalated);
        Assert.Equal(1, summary.Held);
        Assert.DoesNotContain(
            kernel.GetGoal(active.Id).Timeline,
            progress => progress.Message.Contains("dependency-terminal-without-landing", StringComparison.Ordinal));
        Assert.DoesNotContain(
            kernel.GetGoal(active.Id).Timeline,
            progress => progress.Message.Contains("waiting on dependency", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_landed_dependency_remains_satisfied_after_terminal_metadata_changes")]
    public void BatchLoopLandedDependencyRemainsSatisfiedAfterTerminalMetadataChanges()
    {
        var dependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "dependent");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [dependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownCompletedDependencyGoals([dependencyId]);
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(dependencyId, GoalStatus.Superseded.ToString())
        ]);
        var createdWorkspaces = new List<GoalId>();

        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(createWorkspace: goal =>
            {
                createdWorkspaces.Add(goal.Id);
                return "/tmp/workspace";
            }),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.Contains(active.Id, createdWorkspaces);
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

    // ── Persistence: a loop dispatch must be durable across reload ────────
    // Regression for the autonomy-blocker found 2026-06-18. `conduct --loop[ --watch]` runs the
    // ENTIRE loop inside one state transaction (CliPersistentStateRunner.TransactAsync), which
    // only commits when the command returns. ConductorBatchLoop.Run never persists per tick, so
    // a long-running watch loop never commits its dispatches and a killed loop rolls them all
    // back. Observed live: Researcher 5825a584 ran four times on disk (exit 0 each) yet the
    // timeline shows zero TaskDispatchRecorded/TaskProcessStarted events after the Planner — so
    // every tick re-dispatched it and the goal could never advance.
    // The fix runs the loop outside the transaction and checkpoints each tick via persistTick, so a
    // started dispatch is durable the moment its tick completes. This test fails if that wiring breaks.
    [Xunit.Fact(DisplayName = "ConductorBatchLoop_persists_each_tick_so_dispatch_survives_reload")]
    public async Task LoopPersistsEachTickDispatchSurvivesReload()
    {
        var db = Path.Combine(Path.GetTempPath(), $"mcg-loop-persist-{Guid.NewGuid():N}.db");
        var repo = OpenStateRepository(db);

        // Seed a single-task goal and commit it.
        GoalId goalId = default;
        TaskId taskId = default;
        await repo.TransactAsync((k, _) =>
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(k, DefaultAgents(), "Durable dispatch goal");
            goalId = goal.Id;
            taskId = goal.Tasks.Single().Id;
            return Task.FromResult((true, true));
        });

        // Drive the loop as the fixed conduct --loop path does: load the kernel, then run the loop
        // outside the wrapping transaction so each tick can persist independently.
        var kernel = await repo.LoadAsync();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.Single();
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("claude-cli", "claude -p plan", "C:\\wt", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, task.Id,
                    new TaskProcessRecord(4242, "claude -p plan", "C:\\wt", "out.log", "err.log", "exit.txt",
                        DateTimeOffset.UtcNow, null, null));
                return DispatchStartOutcome.Started();
            });

        // The loop runs outside any transaction and checkpoints each tick via persistTick. The
        // process then "dies" with no final save, so durability must come from the per-tick save.
        new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 1,
            persistTick: k => repo.SaveAsync(k).GetAwaiter().GetResult());

        // The dispatch the loop performed must survive so reconcile can recognize it instead of
        // re-dispatching the same task forever.
        // If LastProcess is null after reload, the loop's dispatch was never persisted — so the
        // conductor re-dispatches the same task on every tick and the goal can never advance.
        var reloaded = await repo.LoadAsync();
        var task = reloaded.GetTask(goalId, taskId);
        Assert.True(task.LastProcess is not null);
    }

    [Xunit.Fact(DisplayName = "ConductorBatchLoop_critical_dispatch_start_persists_records_before_slot_release_without_outer_retry")]
    public async Task CriticalDispatchStartPersistsRecordsBeforeSlotReleaseWithoutOuterRetry()
    {
        var db = Path.Combine(Path.GetTempPath(), $"mcg-loop-dispatch-start-{Guid.NewGuid():N}.db");
        var repo = OpenStateRepository(db);

        GoalId goalId = default;
        TaskId taskId = default;
        await repo.TransactAsync((k, _) =>
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(k, DefaultAgents(), "Critical dispatch start goal");
            goalId = goal.Id;
            taskId = goal.Tasks.Single().Id;
            return Task.FromResult((true, true));
        });

        var kernel = await repo.LoadAsync();
        var attempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.Single();
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("claude-cli", "claude -p plan", "C:\\wt", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, task.Id,
                    new TaskProcessRecord(4242, "claude -p plan", "C:\\wt", "out.log", "err.log", "exit.txt",
                        DateTimeOffset.UtcNow, null, null));
                ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                    (checkpoint, changedGoalIds) =>
                    {
                        attempts++;
                        var changed = changedGoalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
                        var snaps = checkpoint.ExportSnapshot().Goals
                            .Where(goal => changed.Contains(goal.Id))
                            .ToArray();
                        repo.SaveGoalSnapshotsAsync(snaps, CancellationToken.None).GetAwaiter().GetResult();
                    },
                    kernel,
                    g.Id,
                    task.Id);
                return DispatchStartOutcome.Started();
            });

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            persistGoalTick: (_, _) => { });

        var reloaded = await repo.LoadAsync();
        var task = reloaded.GetTask(goalId, taskId);
        Assert.Equal(1, attempts);
        Assert.True(task.LastDispatch is not null);
        Assert.True(task.LastProcess is not null);
        Assert.Equal(1, reloaded.Goals.Single(g => g.Id == goalId).Timeline.Count(evt =>
            evt.TaskId == taskId && evt.Kind == ProgressKind.TaskDispatchRecorded));
        Assert.Equal(1, reloaded.Goals.Single(g => g.Id == goalId).Timeline.Count(evt =>
            evt.TaskId == taskId && evt.Kind == ProgressKind.TaskProcessStarted));
    }

    [Xunit.Fact(DisplayName = "ConductorBatchLoop_final_checkpoint_preserves_cli_retry_written_after_last_tick")]
    public async Task FinalCheckpointPreservesCliRetryWrittenAfterLastTick()
    {
        var db = Path.Combine(Path.GetTempPath(), $"mcg-loop-final-checkpoint-merge-{Guid.NewGuid():N}.db");
        var repo = OpenStateRepository(db);
        var developerTaskId = TaskId.New();
        var testerTaskId = TaskId.New();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect final checkpoint retry", [
            new TaskSpec(developerTaskId, "Implement final checkpoint persistence", AgentRole.Developer),
            new TaskSpec(testerTaskId, "Test final checkpoint persistence", AgentRole.Tester)
        ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskVerification(
            goal.Id,
            testerTaskId,
            new TaskVerificationRecord("manual", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
        await repo.SaveAsync(kernel);

        kernel = await repo.LoadAsync();
        var baselines = kernel.ExportSnapshot().Goals.ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        var persistCalls = 0;
        var injectedRetry = false;
        void PersistGoalTick(AgentOrchestratorKernel checkpoint, IReadOnlyCollection<GoalId> changedGoalIds)
        {
            persistCalls++;
            if (persistCalls == 2 && !injectedRetry)
            {
                injectedRetry = true;
                repo.TransactGoalAsync(goal.Id, (storedSnapshot, _) =>
                {
                    var storedKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([storedSnapshot!], []));
                    storedKernel.RetryTask(goal.Id, testerTaskId, "operator retry between last tick and final checkpoint");
                    return Task.FromResult((
                        true,
                        storedKernel.ExportSnapshot().Goals.Single(),
                        true));
                }).GetAwaiter().GetResult();
            }

            var changed = changedGoalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            var requests = checkpoint.ExportSnapshot().Goals
                .Where(snapshot => changed.Contains(snapshot.Id))
                .Select(snapshot => new GoalSnapshotSaveRequest(baselines[snapshot.Id], snapshot))
                .ToArray();
            var results = repo.SaveGoalSnapshotsWithMergeAsync(requests).GetAwaiter().GetResult();
            foreach (var result in results)
            {
                if (result.PersistedSnapshot is not null)
                    baselines[result.GoalId] = result.PersistedSnapshot;
            }
        }

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                kernel.RecordTaskDispatch(g.Id, developerTaskId,
                    new TaskDispatchRecord("claude-cli", "claude -p work", "C:\\wt", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, developerTaskId,
                    new TaskProcessRecord(4242, "claude -p work", "C:\\wt", "out.log", "err.log", "exit.txt",
                        DateTimeOffset.UtcNow, null, null));
                return DispatchStartOutcome.Started();
            });

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            persistGoalTick: PersistGoalTick);

        var reloaded = await repo.LoadAsync();
        var developerTask = reloaded.GetTask(goal.Id, developerTaskId);
        var testerTask = reloaded.GetTask(goal.Id, testerTaskId);
        Assert.True(injectedRetry);
        Assert.True(persistCalls >= 2);
        Assert.Equal(WorkTaskStatus.Running, developerTask.Status);
        Assert.NotNull(developerTask.LastProcess);
        Assert.Equal(WorkTaskStatus.Assigned, testerTask.Status);
        Assert.Contains(reloaded.GetGoal(goal.Id).Timeline, evt =>
            evt.TaskId == testerTaskId &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("operator retry between last tick and final checkpoint", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorBatchLoop_critical_dispatch_start_exhaustion_persists_neither_record")]
    public async Task CriticalDispatchStartExhaustionPersistsNeitherRecord()
    {
        var db = Path.Combine(Path.GetTempPath(), $"mcg-loop-dispatch-start-fail-{Guid.NewGuid():N}.db");
        var repo = OpenStateRepository(db);

        GoalId goalId = default;
        TaskId taskId = default;
        await repo.TransactAsync((k, _) =>
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(k, DefaultAgents(), "Critical dispatch start failure goal");
            goalId = goal.Id;
            taskId = goal.Tasks.Single().Id;
            return Task.FromResult((true, true));
        });

        var kernel = await repo.LoadAsync();
        var task = kernel.GetTask(goalId, taskId);
        kernel.RecordTaskDispatch(goalId, taskId,
            new TaskDispatchRecord("claude-cli", "claude -p plan", "C:\\wt", DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(goalId, taskId,
            new TaskProcessRecord(4242, "claude -p plan", "C:\\wt", "out.log", "err.log", "exit.txt",
                DateTimeOffset.UtcNow, null, null));

        var attempts = 0;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                (_, _) =>
                {
                    attempts++;
                    throw SqliteBusy();
                },
                kernel,
                goalId,
                task.Id));

        var reloaded = await repo.LoadAsync();
        var reloadedTask = reloaded.GetTask(goalId, taskId);
        Assert.Contains("DISPATCH_RECORD_WRITE_FAILED", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ConductorBatchLoop.DefaultMaxBusyWriteAttempts, attempts);
        Assert.True(reloadedTask.LastDispatch is null);
        Assert.True(reloadedTask.LastProcess is null);
    }

    // ── Auto-retry: transient acceptance flake recovers on retry ─────────

    [Xunit.Fact(DisplayName = "BatchLoop_AutoRetry_flakeOnFirstAttempt_recoverOnRetry")]
    public void BatchLoop_AutoRetry_FlakeOnFirstAttemptRecoverOnRetry()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single()); // goal → Completed → Verified state

        var attempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), // IsMerged=false → Verified state
            runAcceptance: _ => { attempts++; return attempts > 1; }, // fail 1st, pass on retry
            writeEscalation: (_, _, _) => { });

        var stopFile = NoStopPath();
        // 1 tick with maxIterations=1: initial fail → 1 retry (passes) → Advanced
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 1);

        Assert.Equal(2, attempts);      // initial + 1 retry
        Assert.Equal(1, summary.Retried);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(1, summary.Advanced);
    }

    // ── Auto-retry: persistent failure escalates after N retries ─────────

    [Xunit.Fact(DisplayName = "BatchLoop_AutoRetry_persistentFailureEscalatesAfterNRetries")]
    public void BatchLoop_AutoRetry_PersistentFailureEscalatesAfterNRetries()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());

        var attempts = 0;
        var escalationWritten = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ => { attempts++; return false; }, // always fail
            writeEscalation: (_, _, _) => { escalationWritten = true; });

        var stopFile = NoStopPath();
        // maxVerifyRetries=2 → 1 initial + 2 retries = 3 total acceptance calls before escalation
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
            maxIterations: 1, maxVerifyRetries: 2);

        Assert.Equal(3, attempts);      // 1 initial + 2 retries
        Assert.Equal(2, summary.Retried);
        Assert.True(escalationWritten);
        Assert.Equal(1, summary.Escalated);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_skips_prior_verified_acceptance_escalation_without_spending_retry_budget")]
    public void BatchLoopSkipsPriorVerifiedAcceptanceEscalationWithoutSpendingRetryBudget()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");

        var acceptanceAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ =>
            {
                acceptanceAttempts++;
                return false;
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            maxVerifyRetries: 2);

        Assert.Equal(0, acceptanceAttempts);
        Assert.Equal(0, summary.Retried);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(0, summary.Advanced);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_treats_cleaned_up_goal_as_done_despite_prior_verified_acceptance_escalation")]
    public void BatchLoopTreatsCleanedUpGoalAsDoneDespitePriorVerifiedAcceptanceEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");

        var acceptanceAttempts = 0;
        var ticks = new List<BatchTickSummary>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true),
            runAcceptance: _ =>
            {
                acceptanceAttempts++;
                return false;
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            maxVerifyRetries: 2,
            onTick: ticks.Add);

        Assert.Equal(0, acceptanceAttempts);
        Assert.Equal(0, summary.Retried);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(0, summary.Advanced);
        Assert.Empty(ticks);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_operator_retry_clears_prior_verified_acceptance_escalation")]
    public void BatchLoopOperatorRetryClearsPriorVerifiedAcceptanceEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");
        kernel.RetryTask(goal.Id, task.Id, "Operator retry after fixing acceptance failure.");

        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                dispatches++;
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\goal", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, dispatches);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(0, summary.Retried);
    }

    // ── Regression: flake recovery followed by ownership-blocked dispatch must not escalate ──
    // Before Fix 1+4, the conductor auto-recovered a watchdog-reaped Developer dispatch via
    // _retryTask (setting the task to Assigned), then on the NEXT tick called _dispatchAndStart
    // which returned EmptyBatch (high-risk ownership under Conservative policy). The empty-batch
    // path immediately escalated → SetAside(LifecycleEscalation) → permanently stuck, even though
    // `readiness` said "Proceed". Fix 1: dispatch in the SAME tick as recovery. Fix 4: on
    // EmptyBatch with assigned tasks, return Held instead of Escalate.

    [Xunit.Fact(DisplayName = "BatchLoop_flake_recovery_then_empty_batch_holds_not_escalates_no_paid_worker")]
    public void BatchLoopFlakeRecoveryThenEmptyBatchHoldsNotEscalatesNoPaidWorker()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("src/Mcg.AgentOrchestrator.Core/Fix something");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var planner = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Planner);
        var researcher = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Researcher);
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var now = DateTimeOffset.UtcNow;

        // Advance Planner/Researcher to Completed so Developer is the current stage.
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Planner done.");
        kernel.ReportTaskProgress(goal.Id, researcher.Id, WorkTaskStatus.Completed, "Researcher done.");

        // Simulate a watchdog reap: Developer was dispatched but produced zero-byte stdout (empty flake).
        kernel.RecordTaskDispatch(goal.Id, developer.Id,
            new TaskDispatchRecord("test-worker", "dev.exe", "C:\\goal", now));
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id,
            new TaskVerificationRecord(
                "dev.exe",
                "C:\\goal",
                1,
                "",
                "",
                now,
                DispatchStartedAt: now - TimeSpan.FromSeconds(30)));

        Assert.Equal(WorkTaskStatus.Failed, kernel.GetTask(goal.Id, developer.Id).Status);
        Assert.Equal(1, developer.EmptyOutputRetryCount);

        var escalated = false;
        var dispatchAttempts = 0;
        var paidWorkerCount = 0;

        // _dispatchAndStart returns EmptyBatch to simulate high-risk ownership blocking
        // under a Conservative policy (the typical trigger for this bug).
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => paidWorkerCount,
            dispatchAndStart: _ =>
            {
                dispatchAttempts++;
                return DispatchStartOutcome.EmptyBatch(
                    "No tasks dispatched; all ready tasks require operator approval: high-risk ownership (src/Mcg.AgentOrchestrator.Core/)");
            },
            writeEscalation: (_, _, _) => { escalated = true; },
            retryTask: (gid, tid, msg) => kernel.RetryTask(gid, tid, msg));

        // Run one tick: Failed state → flake recovery → immediate dispatch attempt → EmptyBatch → Held.
        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative,
            NoStopPath(), maxIterations: 1);

        Assert.False(escalated); // must not escalate when empty batch follows flake recovery
        Assert.Equal(0, paidWorkerCount); // no paid worker started (dispatch returned EmptyBatch)
        Assert.Equal(1, dispatchAttempts); // dispatch was attempted in the same tick as recovery (Fix 1)
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, developer.Id).Status);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_readmits_awaiting_clarification_goal_when_blocker_clears")]
    public void BatchLoopReadmitsAwaitingClarificationGoalWhenBlockerClears()
    {
        var (kernel, goal) = SimpleGoal();
        var hasOpenClarification = true;
        var workspaceCreates = 0;
        var escalations = 0;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(HasOpenClarification: hasOpenClarification),
            createWorkspace: _ =>
            {
                workspaceCreates++;
                return "C:\\goal";
            },
            writeEscalation: (_, _, _) => escalations++);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                hasOpenClarification = false;
                return false;
            });

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, escalations);
        Assert.Equal(1, workspaceCreates);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_readmits_store_answered_clarification_without_restart_and_dispatches")]
    public async Task BatchLoopReadmitsStoreAnsweredClarificationWithoutRestartAndDispatches()
    {
        var root = CreateTempDirectory("mcg-clarification-readmit");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var (kernel, goal) = SimpleGoal("Clarified goal");
        var questionKey = $"{GoalRefinementService.CorrelationKeyPrefix}{goal.Id.Value}:scope:test";
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Do the clarified thing.",
            ["Dispatch starts after clarification."],
            VerificationClass.TestVerifiable,
            [],
            [new RefinedSpecOpenQuestion(questionKey, "Which scope?", "scope", "Open")]));
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Which scope?",
            "body",
            questionKey);
        var workspaceCreated = false;
        var dispatches = 0;
        var resolved = false;
        var task = goal.Tasks.Single();

        var driver = MakeDriver(
            getFacts: g => new GoalLifecycleFacts(
                WorkspaceExists: workspaceCreated,
                HasOpenClarification: GoalRefinementGate.HasOpenClarification(workspace, g)),
            createWorkspace: _ =>
            {
                workspaceCreated = true;
                return "C:\\goal";
            },
            dispatchAndStart: g =>
            {
                dispatches++;
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\goal", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                if (!resolved)
                {
                    store.TryResolveAsync(questionKey, "Use the narrow scope.").GetAwaiter().GetResult();
                    resolved = true;
                }

                return false;
            });

        Assert.Equal(3, summary.Ticks);
        Assert.True(workspaceCreated);
        Assert.Equal(1, dispatches);
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("re-admitted escalated goal after state changed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_readmits_escalated_goal_when_task_state_changes_mid_run")]
    public void BatchLoopReadmitsEscalatedGoalWhenTaskStateChangesMidRun()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Needs operator repair.");
        var repaired = false;
        var escalations = 0;
        var workspaceCreates = 0;

        var driver = MakeDriver(
            createWorkspace: _ =>
            {
                workspaceCreates++;
                return "C:\\goal";
            },
            writeEscalation: (_, _, _) => escalations++);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                if (!repaired)
                {
                    kernel.RetryTask(goal.Id, task.Id, "Operator repaired failed task.");
                    repaired = true;
                }

                return false;
            });

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, escalations);
        Assert.Equal(1, workspaceCreates);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("re-admitted escalated goal after state changed", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BatchLoopResolvedRebaseConflictReadmitsGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Resolve landing conflict");
        var rebaseChecks = 0;
        var conflictChecks = 0;
        var landAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            rebaseOntoMain: _ => ++rebaseChecks == 1
                ? new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.Conflict,
                    "goal/test",
                    "Conflict remains.",
                    ["docs/test-design-discipline.md"],
                    "workspace rebase")
                : new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                    "goal/test",
                    "Branch can fast-forward into main.",
                    [],
                    null),
            recheckPreLandingRebaseConflict: _ =>
            {
                conflictChecks++;
                return new LandingEscalationRecheckResult(
                    ConditionResolved: true,
                    Status: "MergeTreeClean",
                    Observation: "Read-only merge-tree check found no conflict with main.",
                    EvidenceFingerprint: "branch=resolved;main=current");
            },
            runAcceptance: _ => true,
            land: g =>
            {
                landAttempts++;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Done);
        Assert.Equal(2, rebaseChecks);
        Assert.Equal(1, conflictChecks);
        Assert.Equal(1, landAttempts);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Landing escalation self-cleared", StringComparison.Ordinal) &&
            evt.Message.Contains("status=MergeTreeClean", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BatchLoopPersistingRebaseConflictStaysSetAside()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Keep landing conflict escalated");
        var rebaseChecks = 0;
        var conflictChecks = 0;
        var landAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            rebaseOntoMain: _ =>
            {
                rebaseChecks++;
                return new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.Conflict,
                    "goal/test",
                    "Conflict remains.",
                    ["docs/test-design-discipline.md"],
                    "workspace rebase");
            },
            recheckPreLandingRebaseConflict: _ =>
            {
                conflictChecks++;
                return new LandingEscalationRecheckResult(
                    ConditionResolved: false,
                    Status: "MergeTreeConflict",
                    Observation: "Read-only merge-tree check still conflicts with main.",
                    EvidenceFingerprint: "branch=conflicted;main=current");
            },
            runAcceptance: _ => true,
            land: g =>
            {
                landAttempts++;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(1, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, rebaseChecks);
        Assert.Equal(1, conflictChecks);
        Assert.Equal(0, landAttempts);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Landing escalation self-cleared", StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, 2, 0, 1)]
    [Xunit.InlineData(true, 3, 1, 2)]
    public void BatchLoopResolvedRebaseEvidenceRetriesOnlyWhenGitCandidateChanges(
        bool gitCandidateChanges,
        int expectedRebaseChecks,
        int expectedLandAttempts,
        int expectedSelfClears)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Bound mismatched landing conflict probe");
        var rebaseChecks = 0;
        var conflictChecks = 0;
        var landAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            rebaseOntoMain: _ =>
            {
                rebaseChecks++;
                if (gitCandidateChanges && rebaseChecks == 3)
                {
                    return new GoalWorktreeRebaseResult(
                        GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                        "goal/test",
                        "Changed candidate no longer conflicts.",
                        [],
                        null);
                }

                return new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.Conflict,
                    "goal/test",
                    "Commit replay still conflicts.",
                    ["docs/test-design-discipline.md"],
                    "workspace rebase");
            },
            recheckPreLandingRebaseConflict: _ =>
            {
                conflictChecks++;
                var candidate = gitCandidateChanges && conflictChecks == 2
                    ? "branch=changed;main=changed"
                    : "branch=unchanged;main=unchanged";
                return new LandingEscalationRecheckResult(
                    ConditionResolved: true,
                    Status: "MergeTreeClean",
                    Observation: "Tip trees merge cleanly despite the replay conflict.",
                    EvidenceFingerprint: candidate);
            },
            runAcceptance: _ => true,
            land: g =>
            {
                landAttempts++;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(gitCandidateChanges ? 3 : 2, summary.Ticks);
        Assert.Equal(2, summary.Escalated);
        Assert.Equal(expectedRebaseChecks, rebaseChecks);
        Assert.Equal(2, conflictChecks);
        Assert.Equal(expectedLandAttempts, landAttempts);
        Assert.Equal(expectedSelfClears, goal.Timeline.Count(evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Landing escalation self-cleared", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void BatchLoopRebaseRecheckFailureKeepsGoalSetAside()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Keep failed recheck escalated");
        var rebaseChecks = 0;
        var conflictChecks = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            rebaseOntoMain: _ =>
            {
                rebaseChecks++;
                return new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.Conflict,
                    "goal/test",
                    "Conflict remains.",
                    ["docs/test-design-discipline.md"],
                    "workspace rebase");
            },
            recheckPreLandingRebaseConflict: _ =>
            {
                conflictChecks++;
                throw new InvalidOperationException("git merge-tree could not start");
            },
            runAcceptance: _ => true);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(1, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, rebaseChecks);
        Assert.Equal(1, conflictChecks);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Landing escalation recheck failed", StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Landing escalation self-cleared", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_applies_retry_intent_and_publishes_outcome_after_tick_persist")]
    public async Task BatchLoopAppliesRetryIntentAndPublishesOutcomeAfterTickPersist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-loop-operator-intent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (kernel, goal) = SimpleGoal("Apply operator retry");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Needs operator repair.");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                Guid.NewGuid().ToString("N"),
                "loop-retry-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("Operator repaired through inbox.", null),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var persistedGoalIds = new List<GoalId>();

            var summary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                persistGoalTick: (_, goalIds) => persistedGoalIds.AddRange(goalIds));

            var outcome = await store.GetAsync(intent.Id);
            Assert.Equal(1, summary.Ticks);
            Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, task.Id).Status);
            Assert.Contains(goal.Id, persistedGoalIds);
            Assert.Contains(goal.Timeline, item =>
                item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.Contains($"operator-intent:{intent.Id}", StringComparison.Ordinal));
            Assert.NotNull(outcome);
            Assert.Equal(OperatorIntentStatus.Applied, outcome!.Status);
            Assert.Contains("Applied retry", outcome.Outcome, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_retry_intent_invalidates_running_acceptance_before_redispatch")]
    public async Task BatchLoopRetryIntentInvalidatesRunningAcceptanceBeforeRedispatch()
    {
        var root = CreateTempDirectory("mcg-loop-verifying-retry");
        var launches = new ConcurrentDictionary<string, ConductorParallelAcceptanceOwnedProcessLaunch>();
        try
        {
            var acceptanceProcessAlive = true;
            var (kernel, goal) = SimpleGoal("Retry a goal while acceptance is running");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                new TaskVerificationRecord("focused test", root, 0, "passed", "", DateTimeOffset.UtcNow));
            kernel.BeginGoalAcceptanceVerification(goal.Id, "Acceptance attempt launched.");

            var attemptCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                isProcessAlive: _ => acceptanceProcessAlive,
                launchOwnedProcess: launch =>
                {
                    launches[launch.Attempt.AttemptId] = launch;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7100 + launches.Count);
                });
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/Retry.cs"],
                "branch-sha",
                "main-sha");
            var started = attemptCoordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);

            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "verifying-retry-intent",
                "verifying-retry-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("Acceptance evidence requires a correction.", null),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var dispatchStarts = 0;
            var ticks = new List<BatchTickSummary>();

            var summary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    dispatchAndStart: _ =>
                    {
                        dispatchStarts++;
                        return DispatchStartOutcome.Started();
                    },
                    parallelAcceptanceAttemptCoordinator: attemptCoordinator),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 3,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ => false,
                onTick: tick =>
                {
                    ticks.Add(tick);
                    if (ticks.Count == 2)
                    {
                        acceptanceProcessAlive = false;
                    }
                },
                persistGoalTick: (_, _) => { });

            var stale = ReadAttempt(started.Attempt.MetadataPath);
            Assert.Equal(1, summary.Advanced);
            Assert.Equal(2, summary.Held);
            Assert.Equal(1, dispatchStarts);
            Assert.Equal(GoalStatus.Active, goal.Status);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.StaleCandidate, stale.Outcome);
            Assert.NotNull(stale.ReconciledAt);
            Assert.Contains("invalidated the current acceptance attempt", stale.Detail, StringComparison.Ordinal);
            Assert.Equal(3, ticks.Count);
            Assert.Contains(ticks[0].ProgressLines!, line =>
                line.Contains("ACCEPTANCE_INVALIDATED", StringComparison.Ordinal) &&
                line.Contains("attempt_staled=true", StringComparison.Ordinal) &&
                line.Contains("goal_reopened=true", StringComparison.Ordinal));
            Assert.DoesNotContain(ticks[0].ProgressLines!, line =>
                line.Contains("result=executed", StringComparison.Ordinal));
            Assert.Contains(ticks[1].ProgressLines!, line =>
                line.Contains("result=held", StringComparison.Ordinal) &&
                line.Contains("reason=invalidated_acceptance_attempt", StringComparison.Ordinal));
            Assert.DoesNotContain(ticks[1].ProgressLines!, line =>
                line.Contains("result=executed", StringComparison.Ordinal));
            Assert.Contains(ticks[2].ProgressLines!, line =>
                line.Contains("result=executed", StringComparison.Ordinal));

            attemptCoordinator.RunAttemptForTests(
                started.Attempt,
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            var afterLateCompletion = ReadAttempt(started.Attempt.MetadataPath);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.StaleCandidate, afterLateCompletion.Outcome);
            Assert.NotNull(afterLateCompletion.ReconciledAt);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_does_not_publish_applied_intent_when_tick_persist_fails")]
    public async Task BatchLoopDoesNotPublishAppliedIntentWhenTickPersistFails()
    {
        var root = CreateTempDirectory("mcg-loop-operator-intent-persist-failure");
        try
        {
            var (kernel, goal) = SimpleGoal("Keep claimed intent after failed persist");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "persist-failure-intent",
                "persist-failure-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("retry after persistence recovers", null),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var sleepCalls = 0;

            _ = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers),
                 ConductorAutonomyPolicy.Conservative,
                 NoStopPath(),
                 maxIterations: 2,
                 watchInterval: TimeSpan.FromSeconds(1),
                 sleepFunc: _ =>
                 {
                     sleepCalls++;
                     return true;
                 },
                 persistTick: _ => throw SqliteBusy(),
                 keepAliveWhenIdle: true,
                 busyWriteDelay: _ => { });

            var outcome = await store.GetAsync(intent.Id);
            Assert.Equal(OperatorIntentStatus.Claimed, outcome!.Status);
            Assert.Null(outcome.CompletedAt);
            Assert.Equal(1, sleepCalls);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_rejects_operator_intent_outside_configured_goal_scope")]
    public async Task BatchLoopRejectsOperatorIntentOutsideConfiguredGoalScope()
    {
        var root = CreateTempDirectory("mcg-loop-operator-intent-scope");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var agents = AgentCatalog.Default().Agents;
            var scopedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                agents,
                "Scoped conductor goal");
            var outsideGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                agents,
                "Outside conductor goal");
            var outsideTask = outsideGoal.Tasks.Single();
            kernel.ReportTaskProgress(outsideGoal.Id, outsideTask.Id, WorkTaskStatus.Failed, "failed");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "outside-scope-intent",
                "outside-scope-key",
                OperatorIntentVerbs.Retry,
                outsideGoal.Id.Value,
                outsideTask.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("retry outside scope", null),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);

            _ = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onlyGoalId: scopedGoal.Id.Value);

            var outcome = await store.GetAsync(intent.Id);
            Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
            Assert.Contains("outside conductor scope", outcome.Outcome, StringComparison.Ordinal);
            Assert.Equal(WorkTaskStatus.Failed, outsideTask.Status);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_surfaces_rejected_intent_when_no_goal_is_eligible")]
    public async Task BatchLoopSurfacesRejectedIntentWhenNoGoalIsEligible()
    {
        var root = CreateTempDirectory("mcg-loop-operator-intent-rejected");
        try
        {
            var (kernel, goal) = SimpleGoal("Surface rejected parked-goal intent");
            kernel.ParkGoal(goal.Id, "operator parked");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "rejected-loop-intent",
                "rejected-loop-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                TaskId.New().Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("invalid task", null),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var ticks = new List<BatchTickSummary>();

            _ = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: ticks.Add,
                persistGoalTick: (_, _) => { });

            var outcome = await store.GetAsync(intent.Id);
            Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
            Assert.Contains(ticks.SelectMany(tick => tick.ProgressLines ?? []), line =>
                line.Contains("result=rejected", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_stop_after_operator_intent_detaches_and_checkpoints")]
    public async Task BatchLoopStopAfterOperatorIntentDetachesAndCheckpoints()
    {
        var root = CreateTempDirectory("mcg-loop-stop-after-operator-intent");
        try
        {
            var (kernel, goal) = SimpleGoal("Stop after rejected operator intent");
            kernel.ParkGoal(goal.Id, "parked for operator");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "stop-after-intent",
                "stop-after-intent-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                TaskId.New().Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("invalid task", null),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var stopFile = Path.Combine(root, ConductorBatchLoop.StopFileName);
            var detachCalls = 0;
            var checkpointCalls = 0;

            var summary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store),
                detachGoalRunningDispatches: (_, detachedGoal) =>
                {
                    Assert.Equal(goal.Id, detachedGoal.Id);
                    detachCalls++;
                }).Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    maxIterations: 2,
                    onTick: _ => File.WriteAllText(stopFile, "stop"),
                    persistTick: _ => checkpointCalls++);

            var outcome = await store.GetAsync(intent.Id);
            Assert.True(summary.StopRequested);
            Assert.Equal(1, summary.Ticks);
            Assert.Equal(1, detachCalls);
            Assert.Equal(1, checkpointCalls);
            Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_fault_isolates_unavailable_operator_intent_store")]
    public void BatchLoopFaultIsolatesUnavailableOperatorIntentStore()
    {
        var root = CreateTempDirectory("mcg-loop-operator-intent-unavailable");
        try
        {
            var databasePath = Path.Combine(root, "operator-intents.db");
            File.WriteAllText(databasePath, "not a sqlite database");
            var (kernel, _) = SimpleGoal("Advance despite unavailable intent inbox");
            var workspaceCreates = 0;
            var summary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(
                    new SqliteOperatorIntentStore(
                        databasePath,
                        Path.Combine(root, "logs"),
                        readOnly: true))).Run(
                kernel,
                MakeDriver(createWorkspace: _ =>
                {
                    workspaceCreates++;
                    return root;
                }),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(1, summary.Advanced);
            Assert.Equal(1, workspaceCreates);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_running_recovery_sequence_applies_retry_progress_and_manual_verification")]
    public async Task BatchLoopRunningRecoverySequenceAppliesRetryProgressAndManualVerification()
    {
        var root = CreateTempDirectory("mcg-loop-operator-intent-recovery-sequence");
        try
        {
            var (kernel, goal) = SimpleGoal("Apply running-loop recovery sequence");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var coordinator = new OperatorIntentCoordinator(store);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

            async Task SubmitAndTick(string id, string verb, object payload)
            {
                await store.EnqueueAsync(new OperatorIntentRecord(
                    id,
                    $"{id}-key",
                    verb,
                    goal.Id.Value,
                    task.Id.Value,
                    JsonSerializer.Serialize(
                        payload,
                        payload.GetType(),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    [],
                    "operator",
                    "cli",
                    "local-process",
                    DateTimeOffset.UtcNow));
                _ = new ConductorBatchLoop(operatorIntents: coordinator).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1,
                    persistGoalTick: (_, _) => { });
                Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(id))!.Status);
            }

            await SubmitAndTick(
                "sequence-retry",
                OperatorIntentVerbs.Retry,
                new RetryOperatorIntentPayload("mechanical recovery", RetryRoundKind.Mechanical));
            await SubmitAndTick(
                "sequence-progress",
                OperatorIntentVerbs.Progress,
                new ProgressOperatorIntentPayload(WorkTaskStatus.Completed, "existing work still stands"));
            var verification = new TaskVerificationRecord(
                "manual-verification passed",
                root,
                0,
                "operator checked existing result",
                string.Empty,
                DateTimeOffset.UtcNow);
            await SubmitAndTick(
                "sequence-verify",
                OperatorIntentVerbs.VerifyManual,
                new ManualVerificationOperatorIntentPayload(verification));

            Assert.Equal(WorkTaskStatus.Completed, kernel.GetTask(goal.Id, task.Id).Status);
            Assert.Equal(verification, kernel.GetTask(goal.Id, task.Id).LastVerification);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "OperatorIntentCoordinator_recovers_claimed_intent_without_duplicate_state_mutation")]
    public async Task OperatorIntentCoordinatorRecoversClaimedIntentWithoutDuplicateStateMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-loop-operator-intent-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (kernel, goal) = SimpleGoal("Recover claimed operator retry");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Needs operator repair.");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                Guid.NewGuid().ToString("N"),
                "loop-recovery-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("Apply once.", null),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);

            var first = new OperatorIntentCoordinator(store).ExecutePending(kernel, goal);
            Assert.True(first.MutatedGoalState);
            var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
            var restoredGoal = restored.GetGoal(goal.Id);

            var replay = new OperatorIntentCoordinator(store).ExecutePending(restored, restoredGoal);
            var outcome = await store.GetAsync(intent.Id);

            Assert.False(replay.MutatedGoalState);
            Assert.Single(restoredGoal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
            Assert.Equal(OperatorIntentStatus.Applied, outcome!.Status);
            Assert.Contains("recovered durable goal marker", outcome.Outcome, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_keeps_unchanged_escalated_goal_set_aside_without_reescalating")]
    public void BatchLoopKeepsUnchangedEscalatedGoalSetAsideWithoutReescalating()
    {
        var kernel = new AgentOrchestratorKernel();
        var failedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "failed goal");
        var heldGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "held goal");
        var failedTask = failedGoal.Tasks.Single();
        kernel.ReportTaskProgress(failedGoal.Id, failedTask.Id, WorkTaskStatus.Failed, "Needs operator repair.");
        var escalations = 0;
        var heldAttempts = 0;

        var driver = MakeDriver(
            getFacts: goal => goal.Id == heldGoal.Id
                ? new GoalLifecycleFacts(WorkspaceExists: true)
                : GoalLifecycleFacts.None,
            dispatchAndStart: goal =>
            {
                if (goal.Id == heldGoal.Id)
                {
                    heldAttempts++;
                    return DispatchStartOutcome.EmptyBatch("Held for operator approval.");
                }

                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, _) => escalations++);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(3, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, escalations);
        Assert.Equal(3, heldAttempts);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_sets_aside_ownership_hold_escalation_without_relanding")]
    public void BatchLoopSetsAsideOwnershipHoldEscalationWithoutRelanding()
    {
        var root = CreateTempDirectory("mcg-ownership-hold-set-aside");
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var (kernel, goal) = SimpleGoal("ownership held goal");
            var heldGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "ordinary held goal");
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var landAttempts = 0;
            var heldAttempts = 0;

            var driver = MakeDriver(
                getFacts: g => g.Id == heldGoal.Id
                    ? new GoalLifecycleFacts(WorkspaceExists: true)
                    : GoalLifecycleFacts.None,
                getRunningCount: () =>
                {
                    heldAttempts++;
                    return ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers;
                },
                runAcceptance: _ => true,
                land: g =>
                {
                    landAttempts++;
                    OperatorInbox.RecordOwnershipHolds(
                        workspace,
                        g,
                        [
                            new OwnershipHoldRequest(
                                task.Id,
                                1,
                                task.RequiredRole,
                                ["src/Mcg.AgentOrchestrator.Infrastructure/Protected.cs"],
                                "test ownership hold")
                        ]);
                    return new LandingResult(
                        g.Id.Value,
                        g.Id.Value[..8],
                        new LandingDecision.Escalate("ownership-denylist hold: task touched protected path"),
                        "integration",
                        false,
                        "Held");
                });

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 3,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ => false);

            Assert.Equal(3, summary.Ticks);
            Assert.Equal(1, summary.Escalated);
            Assert.Equal(1, landAttempts);
            Assert.Equal(3, heldAttempts);
            var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
            Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.OwnershipHold));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_keeps_unresolved_clarification_set_aside_without_reescalating")]
    public void BatchLoopKeepsUnresolvedClarificationSetAsideWithoutReescalating()
    {
        var kernel = new AgentOrchestratorKernel();
        var blockedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "blocked goal");
        var activeGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active goal");
        var workspaces = new HashSet<string>(StringComparer.Ordinal);
        var blockedClarificationPolls = 0;
        var escalations = 0;
        var dispatches = 0;

        var driver = MakeDriver(
            getFacts: goal =>
            {
                if (goal.Id == blockedGoal.Id)
                {
                    blockedClarificationPolls++;
                    return new GoalLifecycleFacts(HasOpenClarification: true);
                }

                return new GoalLifecycleFacts(WorkspaceExists: workspaces.Contains(goal.Id.Value));
            },
            createWorkspace: goal =>
            {
                workspaces.Add(goal.Id.Value);
                return "C:\\active";
            },
            dispatchAndStart: goal =>
            {
                if (goal.Id == activeGoal.Id)
                {
                    var task = goal.Tasks.Single();
                    kernel.RecordTaskDispatch(goal.Id, task.Id,
                        new TaskDispatchRecord("test-worker", "test.exe", "C:\\active", DateTimeOffset.UtcNow));
                    kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                        new TaskProcessRecord(123, "test.exe", "C:\\active", "out.log", "err.log", "exit.txt",
                            DateTimeOffset.UtcNow, null, null));
                    dispatches++;
                }

                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, _) => escalations++);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(3, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, escalations);
        Assert.Equal(1, dispatches);
        Assert.True(blockedClarificationPolls >= 3);
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

    [Xunit.Fact(DisplayName = "BatchLoop_escalated_goal_reaps_only_its_owned_running_dispatches")]
    public void BatchLoopEscalatedGoalReapsOnlyItsOwnedRunningDispatches()
    {
        var kernel = new AgentOrchestratorKernel();
        var escalatedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "escalated goal");
        var otherGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "other goal");
        var escalatedTask = escalatedGoal.Tasks.Single();
        var otherTask = otherGoal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(escalatedGoal.Id, escalatedTask.Id,
            new TaskDispatchRecord("test-worker", "test.exe", "C:\\escalated", now));
        kernel.RecordTaskProcessStarted(escalatedGoal.Id, escalatedTask.Id,
            new TaskProcessRecord(111, "test.exe", "C:\\escalated", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [111, 222]));
        kernel.RecordTaskDispatch(otherGoal.Id, otherTask.Id,
            new TaskDispatchRecord("test-worker", "other.exe", "C:\\other", now));
        kernel.RecordTaskProcessStarted(otherGoal.Id, otherTask.Id,
            new TaskProcessRecord(333, "other.exe", "C:\\other", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [333]));

        var killed = new List<int>();
        var sleeps = 0;
        var runner = new BackgroundDispatchRunner(
            isStillRunning: _ => false,
            tryKillOwnedProcess: pid =>
            {
                killed.Add(pid);
                return true;
            });
        var driver = MakeDriver(getFacts: _ => throw new InvalidOperationException("policy gate"));

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (loopKernel, goal) => runner.CancelRunningProcessesForGoal(loopKernel, goal.Id)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                onlyGoalId: escalatedGoal.Id.Value,
                sleepFunc: _ =>
                {
                    sleeps++;
                    return false;
                });

        Assert.Equal(1, summary.Escalated);
        Assert.Equal("blocked-recheck-exhausted", summary.StopReason);
        Assert.Equal(1, sleeps);
        Xunit.Assert.Equal([111, 222], killed);
        Assert.True(kernel.GetTask(escalatedGoal.Id, escalatedTask.Id).LastProcess!.WasCancelled);
        Assert.False(kernel.GetTask(otherGoal.Id, otherTask.Id).LastProcess!.WasCancelled);
    }

    // ── Kill-switch: stop file present → loop exits before first tick ─────

    [Xunit.Fact(DisplayName = "BatchLoop_KillSwitch_stopFilePresent_exitsBeforeAnyAdvance")]
    public void BatchLoop_KillSwitch_StopFilePresentExitsBeforeAnyAdvance()
    {
        var (kernel, _) = SimpleGoal();
        var advanceCalled = false;
        var driver = MakeDriver(createWorkspace: _ => { advanceCalled = true; return "/tmp/ws"; });

        var stopFile = ExistingStopPath();
        try
        {
            var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile);

            Assert.Equal(0, summary.Ticks);
            Assert.True(summary.StopRequested);
            Assert.False(advanceCalled); // AdvanceOnce must not run when stop signal is present
        }
        finally
        {
            File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_stop_detaches_watched_goal_running_dispatch")]
    public void BatchLoopStopDetachesWatchedGoalRunningDispatch()
    {
        var kernel = new AgentOrchestratorKernel();
        var watchedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "watched goal");
        var otherGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "other goal");
        var watchedTask = watchedGoal.Tasks.Single();
        var otherTask = otherGoal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(watchedGoal.Id, watchedTask.Id,
            new TaskDispatchRecord("test-worker", "watch.exe", "C:\\watch", now));
        kernel.RecordTaskProcessStarted(watchedGoal.Id, watchedTask.Id,
            new TaskProcessRecord(444, "watch.exe", "C:\\watch", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [444]));
        kernel.RecordTaskDispatch(otherGoal.Id, otherTask.Id,
            new TaskDispatchRecord("test-worker", "other.exe", "C:\\other", now));
        kernel.RecordTaskProcessStarted(otherGoal.Id, otherTask.Id,
            new TaskProcessRecord(555, "other.exe", "C:\\other", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [555]));

        var killed = new List<int>();
        var runner = new BackgroundDispatchRunner(tryKillOwnedProcess: pid =>
        {
            killed.Add(pid);
            return true;
        });
        var stopFile = ExistingStopPath();

        try
        {
            var summary = new ConductorBatchLoop(
                detachGoalRunningDispatches: (loopKernel, goal) => runner.DetachRunningProcessesForGoal(loopKernel, goal.Id)).Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    onlyGoalId: watchedGoal.Id.Value);

            Assert.True(summary.StopRequested);
            Xunit.Assert.Empty(killed);
            Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(watchedGoal.Id, watchedTask.Id).Status);
            Assert.False(kernel.GetTask(watchedGoal.Id, watchedTask.Id).LastProcess!.WasCancelled);
            Assert.False(kernel.GetTask(otherGoal.Id, otherTask.Id).LastProcess!.WasCancelled);
        }
        finally
        {
            File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_stop_detached_orphan_running_task_is_requeued_and_dispatched")]
    public void BatchLoopStopDetachedOrphanRunningTaskIsRequeuedAndDispatched()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "interrupted goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "first.exe", "C:\\goal", now));
        var firstStdout = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.out.log");
        var firstStderr = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.err.log");
        var firstExit = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.exit.txt");
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(444, "first.exe", "C:\\goal", firstStdout, firstStderr, firstExit,
                now, null, null, OwnedProcessIds: [444]));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        var stopFile = ExistingStopPath();
        try
        {
            new ConductorBatchLoop(
                detachGoalRunningDispatches: (loopKernel, loopGoal) => runner.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id)).Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    onlyGoalId: goal.Id.Value);
        }
        finally
        {
            File.Delete(stopFile);
        }

        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasCancelled);

        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var ready = g.Tasks.Single(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, ready.Id,
                    new TaskDispatchRecord("test-worker", "second.exe", "C:\\goal", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, ready.Id,
                    new TaskProcessRecord(777, "second.exe", "C:\\goal", "out2.log", "err2.log", "exit2.txt",
                        DateTimeOffset.UtcNow, null, null, OwnedProcessIds: [777]));
                dispatches++;
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop(
            recoverInterruptedDispatches: loopKernel => runner.RequeueInterruptedDispatches(loopKernel)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, dispatches);
        var recoveredTask = kernel.GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Running, recoveredTask.Status);
        Assert.Equal(777, recoveredTask.LastProcess!.ProcessId);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_max_iterations_detaches_live_dispatch_without_reaping")]
    public void BatchLoopMaxIterationsDetachesLiveDispatchWithoutReaping()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "bounded goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "worker.exe", "C:\\goal", now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(444, "worker.exe", "C:\\goal", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [444]));

        var killed = new List<int>();
        var detachedGoals = new List<string>();
        var reapedGoals = new List<string>();
        var runner = new BackgroundDispatchRunner(tryKillOwnedProcess: pid =>
        {
            killed.Add(pid);
            return true;
        });

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (loopKernel, loopGoal) =>
            {
                reapedGoals.Add(loopGoal.Id.Value);
                runner.CancelRunningProcessesForGoal(loopKernel, loopGoal.Id);
            },
            detachGoalRunningDispatches: (loopKernel, loopGoal) =>
            {
                detachedGoals.Add(loopGoal.Id.Value);
                runner.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id);
            }).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 0,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(0, summary.Ticks);
        Assert.Empty(reapedGoals);
        Assert.Empty(killed);
        Xunit.Assert.Equal([goal.Id.Value], detachedGoals);
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasCancelled);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_max_duration_detaches_live_dispatch_without_reaping")]
    public void BatchLoopMaxDurationDetachesLiveDispatchWithoutReaping()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "duration bounded goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "worker.exe", "C:\\goal", now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(555, "worker.exe", "C:\\goal", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [555]));

        var killed = new List<int>();
        var detachedGoals = new List<string>();
        var reapedGoals = new List<string>();
        var runner = new BackgroundDispatchRunner(tryKillOwnedProcess: pid =>
        {
            killed.Add(pid);
            return true;
        });

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (loopKernel, loopGoal) =>
            {
                reapedGoals.Add(loopGoal.Id.Value);
                runner.CancelRunningProcessesForGoal(loopKernel, loopGoal.Id);
            },
            detachGoalRunningDispatches: (loopKernel, loopGoal) =>
            {
                detachedGoals.Add(loopGoal.Id.Value);
                runner.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id);
            }).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxDuration: TimeSpan.Zero,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(0, summary.Ticks);
        Assert.Empty(reapedGoals);
        Assert.Empty(killed);
        Xunit.Assert.Equal([goal.Id.Value], detachedGoals);
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasCancelled);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_bounded_exit_detached_orphan_running_task_is_requeued_and_dispatched")]
    public void BatchLoopBoundedExitDetachedOrphanRunningTaskIsRequeuedAndDispatched()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "bounded interrupted goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "first.exe", "C:\\goal", now));
        var firstStdout = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.out.log");
        var firstStderr = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.err.log");
        var firstExit = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.exit.txt");
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(444, "first.exe", "C:\\goal", firstStdout, firstStderr, firstExit,
                now, null, null, OwnedProcessIds: [444]));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        new ConductorBatchLoop(
            detachGoalRunningDispatches: (loopKernel, loopGoal) => runner.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id)).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 0,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasCancelled);

        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var ready = g.Tasks.Single(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, ready.Id,
                    new TaskDispatchRecord("test-worker", "second.exe", "C:\\goal", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, ready.Id,
                    new TaskProcessRecord(777, "second.exe", "C:\\goal", "out2.log", "err2.log", "exit2.txt",
                        DateTimeOffset.UtcNow, null, null, OwnedProcessIds: [777]));
                dispatches++;
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop(
            recoverInterruptedDispatches: loopKernel => runner.RequeueInterruptedDispatches(loopKernel)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, dispatches);
        var recoveredTask = kernel.GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Running, recoveredTask.Status);
        Assert.Equal(777, recoveredTask.LastProcess!.ProcessId);
    }

    [Xunit.Fact]
    public void BatchLoop_ConductorCancelledTask_AutoRequeues()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("five stage goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var planner = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Planner);
        var researcher = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Researcher);
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var now = DateTimeOffset.UtcNow;

        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Planner done.");
        kernel.ReportTaskProgress(goal.Id, researcher.Id, WorkTaskStatus.Completed, "Researcher done.");
        kernel.RecordTaskDispatch(goal.Id, developer.Id,
            new TaskDispatchRecord("test-worker", "dev.exe", "C:\\goal", now));
        kernel.RecordTaskProcessStarted(goal.Id, developer.Id,
            new TaskProcessRecord(444, "dev.exe", "C:\\goal", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [444]));
        new BackgroundDispatchRunner(isStillRunning: _ => false, tryKillOwnedProcess: _ => true)
            .CancelRunningProcessesForGoal(kernel, goal.Id);

        Assert.Equal(WorkTaskStatus.Cancelled, kernel.GetTask(goal.Id, developer.Id).Status);
        Assert.True(kernel.GetTask(goal.Id, developer.Id).WasCancelledByConductor);

        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: recoveredGoal =>
            {
                var recoveredTask = recoveredGoal.Tasks.Single(t => t.Id == developer.Id);
                kernel.RecordTaskDispatch(recoveredGoal.Id, recoveredTask.Id,
                    new TaskDispatchRecord("test-worker", "second.exe", "C:\\goal", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(recoveredGoal.Id, recoveredTask.Id,
                    new TaskProcessRecord(777, "second.exe", "C:\\goal", "out2.log", "err2.log", "exit2.txt",
                        DateTimeOffset.UtcNow, null, null, OwnedProcessIds: [777]));
                dispatches++;
                return DispatchStartOutcome.Started();
            });
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);

        var summary = new ConductorBatchLoop(
            recoverInterruptedDispatches: loopKernel => runner.RequeueInterruptedDispatches(loopKernel)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, dispatches);
        var recoveredDeveloper = kernel.GetTask(goal.Id, developer.Id);
        Assert.Equal(WorkTaskStatus.Running, recoveredDeveloper.Status);
        Assert.Equal(777, recoveredDeveloper.LastProcess!.ProcessId);
        Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried && evt.TaskId == developer.Id);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRequeueSkipped);
    }

    // ── Watch mode: continues when all held instead of breaking ──────────

    [Xunit.Fact(DisplayName = "WatchMode_continuesAfterHeld_thenExitsWhenStopped")]
    public void WatchMode_ContinuesAfterHeld_ThenExitsWhenStopped()
    {
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "goal 0");

        // dispatchAndStart records a real dispatch so the goal transitions to Dispatched state,
        // which is always Held on the next tick — no capacity tricks needed.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();
        var tickCount = 0;
        var watchInterval = TimeSpan.FromMilliseconds(50); // very short for tests

        // After the 2nd tick (goal held, watch sleeping), create the stop file.
        void OnTick(BatchTickSummary tick)
        {
            tickCount++;
            if (tickCount >= 2)
                File.WriteAllText(stopFile, "stop");
        }

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
                watchInterval: watchInterval, onTick: OnTick);

            // Tick 1: WorkspaceReady → dispatch (advanced=1).
            // Tick 2: Worker slots full → held → watch sleeps → stop detected.
            Assert.True(summary.Ticks >= 2);
            Assert.True(summary.Advanced >= 1);
            Assert.True(summary.StopRequested);
            Assert.True(tickCount >= 2);
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_withoutWatchFlag_breaksWhenAllHeld")]
    public void WatchMode_WithoutWatchFlag_BreaksWhenAllHeld()
    {
        var (kernel, _) = SimpleGoal();
        // getRunningCount is at cap from the start so the goal is held once WorkspaceReady.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var stopFile = NoStopPath();
        // No watchInterval → non-watch behavior: exit when all goals are held, not sleep-and-continue.
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 10);

        // The loop must exit well before maxIterations once all goals are held.
        Assert.False(summary.StopRequested);   // exited cleanly, not via kill-switch
        Assert.True(summary.Ticks < 10);       // not looping indefinitely (watch-mode would)
        Assert.True(summary.Held >= 1);        // at least one held tick caused the exit
    }

    // ── onTick callback: invoked after each tick ──────────────────────────

    [Xunit.Fact(DisplayName = "OnTick_CalledAfterEachTick_WithCorrectSummary")]
    public void OnTick_CalledAfterEachTick_WithCorrectSummary()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());

        var tickSummaries = new List<BatchTickSummary>();
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,   // Verified state
            runAcceptance: _ => true,
            land: g => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok"),
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null));

        var stopFile = NoStopPath();
        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
            maxIterations: 3, onTick: tickSummaries.Add);

        Assert.True(tickSummaries.Count > 0);
        foreach (var t in tickSummaries)
            Assert.True(t.Tick >= 1);
    }

    [Xunit.Fact(DisplayName = "OnTick_WatchSleeping_TrueWhenAllHeldInWatchMode")]
    public void OnTick_WatchSleeping_TrueWhenAllHeldInWatchMode()
    {
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "held goal");

        // dispatchAndStart records a real dispatch so the goal transitions to Dispatched state,
        // which is always Held on the next tick — no capacity tricks needed.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();
        BatchTickSummary? sleepingTick = null;
        var watchInterval = TimeSpan.FromMilliseconds(30);

        void OnTick(BatchTickSummary tick)
        {
            if (tick.WatchSleeping)
            {
                sleepingTick = tick;
                File.WriteAllText(stopFile, "stop"); // stop after first sleep tick
            }
        }

        try
        {
            new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
                watchInterval: watchInterval, onTick: OnTick);

            Assert.True(sleepingTick is not null);
            Assert.True(sleepingTick!.WatchSleeping);
            Assert.Equal(0, sleepingTick.Advanced); // all held tick
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    // ── Injectable sleep: sleepFunc is called during watch sleep ─────────

    [Xunit.Fact(DisplayName = "WatchMode_InjectableSleep_SleepFuncCalledAndContinues")]
    public void WatchMode_InjectableSleep_SleepFuncCalledAndContinues()
    {
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "held goal");

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                // Reflect real start behavior: a started worker moves the goal to Running (which the
                // loop then HOLDS on), not Dispatched (which the hardened conductor now re-starts).
                kernel.RecordTaskProcessStarted(g.Id, task.Id,
                    new TaskProcessRecord(1234, "test.exe", "C:\\tmp", "out.log", "err.log", "exit.txt", DateTimeOffset.UtcNow, null, null));
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();
        var sleepCallCount = 0;
        var allIntervalsCorrect = true;

        // Inject a sleep func that records calls and returns false (no stop).
        // Stop via stop file after 2nd sleep call.
        Func<TimeSpan, bool> fakeSleep = interval =>
        {
            sleepCallCount++;
            if (interval != TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds))
                allIntervalsCorrect = false;
            if (sleepCallCount >= 2)
                File.WriteAllText(stopFile, "stop");
            return File.Exists(stopFile);
        };

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
                watchInterval: TimeSpan.FromSeconds(15),
                sleepFunc: fakeSleep);

            // Sleep func should have been called at least once (goal dispatched, next tick held)
            Assert.True(sleepCallCount >= 1);
            // Each sleep call should have received the watch interval
            Assert.True(allIntervalsCorrect);
            // Loop should have stopped via the stop file
            Assert.True(summary.StopRequested);
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_exit_file_wake_signal_sweeps_before_fallback_timeout")]
    public void WatchModeExitFileWakeSignalSweepsBeforeFallbackTimeout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-wake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var exit = Path.Combine(root, "worker.exit.txt");
        var stdout = Path.Combine(root, "worker.out.log");
        var stderr = Path.Combine(root, "worker.err.log");
        File.WriteAllText(stdout, "done");
        File.WriteAllText(stderr, "");

        var (kernel, goal) = SimpleGoal("running goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", root, now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(1234, "test.exe", root, stdout, stderr, exit, now, null, null, OwnedProcessIds: [1234]));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        var sweepCalls = 0;
        var wakeSignal = new TestWakeSignal(() => File.WriteAllText(exit, "0"));
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));

        var summary = new ConductorBatchLoop(loopKernel =>
        {
            sweepCalls++;
            runner.SweepExitedProcesses(loopKernel);
        }).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
            wakeSignal: wakeSignal);

        Assert.Equal(TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds), wakeSignal.Timeouts.Single());
        Assert.Contains(wakeSignal.TrackedExitCodePathUpdates.Single(),
            path => string.Equals(path, exit, StringComparison.OrdinalIgnoreCase));
        Assert.True(sweepCalls >= 2);
        Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.IsRunning);
        Assert.False(summary.StopRequested);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_refreshes_exited_readonly_dispatch_before_advance_and_does_not_redispatch_it")]
    public void BatchLoopRefreshesExitedReadonlyDispatchBeforeAdvanceAndDoesNotRedispatchIt()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
        var researcher = new TaskSpec(TaskId.New(), "Research constraints.", AgentRole.Researcher);
        var goal = kernel.CreateGoal("Two role handoff", [planner, researcher]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var root = Path.Combine(Path.GetTempPath(), $"mcg-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var stdout = Path.Combine(root, "planner.out.log");
        var stderr = Path.Combine(root, "planner.err.log");
        var exit = Path.Combine(root, "planner.exit.txt");
        var plannerPlan = WorkerDispatchTestSupport.PlannerContractPlanFixture().Replace(
            "`seed.txt`, ",
            string.Empty,
            StringComparison.Ordinal);
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            plannerPlan,
            "WORKER_RESULT:",
            "files: none",
            "commands: none",
            "tests: not-run - planning only",
            "commit: none",
            "blockers: none",
            "model_fit: OpenAI/gpt-5.5 - adequate - planning",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT"));
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", root, now));
        var running = new TaskProcessRecord(4242, "planner.exe", root, stdout, stderr, exit, now, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, planner.Id, running);

        var dispatchCalls = new List<TaskId>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var next = g.Tasks.Single(task => task.Status == WorkTaskStatus.Assigned);
                dispatchCalls.Add(next.Id);
                kernel.RecordTaskDispatch(g.Id, next.Id, new TaskDispatchRecord("next-worker", "next.exe", root, DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop(
            refreshGoalDispatchesBeforeAdvance: (loopKernel, loopGoal) => { GoalManagementCommandService.RefreshDispatches(loopKernel, loopGoal); })
            .Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);

        Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        Assert.Equal(WorkTaskStatus.Running, researcher.Status);
        Assert.Equal([researcher.Id], dispatchCalls);
        Assert.Equal(1, summary.Advanced);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_reconciles_verified_dead_dispatch_before_gate_admission")]
    public void BatchLoopReconcilesVerifiedDeadDispatchBeforeGateAdmission()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-dead-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var exit = Path.Combine(root, "developer.exit.txt");
        var stdout = Path.Combine(root, "developer.out.log");
        var stderr = Path.Combine(root, "developer.err.log");
        File.WriteAllText(stdout, "prior worker result");
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "1");

        var (kernel, goal) = SimpleGoal("completed tasks held by stale dead watch");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now));
        var processRecord = new TaskProcessRecord(111, "codex exec prompt", root, stdout, stderr, exit, now, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", root, 0, "already completed", string.Empty, now));
        File.WriteAllText(
            BackgroundDispatchRunner.GetHeartbeatPath(processRecord),
            """
{"pid":32640,"childPid":32641,"ownedPids":[],"state":"exiting","lastObservedAt":"2026-07-19T12:00:00Z","lastProgressAt":"2026-07-19T11:59:00Z","stdoutBytes":42,"stderrBytes":0}
""");

        var acceptedGoals = new List<GoalId>();
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (candidate, _) =>
            {
                acceptedGoals.Add(candidate.Id);
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            });

        try
        {
            var summary = new ConductorBatchLoop(loopKernel => runner.SweepExitedProcesses(loopKernel)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal([goal.Id], acceptedGoals);
            Assert.Equal(1, summary.Advanced);
            Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.IsRunning);
            Assert.Equal(1, kernel.GetTask(goal.Id, task.Id).LastProcess!.ExitCode);
            Assert.Single(kernel.GetTask(goal.Id, task.Id).VerificationHistory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_exit_wake_reconciles_before_stop_file_exit")]
    public void WatchModeExitWakeReconcilesBeforeStopFileExit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-wake-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var exit = Path.Combine(root, "worker.exit.txt");
        var stdout = Path.Combine(root, "worker.out.log");
        var stderr = Path.Combine(root, "worker.err.log");
        var stopFile = NoStopPath();
        File.WriteAllText(stdout, "done");
        File.WriteAllText(stderr, "");

        var (kernel, goal) = SimpleGoal("running goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", root, now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(1234, "test.exe", root, stdout, stderr, exit, now, null, null, OwnedProcessIds: [1234]));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        var sweepCalls = 0;
        var wakeSignal = new TestWakeSignal(() =>
        {
            File.WriteAllText(exit, "0");
            File.WriteAllText(stopFile, "stop");
        });

        try
        {
            var summary = new ConductorBatchLoop(loopKernel =>
            {
                sweepCalls++;
                runner.SweepExitedProcesses(loopKernel);
            }).Run(
                kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative,
                stopFile,
                watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                wakeSignal: wakeSignal);

            Assert.True(summary.StopRequested);
            Assert.True(sweepCalls >= 2);
            Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.IsRunning);
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_no_exit_wake_waits_running_dispatch_fallback_interval")]
    public void WatchModeNoExitWakeWaitsRunningDispatchFallbackInterval()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-wake-no-event-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var exit = Path.Combine(root, "worker.exit.txt");
        var stdout = Path.Combine(root, "worker.out.log");
        var stderr = Path.Combine(root, "worker.err.log");
        File.WriteAllText(stdout, "still running");
        File.WriteAllText(stderr, "");

        var (kernel, goal) = SimpleGoal("running goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", root, now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(1234, "test.exe", root, stdout, stderr, exit, now, null, null, OwnedProcessIds: [1234]));

        var wakeSignal = new TestWakeSignal(_ => false);
        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                wakeSignal: wakeSignal);

            Assert.Equal(1, summary.Ticks);
            Assert.Equal([TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds)], wakeSignal.Timeouts);
            Assert.Contains(wakeSignal.TrackedExitCodePathUpdates.Single(),
                path => string.Equals(path, exit, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_multiple_exit_wakes_coalesce_into_one_immediate_tick")]
    public void WatchModeMultipleExitWakesCoalesceIntoOneImmediateTick()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-wake-coalesce-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var exitPaths = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), $"running goal {i}");
            var task = goal.Tasks.Single();
            var stdout = Path.Combine(root, $"worker-{i}.out.log");
            var stderr = Path.Combine(root, $"worker-{i}.err.log");
            var exit = Path.Combine(root, $"worker-{i}.exit.txt");
            File.WriteAllText(stdout, "done");
            File.WriteAllText(stderr, "");
            var now = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", root, now));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                new TaskProcessRecord(1234 + i, "test.exe", root, stdout, stderr, exit, now, null, null, OwnedProcessIds: [1234 + i]));
            exitPaths.Add(exit);
        }

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        var wakeSignal = new TestWakeSignal(() =>
        {
            foreach (var exitPath in exitPaths)
            {
                File.WriteAllText(exitPath, "0");
            }
        });

        try
        {
            var summary = new ConductorBatchLoop(loopKernel => runner.SweepExitedProcesses(loopKernel)).Run(
                kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                wakeSignal: wakeSignal);

            Assert.Equal(1, wakeSignal.SignaledWaits);
            Assert.Single(wakeSignal.Timeouts);
            Assert.Equal(3, wakeSignal.TrackedExitCodePathUpdates.Single().Count);
            Assert.All(exitPaths, exitPath => Assert.Contains(wakeSignal.TrackedExitCodePathUpdates.Single(),
                trackedPath => string.Equals(trackedPath, exitPath, StringComparison.OrdinalIgnoreCase)));
            Assert.False(kernel.Goals.SelectMany(goal => goal.Tasks).Any(task => task.LastProcess is { IsRunning: true }));
            Assert.False(summary.StopRequested);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_idle_without_running_workers_keeps_default_fallback")]
    public void WatchModeIdleWithoutRunningWorkersKeepsDefaultFallback()
    {
        var kernel = new AgentOrchestratorKernel();
        var stopFile = NoStopPath();
        var wakeSignal = new TestWakeSignal(waitNumber =>
        {
            if (waitNumber == 3)
            {
                File.WriteAllText(stopFile, "stop");
            }

            return false;
        });

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                stopFile,
                maxIterations: 1,
                watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                wakeSignal: wakeSignal,
                keepAliveWhenIdle: true);

            Assert.Equal(TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds), TimeSpan.FromTicks(wakeSignal.Timeouts.Sum(t => t.Ticks)));
            Assert.All(wakeSignal.TrackedExitCodePathUpdates, update => Assert.Empty(update));
            Assert.True(wakeSignal.Timeouts.All(timeout => timeout <= TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds)));
            Assert.Equal(0, summary.Ticks);
            Assert.True(summary.StopRequested);
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_InjectableSleep_StopReturnedFromSleepFunc")]
    public void WatchMode_InjectableSleep_StopReturnedFromSleepFunc()
    {
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "held goal");

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();

        // Inject a sleep func that immediately signals stop (returns true).
        Func<TimeSpan, bool> fakeSleepStop = _ => true;

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
                watchInterval: TimeSpan.FromSeconds(15),
                sleepFunc: fakeSleepStop);

            // Loop exits because the sleep func returned true (stop signaled during sleep)
            Assert.True(summary.StopRequested);
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    private sealed class TestWakeSignal : IConductorWakeSignal
    {
        private readonly Func<int, bool>? _wait;
        private int _waits;

        public TestWakeSignal(Action? onFirstWait = null)
            : this(onFirstWait is null
                ? null
                : waitNumber =>
                {
                    if (waitNumber == 1)
                    {
                        onFirstWait();
                        return true;
                    }

                    return false;
                })
        {
        }

        public TestWakeSignal(Func<int, bool>? wait)
        {
            _wait = wait;
        }

        public List<TimeSpan> Timeouts { get; } = [];

        public List<IReadOnlyList<string>> TrackedExitCodePathUpdates { get; } = [];

        public int SignaledWaits { get; private set; }

        public void UpdateTrackedExitArtifacts(IReadOnlyCollection<string> exitCodePaths)
        {
            TrackedExitCodePathUpdates.Add(exitCodePaths.ToArray());
        }

        public bool Wait(TimeSpan timeout)
        {
            Timeouts.Add(timeout);
            var signaled = _wait?.Invoke(++_waits) ?? false;
            if (signaled)
            {
                SignaledWaits++;
            }

            return signaled;
        }

        public void Dispose()
        {
        }
    }

    // ── Progress emission: compact lines emitted to stdout ───────────────

    [Xunit.Fact(DisplayName = "ProgressEmission_CompactLinesIncludeTickAndGoalEvents")]
    public void ProgressEmission_CompactLinesIncludeTickAndGoalEvents()
    {
        var (kernel, _) = SimpleGoal();

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var stopFile = NoStopPath();

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 1);
        });

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(lines.Any(l => l.StartsWith("TICK tick=1 eligible=", StringComparison.Ordinal)));
        Assert.True(lines.Any(l => l.StartsWith("GOAL goal=", StringComparison.Ordinal)));
        Assert.True(lines.Any(l => l.StartsWith("TICK_END tick=1 ", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "ProgressEmission_TickSummaryContainsProgressLines")]
    public void ProgressEmission_TickSummaryContainsProgressLines()
    {
        var (kernel, _) = SimpleGoal();

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var stopFile = NoStopPath();
        BatchTickSummary? capturedTick = null;

        new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
            maxIterations: 1, onTick: t => capturedTick = t);

        Assert.True(capturedTick is not null);
        var lines = capturedTick!.ProgressLines;
        Assert.True(lines is not null && lines.Count > 0);
        Assert.True(lines!.Any(l => l.StartsWith("TICK ", StringComparison.Ordinal)));
        Assert.True(lines.Any(l => l.StartsWith("GOAL ", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_shared_stream_receives_events_from_sequential_loop_instances")]
    public void ConductEventsSharedStreamReceivesEventsFromSequentialLoopInstances()
    {
        var root = CreateTempDirectory("mcg-conduct-events");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        string output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var (firstKernel, _) = SimpleGoal("first conduct event stream goal");
            new ConductorBatchLoop(conductEventLogWriter: writer).Run(
                firstKernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);

            var (secondKernel, _) = SimpleGoal("second conduct event stream goal");
            new ConductorBatchLoop(conductEventLogWriter: writer).Run(
                secondKernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
        });

        Assert.Contains("GOAL goal=", output, StringComparison.Ordinal);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.True(records.Count(record => record.EventKind == "loop-start") >= 2);
        Assert.Contains(records, record => record.EventKind == "goal" && record.GoalId is not null);
    }

    [Xunit.Fact(DisplayName = "ConductEvents_required_rollback_survives_transient_stream_write_failure")]
    public void ConductEventsRequiredRollbackSurvivesTransientStreamWriteFailure()
    {
        var root = CreateTempDirectory("mcg-conduct-events-required");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var writer = new ConductEventLogWriter(logPath);

        bool appendedImmediately;
        using (var streamLock = new FileStream(
            logPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read))
        {
            appendedImmediately = writer.AppendRequired(
                "loop-relaunch-rollback",
                "goal1234",
                "LOOP_RELAUNCH_ROLLBACK goal=goal1234 phase=self-check rolledBack=true continuing=true");
        }

        Assert.False(appendedImmediately);
        Assert.Single(Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(logPath)!, ConductEventLogWriter.PendingEventsDirectoryName),
            $"{Path.GetFileName(logPath)}.pending-*.jsonl"));

        writer.Append("loop-stop", null, "LOOP_STOP tick=2 reason=test");

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Contains(records, record =>
            record.EventKind == "loop-relaunch-rollback" &&
            record.GoalId == "goal1234" &&
            record.Detail.Contains("continuing=true", StringComparison.Ordinal));
        Assert.Contains(records, record => record.EventKind == "loop-stop");
        Assert.Empty(Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(logPath)!, ConductEventLogWriter.PendingEventsDirectoryName),
            $"{Path.GetFileName(logPath)}.pending-*.jsonl"));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_migrates_and_drains_legacy_parent_pending_events")]
    public void ConductEventsMigratesAndDrainsLegacyParentPendingEvents()
    {
        var root = CreateTempDirectory("mcg-conduct-events-legacy-pending");
        var logDirectory = Path.Combine(root, ".orchestrator", "logs");
        var logPath = Path.Combine(logDirectory, ConductEventLogWriter.CurrentFileName);
        Directory.CreateDirectory(logDirectory);
        var legacyPendingPath = Path.Combine(
            logDirectory,
            $"{Path.GetFileName(logPath)}.pending-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(
            legacyPendingPath,
            JsonSerializer.Serialize(
                new ConductEventRecord(
                    DateTimeOffset.Parse("2026-07-27T12:00:00Z"),
                    "legacy-required",
                    "goal1234",
                    "LEGACY_REQUIRED goal=goal1234"),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) + Environment.NewLine);

        var writer = new ConductEventLogWriter(logPath);
        writer.Append("loop-stop", null, "LOOP_STOP tick=1 reason=test");

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Contains(records, record => record.EventKind == "legacy-required" && record.GoalId == "goal1234");
        Assert.Contains(records, record => record.EventKind == "loop-stop");
        Assert.False(File.Exists(legacyPendingPath));
        Assert.Empty(Directory.GetFiles(
            Path.Combine(logDirectory, ConductEventLogWriter.PendingEventsDirectoryName),
            $"{Path.GetFileName(logPath)}.pending-*.jsonl"));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_migrates_legacy_pending_events_for_each_log_name")]
    public void ConductEventsMigratesLegacyPendingEventsForEachLogName()
    {
        var root = CreateTempDirectory("mcg-conduct-events-legacy-pending-names");
        var logDirectory = Path.Combine(root, ".orchestrator", "logs");
        Directory.CreateDirectory(logDirectory);
        var firstLogPath = Path.Combine(logDirectory, "first-events.log");
        var secondLogPath = Path.Combine(logDirectory, "second-events.log");

        static string WriteLegacyPending(string logDirectory, string logPath, string eventKind)
        {
            var pendingPath = Path.Combine(
                logDirectory,
                $"{Path.GetFileName(logPath)}.pending-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(
                pendingPath,
                JsonSerializer.Serialize(
                    new ConductEventRecord(
                        DateTimeOffset.Parse("2026-07-27T12:00:00Z"),
                        eventKind,
                        null,
                        eventKind),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) + Environment.NewLine);
            return pendingPath;
        }

        var firstPendingPath = WriteLegacyPending(logDirectory, firstLogPath, "first-legacy");
        var secondPendingPath = WriteLegacyPending(logDirectory, secondLogPath, "second-legacy");

        var firstWriter = new ConductEventLogWriter(firstLogPath);
        var secondWriter = new ConductEventLogWriter(secondLogPath);
        firstWriter.Append("first-current", null, "first-current");
        secondWriter.Append("second-current", null, "second-current");

        Assert.Contains("\"eventKind\":\"first-legacy\"", File.ReadAllText(firstLogPath), StringComparison.Ordinal);
        Assert.Contains("\"eventKind\":\"second-legacy\"", File.ReadAllText(secondLogPath), StringComparison.Ordinal);
        Assert.False(File.Exists(firstPendingPath));
        Assert.False(File.Exists(secondPendingPath));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_parallel_required_writers_drain_each_event_exactly_once")]
    public async Task ConductEventsParallelRequiredWritersDrainEachEventExactlyOnce()
    {
        var root = CreateTempDirectory("mcg-conduct-events-parallel-required");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        using var drainEntrants = new CountdownEvent(2);
        using var releaseDrain = new ManualResetEventSlim();

        void BeforeDrain()
        {
            drainEntrants.Signal();
            releaseDrain.Wait();
        }

        var firstWriter = new ConductEventLogWriter(logPath, beforeRequiredEventDrain: BeforeDrain);
        var secondWriter = new ConductEventLogWriter(logPath, beforeRequiredEventDrain: BeforeDrain);
        var writes = new[]
        {
            Task.Run(() => firstWriter.AppendRequired(
                "gate-progress",
                "goal0001",
                "PHASE_PROGRESS goal=goal0001 phase=infrastructure-shard target=first")),
            Task.Run(() => secondWriter.AppendRequired(
                "gate-progress",
                "goal0002",
                "PHASE_PROGRESS goal=goal0002 phase=infrastructure-shard target=second"))
        };

        try
        {
            Assert.True(
                drainEntrants.Wait(TimeSpan.FromSeconds(5)),
                "Both writers must enter the drain concurrently before either is released.");
        }
        finally
        {
            releaseDrain.Set();
        }

        var appendVerdicts = await Task.WhenAll(writes);
        Assert.All(appendVerdicts, Assert.True);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Equal(2, records.Length);
        Assert.Single(records, record => record.EventKind == "gate-progress" && record.GoalId == "goal0001");
        Assert.Single(records, record => record.EventKind == "gate-progress" && record.GoalId == "goal0002");
        Assert.Empty(Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(logPath)!, ConductEventLogWriter.PendingEventsDirectoryName),
            $"{Path.GetFileName(logPath)}.pending-*.jsonl"));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_parallel_append_and_required_write_serialize_forced_rotation")]
    public async Task ConductEventsParallelAppendAndRequiredWriteSerializeForcedRotation()
    {
        var root = CreateTempDirectory("mcg-conduct-events-parallel-rotation");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var timestamp = DateTimeOffset.Parse("2026-07-25T18:00:00Z");
        new ConductEventLogWriter(logPath, maxBytes: 0, utcNow: () => timestamp).Append(
            "seed",
            null,
            new string('x', 128));

        using var appendCommitReached = new ManualResetEventSlim();
        using var releaseAppendCommit = new ManualResetEventSlim();
        using var requiredDrainAttempted = new ManualResetEventSlim();
        var appendWriter = new ConductEventLogWriter(
            logPath,
            maxBytes: 32,
            utcNow: () => timestamp,
            beforeAppendCommit: () =>
            {
                appendCommitReached.Set();
                releaseAppendCommit.Wait();
            });
        var requiredWriter = new ConductEventLogWriter(
            logPath,
            maxBytes: 32,
            utcNow: () => timestamp,
            beforeRequiredEventDrain: requiredDrainAttempted.Set);

        var append = Task.Run(() => appendWriter.Append(
            "gate-total",
            "goal0001",
            "GATE_TOTAL goal=goal0001 durationMs=100"));
        Task<bool>? required = null;
        try
        {
            Assert.True(
                appendCommitReached.Wait(TimeSpan.FromSeconds(5)),
                "The append writer must reach the forced-rotation commit boundary.");
            required = Task.Run(() => requiredWriter.AppendRequired(
                "gate-progress",
                "goal0002",
                "PHASE_PROGRESS goal=goal0002 phase=infrastructure-shard target=required"));
            Assert.True(
                requiredDrainAttempted.Wait(TimeSpan.FromSeconds(5)),
                "The required writer must attempt its drain while the append commit is held.");
            Assert.False(
                required.IsCompleted,
                "The required writer must remain serialized behind the append rotation and write.");
        }
        finally
        {
            releaseAppendCommit.Set();
        }

        await append;
        Assert.NotNull(required);
        Assert.True(await required);

        var records = Directory.GetFiles(Path.GetDirectoryName(logPath)!, "conduct-events*.log")
            .SelectMany(File.ReadAllLines)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Single(records, record => record.EventKind == "gate-total" && record.GoalId == "goal0001");
        Assert.Single(records, record => record.EventKind == "gate-progress" && record.GoalId == "goal0002");
        Assert.Empty(Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(logPath)!, ConductEventLogWriter.PendingEventsDirectoryName),
            $"{Path.GetFileName(logPath)}.pending-*.jsonl"));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_skips_janitorial_phase_failure_and_journals_event")]
    public void BatchLoopSkipsJanitorialPhaseFailureAndJournalsEvent()
    {
        var root = CreateTempDirectory("mcg-conduct-events-janitorial");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var driver = MakeDriver();

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var summary = new ConductorBatchLoop(
                measuredSweep: _ => throw new InvalidOperationException("janitorial access denied"),
                conductEventLogWriter: writer).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.False(summary.StopRequested);
        });

        Assert.Contains("LOOP_JANITORIAL_FAILED", output, StringComparison.Ordinal);
        Assert.Contains("LOOP_STOP", output, StringComparison.Ordinal);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.Contains(records, record =>
            record.EventKind == "loop-janitorial-failure" &&
            record.Detail.Contains("exception=InvalidOperationException", StringComparison.Ordinal) &&
            record.Detail.Contains("janitorial_access_denied", StringComparison.Ordinal));
        Assert.Contains(records, record => record.EventKind == "loop-stop");
    }

    [Xunit.Fact(DisplayName = "BatchLoop_skips_idle_wake_janitorial_failure_and_journals_event")]
    public void BatchLoopSkipsIdleWakeJanitorialFailureAndJournalsEvent()
    {
        var root = CreateTempDirectory("mcg-conduct-events-idle-wake-janitorial");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var stopFile = NoStopPath();
        var wakeSignal = new TestWakeSignal(() => File.WriteAllText(stopFile, "stop"));

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var summary = new ConductorBatchLoop(
                    measuredSweep: _ => throw new InvalidOperationException("idle wake janitorial access denied"),
                    conductEventLogWriter: writer).Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    maxIterations: 1,
                    watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                    wakeSignal: wakeSignal,
                    keepAliveWhenIdle: true);

                Assert.True(summary.StopRequested);
            });

            Assert.Contains("LOOP_JANITORIAL_FAILED", output, StringComparison.Ordinal);
            Assert.Contains("LOOP_STOP", output, StringComparison.Ordinal);

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();

            Assert.Contains(records, record =>
                record.EventKind == "loop-janitorial-failure" &&
                record.Detail.Contains("phase=idle-wake-sweep", StringComparison.Ordinal) &&
                record.Detail.Contains("idle_wake_janitorial_access_denied", StringComparison.Ordinal));
            Assert.Contains(records, record => record.EventKind == "loop-stop");
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_skips_watch_wake_janitorial_failure_and_journals_event")]
    public void BatchLoopSkipsWatchWakeJanitorialFailureAndJournalsEvent()
    {
        var root = CreateTempDirectory("mcg-conduct-events-watch-wake-janitorial");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var (kernel, _) = SimpleGoal("held wake janitorial failure goal");
        var stopFile = NoStopPath();
        var wakeSignal = new TestWakeSignal(() => File.WriteAllText(stopFile, "stop"));
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var summary = new ConductorBatchLoop(
                    measuredSweep: _ => throw new InvalidOperationException("watch wake janitorial access denied"),
                    conductEventLogWriter: writer).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    maxIterations: 1,
                    watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                    wakeSignal: wakeSignal);

                Assert.True(summary.StopRequested);
            });

            Assert.Contains("LOOP_JANITORIAL_FAILED", output, StringComparison.Ordinal);
            Assert.Contains("LOOP_STOP", output, StringComparison.Ordinal);

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();

            Assert.Contains(records, record =>
                record.EventKind == "loop-janitorial-failure" &&
                record.Detail.Contains("phase=wake-sweep", StringComparison.Ordinal) &&
                record.Detail.Contains("watch_wake_janitorial_access_denied", StringComparison.Ordinal));
            Assert.Contains(records, record => record.EventKind == "loop-stop");
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "ConductEvents_shared_stream_records_escalation_reason")]
    public void ConductEventsSharedStreamRecordsEscalationReason()
    {
        var root = CreateTempDirectory("mcg-conduct-events-escalation");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "escalation event stream goal");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ => false);

        new ConductorBatchLoop(conductEventLogWriter: writer).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            maxVerifyRetries: 0);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.Contains(records, record =>
            record.EventKind == "goal-escalation" &&
            record.GoalId == goal.Id.Value[..8] &&
            record.Detail.Contains("reason=Acceptance_verification_failed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_rollover_preserves_stable_current_filename")]
    public void ConductEventsRolloverPreservesStableCurrentFilename()
    {
        var root = CreateTempDirectory("mcg-conduct-events-rollover");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, new string('x', 128));

        var writer = new ConductEventLogWriter(
            logPath,
            maxBytes: 32,
            utcNow: () => DateTimeOffset.Parse("2026-07-13T02:30:00Z"));

        writer.Append("loop-stop", null, "LOOP_STOP tick=0 reason=test");

        Assert.True(File.Exists(logPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(logPath)!, "conduct-events-*.log"));
        var current = JsonSerializer.Deserialize<ConductEventRecord>(
            File.ReadAllText(logPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("loop-stop", current.EventKind);
    }

    [Xunit.Fact(DisplayName = "ProgressEmission_TickSummaryContainsPhaseTimingLines")]
    public void ProgressEmission_TickSummaryContainsPhaseTimingLines()
    {
        var (kernel, _) = SimpleGoal("phase timing dispatch");
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));
        BatchTickSummary? capturedTick = null;

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            onTick: tick => capturedTick = tick);

        var lines = capturedTick!.ProgressLines!;
        Assert.Contains(lines, line => line.StartsWith("PHASE_TIMING tick=1 phase=sweep ", StringComparison.Ordinal) && line.Contains(" ts=", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("PHASE_TIMING tick=1 phase=prewalk ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("PHASE_TIMING tick=1 phase=dispatch-prep ", StringComparison.Ordinal) && line.Contains(" task=", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("PHASE_TIMING tick=1 phase=per-goal-walk ", StringComparison.Ordinal) && line.Contains("slowest=", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ProgressEmission_WatchHeldRunningEmitsOnlyChangedDisposition")]
    public void ProgressEmission_WatchHeldRunningEmitsOnlyChangedDisposition()
    {
        var (kernel, _) = SimpleGoal();

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var ticks = new List<BatchTickSummary>();
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 4,
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => false,
                onTick: ticks.Add);
        });

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(4, ticks.Count);
        Assert.Single(lines.Where(l => l.StartsWith("GOAL goal=", StringComparison.Ordinal)));
        Assert.Single(lines.Where(l => l.StartsWith("TICK_END tick=", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.StartsWith("TICK_END tick=2 ", StringComparison.Ordinal));
        Assert.True(ticks.Skip(1).All(t =>
            t.ProgressLines is not null &&
            t.ProgressLines.All(line => line.StartsWith("PHASE_TIMING ", StringComparison.Ordinal))));
    }

    [Xunit.Fact(DisplayName = "ConductorTick_includes_operator_disposition_snapshot")]
    public void ConductorTickIncludesOperatorDispositionSnapshot()
    {
        var (kernel, goal) = SimpleGoal("operator disposition tick");
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));
        BatchTickSummary? captured = null;

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            onTick: tick => captured = tick,
            buildOperatorDispositions: _ =>
            [
                new ConductorOperatorDispositionSnapshot(
                    goal.Id.Value,
                    OperatorDispositionState.Wait,
                    OperatorDispositionConfidence.High,
                    "conductor snapshot",
                    "wait",
                    DateTimeOffset.Parse("2026-07-03T12:10:00Z"),
                    [],
                    [],
                    [])
            ]);

        var snapshot = Assert.Single(captured!.OperatorDispositions!);
        Assert.Equal(goal.Id.Value, snapshot.GoalId);
        Assert.Equal(OperatorDispositionState.Wait, snapshot.State);
        Assert.Equal("conductor snapshot", snapshot.Reason);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_emits_dispatch_role_liveness_bytes_and_files")]
    public void WatchProgressEmitsDispatchRoleLivenessBytesAndFiles()
    {
        var (kernel, goal) = SimpleGoal("watch progress");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-2), "abc123");
        var reporter = FakeWatchReporter(
            now,
            stdoutBytes: 42,
            stderrBytes: 8,
            idle: TimeSpan.FromSeconds(15),
            ownedPids: [111, 222],
            alivePids: [222],
            files: ["src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => true,
            onTick: ticks.Add);

        var line = ticks.Single().ProgressLines!.Single(l => l.StartsWith("WATCH_PROGRESS ", StringComparison.Ordinal));
        Assert.Contains($"goal={goal.Id.Value[..8]}", line);
        Assert.Contains($"role={task.RequiredRole}", line);
        Assert.Contains("task=1/", line);
        Assert.Contains("elapsed=2m0s", line);
        Assert.Contains("liveness=\"alive\"", line);
        Assert.Contains("output_delta=50", line);
        Assert.Contains("last_progress_age=15s", line);
        Assert.Contains("files=4", line);
        Assert.Contains("src/A.cs", line);
        Assert.Contains("+1 more", line);

        var human = ticks.Single().ProgressLines!.Single(l => l.StartsWith($"[{goal.Id.Value[..8]}] {task.RequiredRole}", StringComparison.Ordinal));
        Assert.Contains("(task 1/", human);
        Assert.Contains("running 2m0s", human);
        Assert.Contains("worker pid 222 alive", human);
        Assert.Contains("+42B stdout", human);
        Assert.Contains("last progress 15s ago", human);
        Assert.Contains("4 files changed (src/A.cs, src/B.cs, src/C.cs, +1 more)", human);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_throttles_until_output_or_file_count_changes")]
    public void WatchProgressThrottlesUntilOutputOrFileCountChanges()
    {
        var (kernel, goal) = SimpleGoal("watch throttle");
        var task = goal.Tasks.First();
        var start = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, start.AddMinutes(-1), "abc123");
        var nowCalls = 0;
        var heartbeatCalls = 0;
        var reporter = new ConductorWatchProgressReporter(
            readHeartbeat: (process, observedAt) =>
            {
                heartbeatCalls++;
                var bytes = heartbeatCalls < 3 ? 10 : 11;
                return Heartbeat(process, observedAt, bytes, 0, TimeSpan.FromSeconds(5), [111]);
            },
            readChanges: (_, _) => new DispatchLiveChangeSnapshot(["src/A.cs"], ["src/A.cs"], 0),
            isProcessAlive: pid => pid == 111,
            now: () => start.AddSeconds(nowCalls++ * 10));
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        var progressLines = ticks.SelectMany(t => t.ProgressLines ?? []).Where(l => l.StartsWith("WATCH_PROGRESS ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, progressLines.Length);
        Assert.Contains("output_delta=1", progressLines[1]);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_quiet_suppresses_human_lines_but_keeps_machine_lines")]
    public void WatchProgressQuietSuppressesHumanLinesButKeepsMachineLines()
    {
        var (kernel, goal) = SimpleGoal("watch quiet");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-1), "abc123");
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(5), [111], [111], ["src/A.cs"]);

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop(watchProgressReporter: reporter).Run(
                kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => true,
                quiet: true);
        });

        Assert.Contains("TICK tick=1", output);
        Assert.DoesNotContain("WATCH_PROGRESS", output);
        Assert.DoesNotContain("WATCH_WARNING", output);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_warns_when_owned_pids_are_dead")]
    public void WatchProgressWarnsWhenOwnedPidsAreDead()
    {
        var (kernel, goal) = SimpleGoal("watch warning");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-1), "abc123");
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(20), [111], [], ["src/A.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => true,
            onTick: ticks.Add);

        var warning = ticks.Single().ProgressLines!.Single(l => l.StartsWith("WATCH_WARNING ", StringComparison.Ordinal));
        Assert.Contains($"goal={goal.Id.Value[..8]}", warning);
        Assert.Contains("reason=no-live-worker", warning);

        var human = ticks.Single().ProgressLines!.Single(l => l.StartsWith($"[{goal.Id.Value[..8]}] WARNING:", StringComparison.Ordinal));
        Assert.Contains("no worker progress for 20s", human);
        Assert.Contains("possible stall", human);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_emits_transition_after_role_completion")]
    public void WatchProgressEmitsTransitionAfterRoleCompletion()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var second = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("watch transition", [first, second]);
        kernel.ActivateGoal(goal.Id, BuildAgents(AgentRole.Planner, AgentRole.Developer));
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, first, now.AddMinutes(-3), "abc123");
        var calls = 0;
        Action<AgentOrchestratorKernel> sweep = loopKernel =>
        {
            calls++;
            if (calls != 2)
            {
                return;
            }

            loopKernel.RecordDispatchResultCommit(goal.Id, first.Id, "deadbeefcafebabe");
            var firstProcess = loopKernel.GetTask(goal.Id, first.Id).LastProcess!;
            loopKernel.RecordTaskProcessRefreshed(goal.Id, first.Id, firstProcess with { CompletedAt = now, ExitCode = 0 }, null);
            loopKernel.RecordTaskVerification(goal.Id, first.Id, new TaskVerificationRecord("manual", "C:\\repo", 0, "ok", "", now));
            StartProcess(loopKernel, goal, second, now.AddMinutes(-1), "def456", processId: 222);
        };
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(5), [111, 222], [111, 222], ["src/A.cs", "src/B.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(sweep: sweep, watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        var transition = ticks.SelectMany(t => t.ProgressLines ?? []).Single(l => l.StartsWith("WATCH_TRANSITION ", StringComparison.Ordinal));
        Assert.Contains("Planner=✓", transition);
        Assert.Contains("commit=deadbeefcafe", transition);
        Assert.Contains("files=2", transition);
        Assert.Contains("elapsed=3m0s", transition);
        Assert.Contains("next=Developer", transition);
        Assert.Contains("task=2/2", transition);
        Assert.True(ticks.SelectMany(t => t.ProgressLines ?? []).Any(l => l.Contains("role=Developer", StringComparison.Ordinal)));

        var human = ticks.SelectMany(t => t.ProgressLines ?? []).Single(l => l.StartsWith($"[{goal.Id.Value[..8]}] Planner - committed", StringComparison.Ordinal));
        Assert.Contains("committed deadbee", human);
        Assert.Contains("(2 files changed, 3m0s)", human);
        Assert.Contains("-> Developer dispatched", human);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_emits_final_transition_before_gated_lifecycle_event")]
    public void WatchProgressEmitsFinalTransitionBeforeGatedLifecycleEvent()
    {
        var (kernel, goal) = SimpleGoal("watch final gated transition");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-3), "abc123");
        var calls = 0;
        Action<AgentOrchestratorKernel> sweep = loopKernel =>
        {
            calls++;
            if (calls == 2)
            {
                CompleteDispatchedTask(loopKernel, goal, task, now, "feedfacecafebabe");
            }
        };
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(5), [111], [111], ["src/A.cs", "src/B.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(sweep: sweep, watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        var terminalTick = Assert.Single(ticks.Where(t => (t.ProgressLines ?? [])
            .Any(l => l.StartsWith("WATCH_TRANSITION ", StringComparison.Ordinal))));
        var lines = terminalTick.ProgressLines!.ToList();
        var transitionIndex = lines.FindIndex(l => l.StartsWith("WATCH_TRANSITION ", StringComparison.Ordinal));
        var lifecycleIndex = lines.FindIndex(l => l.StartsWith($"GOAL goal={goal.Id.Value[..8]} ", StringComparison.Ordinal));
        Assert.True(transitionIndex >= 0);
        Assert.True(lifecycleIndex >= 0);
        Assert.True(transitionIndex < lifecycleIndex);

        var transition = lines[transitionIndex];
        Assert.Contains($"{task.RequiredRole}=✓", transition);
        Assert.Contains("commit=feedfacecafe", transition);
        Assert.Contains("files=2", transition);
        Assert.Contains("elapsed=3m0s", transition);
        Assert.Contains("next=acceptance-gate", transition);
        Assert.Contains("task=1/1", transition);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_emits_final_transition_before_ungated_lifecycle_event")]
    public void WatchProgressEmitsFinalTransitionBeforeUngatedLifecycleEvent()
    {
        var (kernel, goal) = SimpleGoal("watch final ungated transition");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-3), "abc123");
        var calls = 0;
        Action<AgentOrchestratorKernel> sweep = loopKernel =>
        {
            calls++;
            if (calls == 2)
            {
                CompleteDispatchedTask(loopKernel, goal, task, now, "0123456789abcdef");
            }
        };
        var policy = PolicyEscalatingAtVerified();
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(5), [111], [111], ["src/A.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(sweep: sweep, watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            policy,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        var terminalTick = Assert.Single(ticks.Where(t => (t.ProgressLines ?? [])
            .Any(l => l.StartsWith("WATCH_TRANSITION ", StringComparison.Ordinal))));
        var lines = terminalTick.ProgressLines!.ToList();
        var transitionIndex = lines.FindIndex(l => l.StartsWith("WATCH_TRANSITION ", StringComparison.Ordinal));
        var lifecycleIndex = lines.FindIndex(l => l.StartsWith($"GOAL goal={goal.Id.Value[..8]} ", StringComparison.Ordinal));
        Assert.True(transitionIndex >= 0);
        Assert.True(lifecycleIndex >= 0);
        Assert.True(transitionIndex < lifecycleIndex);

        var transition = lines[transitionIndex];
        Assert.Contains($"{task.RequiredRole}=✓", transition);
        Assert.Contains("commit=0123456789ab", transition);
        Assert.Contains("files=1", transition);
        Assert.Contains("elapsed=3m0s", transition);
        Assert.Contains("next=none", transition);
        Assert.Contains("task=1/1", transition);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_uses_operator_supplied_stall_warning_threshold")]
    public void WatchProgressUsesOperatorSuppliedStallWarningThreshold()
    {
        var (kernel, goal) = SimpleGoal("watch custom stall threshold");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-1), "abc123");
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(45), [111], [111], ["src/A.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => true,
            onTick: ticks.Add,
            stallWarningThreshold: TimeSpan.FromSeconds(30));

        var warning = ticks.Single().ProgressLines!.Single(l => l.StartsWith("WATCH_WARNING ", StringComparison.Ordinal));
        Assert.Contains("reason=last-progress-stale", warning);
        Assert.Contains("stall=45s", warning);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_default_stall_threshold_uses_four_poll_intervals_when_larger")]
    public void WatchProgressDefaultStallThresholdUsesFourPollIntervalsWhenLarger()
    {
        var (kernel, goal) = SimpleGoal("watch poll threshold");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-30), "abc123");
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromMinutes(12), [111], [111], ["src/A.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            watchInterval: TimeSpan.FromMinutes(5),
            sleepFunc: _ => true,
            onTick: ticks.Add);

        Assert.DoesNotContain(ticks.Single().ProgressLines!, l => l.StartsWith("WATCH_WARNING ", StringComparison.Ordinal));
        Assert.DoesNotContain(ticks.Single().ProgressLines!, l => l.Contains("WARNING:", StringComparison.Ordinal));
    }

    // ── Fault isolation: a throwing goal is escalated, others still advance ─

    [Xunit.Fact(DisplayName = "BatchLoop_FaultIsolation_ThrowingGoalEscalated_HealthyGoalStillAdvanced")]
    public void BatchLoop_FaultIsolation_ThrowingGoalEscalated_HealthyGoalStillAdvanced()
    {
        var kernel = new AgentOrchestratorKernel();
        var faultyGoal  = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "faulty goal");
        var healthyGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "healthy goal");

        var advancedGoalIds = new List<string>();
        var driver = MakeDriver(
            getFacts: g =>
            {
                if (g.Id == faultyGoal.Id)
                    throw new InvalidOperationException("Assigned agent 'missing-agent' was not found.");
                return new GoalLifecycleFacts(WorkspaceExists: true);
            },
            dispatchAndStart: g =>
            {
                advancedGoalIds.Add(g.Id.Value);
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 1);

        // Faulty goal must be counted escalated and excluded
        Assert.Equal(1, summary.Escalated);
        // Healthy goal must have been advanced
        Assert.True(advancedGoalIds.Contains(healthyGoal.Id.Value));
        // Loop must complete (not throw)
        Assert.Equal(1, summary.Ticks);
    }

    // ── Terminal-goal skip: terminal historical goals not in eligible set ─

    [Xunit.Fact(DisplayName = "BatchLoop_terminal_historical_goals_are_excluded_from_eligible_set")]
    public void BatchLoopTerminalHistoricalGoalsAreExcludedFromEligibleSet()
    {
        var kernel = new AgentOrchestratorKernel();
        var verifiedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "verified goal");
        var cleanedUpGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "cleaned-up goal");
        var cancelledGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "cancelled goal");
        var supersededGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "superseded goal");

        PassVerification(kernel, verifiedGoal, verifiedGoal.Tasks.Single());
        PassVerification(kernel, cleanedUpGoal, cleanedUpGoal.Tasks.Single());
        kernel.CompleteGoal(cleanedUpGoal.Id, "Test fixture: historical goal already landed, recorded, and cleaned up.");
        kernel.CancelGoal(cancelledGoal.Id, "test cancel");
        kernel.SupersedeGoal(supersededGoal.Id, "test supersede");

        var terminalGoalIds = new HashSet<GoalId>
        {
            cleanedUpGoal.Id,
            cancelledGoal.Id,
            supersededGoal.Id
        };
        var driverCalls = new List<GoalId>();
        var advancedGoalIds = new List<GoalId>();
        var driver = MakeDriver(
            getFacts: goal =>
            {
                driverCalls.Add(goal.Id);
                return goal.Id == cleanedUpGoal.Id
                    ? new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : GoalLifecycleFacts.None;
            },
            land: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok");
            },
            record: goal => advancedGoalIds.Add(goal.Id),
            cleanup: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null);
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3);

        Assert.Equal(3, summary.Ticks);
        Assert.Equal(3, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.Contains(verifiedGoal.Id, driverCalls);
        Assert.Empty(advancedGoalIds.Where(terminalGoalIds.Contains));
    }

    [Xunit.Theory(DisplayName = "BatchLoop_stale_terminal_goals_with_assigned_work_are_excluded_from_processing_set")]
    [Xunit.InlineData(GoalStatus.Completed)]
    [Xunit.InlineData(GoalStatus.Cancelled)]
    [Xunit.InlineData(GoalStatus.Failed)]
    public void BatchLoopStaleTerminalGoalsWithAssignedWorkAreExcludedFromProcessingSet(GoalStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var staleTask = new TaskSpec(TaskId.New(), "Stale assigned work", AgentRole.Developer);
        var staleGoal = kernel.CreateGoal($"Stale {status}", [staleTask]);
        var activeGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Active conductor goal");
        kernel.ActivateGoal(staleGoal.Id, DefaultAgents());
        kernel = WithGoalStatus(kernel, staleGoal.Id, status);

        var advancedGoalIds = new List<GoalId>();
        var driver = MakeDriver(
            createWorkspace: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return "/tmp/workspace";
            },
            dispatchAndStart: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, summary.Ticks);
        Assert.Equal(1, summary.Advanced);
        Assert.Contains(activeGoal.Id, advancedGoalIds);
        Assert.DoesNotContain(staleGoal.Id, advancedGoalIds);
        Assert.Equal(status, kernel.GetGoal(staleGoal.Id).Status);
        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(staleGoal.Id, staleTask.Id).Status);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_pre_walk_exclusion_avoids_lifecycle_fact_reads_for_inert_goals")]
    public void BatchLoopPreWalkExclusionAvoidsLifecycleFactReadsForInertGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "cancelled");
        var superseded = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "superseded");
        var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "parked");
        var staleTask = new TaskSpec(TaskId.New(), "stale assigned", AgentRole.Developer);
        var staleCompleted = kernel.CreateGoal("stale completed", [staleTask]);
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active");
        kernel.ActivateGoal(staleCompleted.Id, DefaultAgents());
        kernel.CancelGoal(cancelled.Id, "cancelled");
        kernel.SupersedeGoal(superseded.Id, "superseded");
        kernel.ParkGoal(parked.Id, "parked");
        kernel = WithGoalStatus(kernel, staleCompleted.Id, GoalStatus.Completed);

        var inertGoalIds = new HashSet<GoalId> { cancelled.Id, superseded.Id, parked.Id, staleCompleted.Id };
        var factReads = new List<GoalId>();
        var advancedGoalIds = new List<GoalId>();
        var driver = MakeDriver(
            getFacts: goal =>
            {
                factReads.Add(goal.Id);
                if (inertGoalIds.Contains(goal.Id))
                {
                    throw new InvalidOperationException("Inert goal should have been excluded before lifecycle facts.");
                }

                return GoalLifecycleFacts.None;
            },
            createWorkspace: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return "/tmp/workspace";
            });

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Contains(active.Id, advancedGoalIds);
        Assert.DoesNotContain(factReads, inertGoalIds.Contains);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_reuses_unchanged_cleaned_up_projection_between_watch_ticks")]
    public void BatchLoopReusesUnchangedCleanedUpProjectionBetweenWatchTicks()
    {
        var kernel = new AgentOrchestratorKernel();
        var cleanedUp = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "cleaned up");
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active held");
        PassVerification(kernel, cleanedUp, cleanedUp.Tasks.Single());
        kernel.CompleteGoal(cleanedUp.Id, "completed");

        var cleanedFactReads = 0;
        var driver = MakeDriver(
            getFacts: goal =>
            {
                if (goal.Id == cleanedUp.Id)
                {
                    cleanedFactReads++;
                    return new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true);
                }

                return new GoalLifecycleFacts(WorkspaceExists: true);
            },
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => false);

        Assert.Equal(1, cleanedFactReads);
        Assert.Equal(GoalStatus.Completed, kernel.GetGoal(cleanedUp.Id).Status);
        Assert.Equal(GoalStatus.Active, kernel.GetGoal(active.Id).Status);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parked_goal_is_counted_in_summary_without_escalation_and_resumes_when_active")]
    public void BatchLoopParkedGoalIsCountedInSummaryWithoutEscalationAndResumesWhenActive()
    {
        var kernel = new AgentOrchestratorKernel();
        var parkedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Parked conductor goal");
        var activeGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Active conductor goal");
        var request = kernel.RequestHumanInput(parkedGoal.Id, null, "Should this continue?");
        kernel.ParkGoal(parkedGoal.Id, "operator deferred");

        Assert.True(request.IsCompleted);
        Assert.DoesNotContain(kernel.HumanInputRequests, candidate => candidate.GoalId == parkedGoal.Id && !candidate.IsCompleted);

        var advancedGoalIds = new List<GoalId>();
        var escalationReasons = new List<string>();
        var driver = MakeDriver(
            createWorkspace: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return "/tmp/workspace";
            },
            writeEscalation: (_, _, reason) => escalationReasons.Add(reason));

        var parkedOutput = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
        });

        Assert.Empty(escalationReasons);
        Assert.DoesNotContain(parkedGoal.Id, advancedGoalIds);
        Assert.Contains(activeGoal.Id, advancedGoalIds);
        Assert.Contains("TICK tick=1 eligible=1", parkedOutput);
        Assert.Contains("TICK_EXCLUDED tick=1 kind=parked count=1", parkedOutput);
        Assert.Contains("TICK_END tick=1 advanced=1 held=0 escalated=0 done=0", parkedOutput);
        Assert.DoesNotContain($"GOAL goal={parkedGoal.Id.Value[..8]}", parkedOutput);
        Assert.DoesNotContain("AwaitingHumanInput", parkedOutput, StringComparison.Ordinal);

        kernel = WithGoalStatus(kernel, parkedGoal.Id, GoalStatus.Active);
        advancedGoalIds.Clear();
        escalationReasons.Clear();

        var resumedOutput = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
        });

        Assert.Empty(escalationReasons);
        Assert.Contains(parkedGoal.Id, advancedGoalIds);
        Assert.Contains($"GOAL goal={parkedGoal.Id.Value[..8]} result=executed state=Created", resumedOutput);
        Assert.DoesNotContain("TICK_EXCLUDED tick=1 kind=parked", resumedOutput);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_retired_verified_goal_is_excluded_from_tick_eligible_set")]
    public void BatchLoopRetiredVerifiedGoalIsExcludedFromTickEligibleSet()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var retiredGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Retired missing branch goal");
            var activeGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Active conductor goal");
            PassVerification(kernel, retiredGoal, retiredGoal.Tasks.Single());

            var retirementDetail = "Operator retired missing goal branch.";
            GoalOperationJournal.RecordTerminalDisposition(
                root,
                retiredGoal,
                new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, retirementDetail));
            kernel.CompleteGoal(retiredGoal.Id, retirementDetail);
            Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, retiredGoal.Id)));

            GoalOperationJournal.Begin(root, retiredGoal, "conductor:cleanup", "Later interrupted cleanup must not erase retirement.");

            var advancedGoalIds = new List<GoalId>();
            var driver = MakeDriver(
                getFacts: goal =>
                {
                    var journal = GoalOperationJournal.Read(root, goal.Id);
                    return new GoalLifecycleFacts(
                        IsMerged: GoalOperationJournal.HasCompletedLandingEvidence(journal),
                        IsRecorded: GoalOperationJournal.HasCompletedRecordEvidence(journal),
                        IsCleanedUp: GoalOperationJournal.HasCompletedCleanupEvidence(journal));
                },
                createWorkspace: goal =>
                {
                    advancedGoalIds.Add(goal.Id);
                    return "/tmp/workspace";
                },
                dispatchAndStart: goal =>
                {
                    advancedGoalIds.Add(goal.Id);
                    return DispatchStartOutcome.Started();
                });

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1);
            });

            Assert.Contains("TICK tick=1 eligible=1", output);
            Assert.Contains(activeGoal.Id, advancedGoalIds);
            Assert.DoesNotContain(retiredGoal.Id, advancedGoalIds);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_goal_mark_landed_merge_disposition_is_excluded_by_typed_source")]
    public void BatchLoopGoalMarkLandedMergeDispositionIsExcludedByTypedSource()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var landedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Goal marked landed");
            var activeGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Active conductor goal");
            PassVerification(kernel, landedGoal, landedGoal.Tasks.Single());
            GoalOperationJournal.RecordTerminalDisposition(
                root,
                landedGoal,
                new GoalTerminalDisposition(
                    GoalTerminalDispositionKind.Landed,
                    $"Goal {landedGoal.Id.Value[..8]} was marked landed via goal-mark-landed.",
                    GoalTerminalDispositionSource.MergeEvidence));
            kernel.CompleteGoal(landedGoal.Id, "Goal marked landed.");

            var advancedGoalIds = new List<GoalId>();
            var driver = MakeDriver(
                getFacts: goal =>
                {
                    var journal = GoalOperationJournal.Read(root, goal.Id);
                    return new GoalLifecycleFacts(
                        IsMerged: GoalOperationJournal.HasCompletedLandingEvidence(journal),
                        IsRecorded: GoalOperationJournal.HasCompletedRecordEvidence(journal),
                        IsCleanedUp: GoalOperationJournal.HasCompletedCleanupEvidence(journal));
                },
                createWorkspace: goal =>
                {
                    advancedGoalIds.Add(goal.Id);
                    return "/tmp/workspace";
                },
                dispatchAndStart: goal =>
                {
                    advancedGoalIds.Add(goal.Id);
                    return DispatchStartOutcome.Started();
                });

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1);
            });

            Assert.Contains("TICK tick=1 eligible=1", output);
            Assert.Contains(activeGoal.Id, advancedGoalIds);
            Assert.DoesNotContain(landedGoal.Id, advancedGoalIds);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_verified_missing_branch_is_retired_without_tick_escalation")]
    public void BatchLoopVerifiedMissingBranchIsRetiredWithoutTickEscalation()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var missingBranchGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Missing branch pre-landing goal");
            PassVerification(kernel, missingBranchGoal, missingBranchGoal.Tasks.Single());

            var escalationReasons = new List<string>();
            var driver = MakeDriver(
                rebaseOntoMain: goal => new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.MissingBranch,
                    GoalWorktrees.BranchName(goal.Id),
                    $"Goal branch {GoalWorktrees.BranchName(goal.Id)} is missing.",
                    [],
                    "goal-recovery"),
                writeEscalation: (_, _, reason) => escalationReasons.Add(reason),
                recordMissingBranchRetirement: (goal, detail) =>
                {
                    GoalOperationJournal.RecordTerminalDisposition(
                        root,
                        goal,
                        new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, detail));
                    kernel.CompleteGoal(goal.Id, detail);
                });

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1);
            });

            Assert.Empty(escalationReasons);
            Assert.Contains("TICK_END tick=1 advanced=0 held=0 escalated=0 done=1", output);
            Assert.DoesNotContain("pre-landing rebase failed", output, StringComparison.Ordinal);
            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(missingBranchGoal.Id).Status);
            Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, missingBranchGoal.Id)));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
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

    // ── Per-tick write scope: persistGoalTick fires once with exactly the goals that changed ──

    [Xunit.Fact(DisplayName = "PersistGoalTick_busy_exhausted_aborts_changed_goal_tick")]
    public void PersistGoalTickBusyExhaustedAbortsChangedGoalTick()
    {
        var (kernel, goal) = SimpleGoal("busy persistence survives");
        var driver = MakeDriver();
        var ticks = new List<BatchTickSummary>();
        var attempts = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 2,
            onTick: ticks.Add,
            persistGoalTick: (_, _) =>
            {
                attempts++;
                throw SqliteBusy();
            },
            busyWriteDelay: _ => { }));

        Assert.Contains("DISPATCH_RECORD_WRITE_FAILED", ex.Message, StringComparison.Ordinal);
        Assert.Empty(ticks);
        Assert.True(attempts >= ConductorBatchLoop.DefaultMaxBusyWriteAttempts);
        Assert.Contains(goal.Id.Value[..8], ex.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "PersistGoalTick_busy_is_not_retried_after_repository_budget")]
    public void PersistGoalTickBusyIsNotRetriedAfterRepositoryBudget()
    {
        var (kernel, _) = SimpleGoal("busy persistence clears");
        var driver = MakeDriver();
        var attempts = 0;

        Assert.Throws<InvalidOperationException>(() => new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 1,
            persistGoalTick: (_, _) =>
            {
                attempts++;
                throw SqliteBusy();
            },
            busyWriteDelay: _ => { }));

        Assert.Equal(1, attempts);
    }

    [Xunit.Fact(DisplayName = "PersistGoalTick_FiresOneBatchForGoalsThatChangedDisposition")]
    public void PersistGoalTick_FiresOneBatchForGoalsThatChangedDisposition()
    {
        // Two goals: A advances (workspace creation), B is held by concurrent cap.
        // persistGoalTick must be called once with A and without B.
        var kernel = new AgentOrchestratorKernel();
        var goalA = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "goal A");
        var goalB = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "goal B");

        var createWorkspaceCalls = 0;
        var driver = MakeDriver(
            getFacts: g => g.Id == goalA.Id
                ? GoalLifecycleFacts.None          // A: no workspace yet → workspace creation tick
                : new GoalLifecycleFacts(WorkspaceExists: true),
            // After A's workspace is created the running count hits cap, so B stays held.
            getRunningCount: () => createWorkspaceCalls >= 1
                ? ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers
                : 0,
            createWorkspace: _ => { createWorkspaceCalls++; return "/tmp/ws"; });

        var persistedGoalBatches = new List<IReadOnlyCollection<GoalId>>();

        new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 1,
            persistGoalTick: (_, changedGoalIds) => persistedGoalBatches.Add(changedGoalIds.ToArray()));

        // A changed disposition (workspace created); B started a durable hold episode.
        var persistedGoalIds = persistedGoalBatches.First();
        Assert.Contains(goalA.Id, persistedGoalIds);
        Assert.Contains(goalB.Id, persistedGoalIds);
        Assert.Equal(2, persistedGoalIds.Count);
    }

    private static AgentOrchestratorKernel WithGoalStatus(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        GoalStatus status)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(goal => goal.Id == goalId.Value ? goal with { Status = status } : goal)
                .ToArray()
        });
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void GoalStall_RestartAfterThreshold_EmitsOnce()
    {
        var root = CreateTempDirectory("mcg-goal-stall-restart");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var now = new DateTimeOffset(2026, 8, 3, 1, 0, 0, TimeSpan.Zero);
        var writer = new ConductEventLogWriter(logPath, utcNow: () => now);
        var (kernel, goal) = SimpleGoal("persist a held goal across loop generations");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        new ConductorBatchLoop(conductEventLogWriter: writer, utcNow: () => now).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            goalStallThreshold: TimeSpan.FromMinutes(10));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        now = now.AddMinutes(11);
        new ConductorBatchLoop(conductEventLogWriter: writer, utcNow: () => now).Run(
            restored,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            goalStallThreshold: TimeSpan.FromMinutes(10));

        now = now.AddMinutes(1);
        new ConductorBatchLoop(conductEventLogWriter: writer, utcNow: () => now).Run(
            restored,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            goalStallThreshold: TimeSpan.FromMinutes(10));

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(record => record.EventKind == "goal-stalled")
            .ToArray();
        var stalled = Assert.Single(records);
        Assert.Equal(goal.Id.Value[..8], stalled.GoalId);
        Assert.Contains("repeatedForSeconds=660", stalled.Detail, StringComparison.Ordinal);
        Assert.NotNull(restored.GetGoal(goal.Id).CurrentHold?.StalledAt);
    }

    [Xunit.Fact]
    public void GoalStall_EventStreamFailure_DoesNotStopLoop()
    {
        var root = CreateTempDirectory("mcg-goal-stall-event-failure");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var now = new DateTimeOffset(2026, 8, 3, 1, 30, 0, TimeSpan.Zero);
        var writer = new ConductEventLogWriter(
            logPath,
            utcNow: () => now,
            beforeRequiredEventDrain: () => throw new InvalidOperationException("event stream unavailable"));
        var (kernel, goal) = SimpleGoal("keep conducting when the stall event stream fails");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var summary = new ConductorBatchLoop(
            conductEventLogWriter: writer,
            utcNow: () => now).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ =>
                {
                    now = now.AddMinutes(11);
                    return false;
                },
                goalStallThreshold: TimeSpan.FromMinutes(10));

        Assert.Equal(2, summary.Ticks);
        Assert.NotNull(goal.CurrentHold?.StalledAt);
    }

    [Xunit.Fact]
    public void GoalStall_DependencyStateReadFailure_DoesNotStopLoop()
    {
        var dependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "hold through a state read failure");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [dependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(dependencyId, GoalStatus.Active.ToString())
        ]);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(getFacts: _ => throw SqliteBusy()),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        var hold = Assert.IsType<GoalHoldState>(kernel.GetGoal(active.Id).CurrentHold);
        Assert.Equal(1, summary.Ticks);
        Assert.Equal(1, summary.Held);
        Assert.Equal("LifecycleState=unknown", hold.State);
        Assert.Contains("waiting on dependency", hold.Blocker, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void GoalStall_InvalidatedAttemptStateReadFailure_DoesNotStopLoop()
    {
        var root = CreateTempDirectory("mcg-goal-stall-invalidated-state-read");
        var (kernel, goal) = SimpleGoal("hold invalidated acceptance through a state read failure");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(root, "attempts"),
            isProcessAlive: _ => true,
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7301));
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/Retry.cs"],
            "branch-a",
            "main-a");

        try
        {
            var started = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            Assert.True(coordinator.InvalidateCurrent(goal.Id.Value, "retry invalidated attempt"));

            var summary = new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(
                    getFacts: _ => throw SqliteBusy(),
                    parallelAcceptanceAttemptCoordinator: coordinator),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            var hold = Assert.IsType<GoalHoldState>(goal.CurrentHold);
            Assert.Equal(1, summary.Ticks);
            Assert.Equal(1, summary.Held);
            Assert.Equal("LifecycleState=unknown", hold.State);
            Assert.Contains("invalidated acceptance attempt", hold.Blocker, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void GoalStall_ChangedBlocker_DoesNotEmit()
    {
        var root = CreateTempDirectory("mcg-goal-stall-change");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var now = new DateTimeOffset(2026, 8, 3, 2, 0, 0, TimeSpan.Zero);
        var reason = "first empty batch";
        var (kernel, _) = SimpleGoal("change the blocker before the threshold");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => DispatchStartOutcome.EmptyBatch(reason));

        new ConductorBatchLoop(
            conductEventLogWriter: new ConductEventLogWriter(logPath, utcNow: () => now),
            utcNow: () => now).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ =>
                {
                    now = now.AddMinutes(11);
                    reason = "second empty batch";
                    return false;
                },
                goalStallThreshold: TimeSpan.FromMinutes(10));

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        Assert.DoesNotContain(records, record => record.EventKind == "goal-stalled");
        Assert.Contains("second empty batch", kernel.Goals.Single().CurrentHold?.Blocker, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void GoalStall_LiveWorkerPastThreshold_DoesNotEmit()
    {
        var root = CreateTempDirectory("mcg-goal-stall-live-worker");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var now = new DateTimeOffset(2026, 8, 3, 3, 0, 0, TimeSpan.Zero);
        var (kernel, goal) = SimpleGoal("keep a healthy worker running");
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", root, now));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(
                1234,
                "test.exe",
                root,
                Path.Combine(root, "worker.out.log"),
                Path.Combine(root, "worker.err.log"),
                Path.Combine(root, "worker.exit.txt"),
                now,
                null,
                null,
                OwnedProcessIds: [1234]));

        new ConductorBatchLoop(
            conductEventLogWriter: new ConductEventLogWriter(logPath, utcNow: () => now),
            utcNow: () => now).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ =>
            {
                now = now.AddMinutes(11);
                return false;
            },
            goalStallThreshold: TimeSpan.FromMinutes(10));

        Assert.Null(goal.CurrentHold);
        Assert.DoesNotContain(
            File.ReadAllLines(logPath).Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!),
            record => record.EventKind == "goal-stalled");
    }

    [Xunit.Fact]
    public void GoalStall_VerifyingGatePastThreshold_DoesNotEmit()
    {
        var root = CreateTempDirectory("mcg-goal-stall-verifying");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var now = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        var (originalKernel, goal) = SimpleGoal("keep a healthy acceptance gate running");
        var kernel = WithGoalStatus(originalKernel, goal.Id, GoalStatus.Verifying);

        new ConductorBatchLoop(
            conductEventLogWriter: new ConductEventLogWriter(logPath, utcNow: () => now),
            utcNow: () => now).Run(
                kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ =>
                {
                    now = now.AddMinutes(11);
                    return false;
                },
                goalStallThreshold: TimeSpan.FromMinutes(10));

        Assert.Null(kernel.GetGoal(goal.Id).CurrentHold);
        Assert.DoesNotContain(
            File.ReadAllLines(logPath).Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!),
            record => record.EventKind == "goal-stalled");
    }

    [Xunit.Fact]
    public void EmptyLoop_RecordsDurableStartAndTerminalStop()
    {
        var events = new List<RunEventAppend>();
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var recorder = new ConductorLifecycleRecorder(
            new DelegateRunEventStore(evt => events.Add(evt)),
            () => now,
            () => "empty-loop");

        var summary = new ConductorBatchLoop(
            utcNow: () => now,
            lifecycleRecorder: recorder).Run(
                new AgentOrchestratorKernel(),
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath());

        Assert.Equal("all-terminal", summary.StopReason);
        Assert.Equal(["start", "stop"], events.Select(evt => evt.Operation));
        Assert.Equal("all-terminal", events[1].Status);
    }

    private sealed class DelegateRunEventStore(Action<RunEventAppend> append) : IRunEventStore
    {
        public Task<RunEventRecord> AppendAsync(
            RunEventAppend evt,
            CancellationToken cancellationToken = default)
        {
            append(evt);
            return Task.FromResult(new RunEventRecord(
                1,
                Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue,
                evt.EventType,
                evt.GoalId,
                evt.Operation,
                evt.Status,
                evt.Detail,
                evt.PayloadJson));
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(
            long afterSequence = 0,
            string? goalId = null,
            int maxCount = 500,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopBeginTickTests
{
    [Xunit.Fact(DisplayName = "BatchLoop_rearms_dispatch_remediation_before_goal_walk")]
    public void BatchLoopRearmsDispatchRemediationBeforeGoalWalk()
    {
        var preTickKernel = new AgentOrchestratorKernel();
        var preTickGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            preTickKernel,
            AgentCatalog.Default().Agents,
            "Pre-tick failure");
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Loop failure");
        var shutdownCalls = 0;
        var driver = new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => "/tmp/workspace",
            dispatchAndStart: _ => DispatchStartOutcome.SpawnFailed("spawn failed"),
            startRecordedDispatches: null,
            buildServerShutdown: () => shutdownCalls++,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordTaskNote: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                "goal/test",
                "OK",
                [],
                null),
            land: (goal, _) => new LandingResult(
                goal.Id.Value,
                goal.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "Landed"),
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null),
            writeEscalation: (_, _, _) => { },
            classifyChangeRisk: _ => null);

        driver.AdvanceOnce(preTickGoal, ConductorAutonomyPolicy.Conservative);
        Xunit.Assert.Equal(1, shutdownCalls);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            Path.Combine(Path.GetTempPath(), $"conduct-stop-{Guid.NewGuid():N}.txt"),
            maxIterations: 1);

        Xunit.Assert.Equal(1, summary.Ticks);
        Xunit.Assert.Equal(2, shutdownCalls);
    }
}
