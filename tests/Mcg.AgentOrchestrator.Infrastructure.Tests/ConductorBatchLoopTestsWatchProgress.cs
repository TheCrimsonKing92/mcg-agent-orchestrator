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
public sealed class ConductorBatchLoopTestsWatchProgress : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsWatchProgress(ITestOutputHelper output)
        : base(output)
    {
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) SoftwareGoal(string objective = "Review retry goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(kernel, DefaultAgents(), objective);
        return (kernel, goal);
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
        IReadOnlyList<string> files,
        int loopProcessId = 9001,
        int launcherProcessId = 9002) =>
        new(
            readHeartbeat: (process, observedAt) => Heartbeat(process, observedAt, stdoutBytes, stderrBytes, idle, ownedPids),
            readChanges: (_, _) => new DispatchLiveChangeSnapshot(files, files.Take(3).ToArray(), Math.Max(0, files.Count - 3)),
            isProcessAlive: alivePids.Contains,
            now: () => now,
            loopProcessId: loopProcessId,
            launcherProcessId: launcherProcessId);

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
            StringComparison.Ordinal).Replace(
            WorkerDispatchTestSupport.PlannerContractAcceptanceMappingBody,
            "1. disposition=undecidable; would-settle=an historical diagnostic; required-source=the original process; unavailable-because=the process never recorded it",
            StringComparison.Ordinal);
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            plannerPlan,
            "WORKER_RESULT:",
            "files: none",
            "commands: none",
            "tests: not-run - planning only",
            "commit: none",
            "blockers: none",
            "model_fit: OpenAI/gpt-5.5 - adequate - planning", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
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
        Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
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
        Assert.Contains("state=working", line);
        Assert.Contains("pid=111", line);
        Assert.DoesNotContain("pid=222", line);
        Assert.Contains("liveness=\"alive\"", line);
        Assert.Contains("output_delta=50", line);
        Assert.Contains("last_progress_age=15s", line);
        Assert.Contains("files=4", line);
        Assert.Contains("src/A.cs", line);
        Assert.Contains("+1 more", line);

        var human = ticks.Single().ProgressLines!.Single(l => l.StartsWith($"[{goal.Id.Value[..8]}] {task.RequiredRole}", StringComparison.Ordinal));
        Assert.Contains("(task 1/", human);
        Assert.Contains("running 2m0s", human);
        Assert.Contains("state=working", human);
        Assert.Contains("worker pid=111 alive", human);
        Assert.DoesNotContain("pid=222", human);
        Assert.Contains("+42B stdout", human);
        Assert.Contains("last progress 15s ago", human);
        Assert.Contains("4 files changed (src/A.cs, src/B.cs, src/C.cs, +1 more)", human);
    }

    [Xunit.Theory(DisplayName = "WatchProgress_suppresses_conductor_process_ids")]
    [Xunit.InlineData(9001)]
    [Xunit.InlineData(9002)]
    public void WatchProgressSuppressesConductorProcessIds(int conductorProcessId)
    {
        var (kernel, goal) = SimpleGoal("watch pid guard");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-1), "abc123", conductorProcessId);
        var reporter = FakeWatchReporter(
            now,
            stdoutBytes: 10,
            stderrBytes: 0,
            idle: TimeSpan.FromSeconds(5),
            ownedPids: [333],
            alivePids: [333],
            files: []);

        var lines = reporter.BuildLines(
            kernel.GetGoal(goal.Id),
            quiet: false,
            policy: ConductorAutonomyPolicy.Conservative,
            watchInterval: TimeSpan.FromSeconds(1));

        var machine = lines.Single(line => line.StartsWith("WATCH_PROGRESS ", StringComparison.Ordinal));
        var human = lines.Single(line => line.StartsWith($"[{goal.Id.Value[..8]}]", StringComparison.Ordinal));
        Assert.Contains("pid=unknown", machine);
        Assert.Contains("worker pid=unknown alive", human);
        Assert.DoesNotContain($"pid={conductorProcessId}", machine);
        Assert.DoesNotContain($"pid={conductorProcessId}", human);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_distinguishes_working_quiet_and_stalled_state")]
    public void WatchProgressDistinguishesWorkingQuietAndStalledState()
    {
        var (kernel, goal) = SimpleGoal("watch progress state");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-12), "abc123");
        var currentGoal = kernel.GetGoal(goal.Id);
        var workingReporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(5), [111], [111], []);
        var quietReporter = FakeWatchReporter(now, 0, 0, TimeSpan.FromSeconds(5), [111], [111], []);
        var stalledReporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromMinutes(10), [111], [111], []);

        var working = workingReporter.BuildLines(
            currentGoal,
            quiet: false,
            policy: ConductorAutonomyPolicy.Conservative,
            stallThreshold: TimeSpan.FromMinutes(10));
        var quiet = quietReporter.BuildLines(
            currentGoal,
            quiet: false,
            policy: ConductorAutonomyPolicy.Conservative,
            stallThreshold: TimeSpan.FromMinutes(10));
        var stalled = stalledReporter.BuildLines(
            currentGoal,
            quiet: false,
            policy: ConductorAutonomyPolicy.Conservative,
            stallThreshold: TimeSpan.FromMinutes(10));

        Assert.All(working, line => Assert.Contains("state=working", line));
        Assert.All(quiet, line => Assert.Contains("state=quiet", line));
        Assert.All(stalled.Where(line => !line.Contains("WARNING", StringComparison.Ordinal)), line => Assert.Contains("state=stalled", line));
        Assert.NotEqual(working[0], stalled[0]);
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

    [Xunit.Fact(DisplayName = "BatchLoop_healthy_reviewer_bounce_emits_single_transition_without_escalation")]
    public void BatchLoopHealthyReviewerBounceEmitsSingleTransitionWithoutEscalation()
    {
        var root = CreateTempDirectory("mcg-reviewer-bounce-events");
        var logPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
        var (kernel, goal) = SoftwareGoal("healthy reviewer bounce event counts");
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var now = DateTimeOffset.Parse("2026-08-09T10:00:00Z");
        StartProcess(kernel, goal, reviewer, now.AddMinutes(-2), "review-candidate", processId: 111);
        var finding = new ReviewFinding(
            "AC3-HEALTHY-BOUNCE",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Test.cs", "Test.Run", "retry target"),
            "Developer must add the missing focused regression.",
            FindingSeverity.Blocking);
        var sweepCalls = 0;
        Action<AgentOrchestratorKernel> sweep = loopKernel =>
        {
            sweepCalls++;
            if (sweepCalls != 2)
            {
                return;
            }

            loopKernel.RecordDispatchResultCommit(goal.Id, reviewer.Id, "reviewed123456789");
            var reviewerProcess = loopKernel.GetTask(goal.Id, reviewer.Id).LastProcess!;
            loopKernel.RecordTaskProcessRefreshed(
                goal.Id,
                reviewer.Id,
                reviewerProcess with { CompletedAt = now, ExitCode = 1 },
                null);
            loopKernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
                reviewerProcess.Command,
                reviewerProcess.WorkingDirectory,
                1,
                StructuredReviewerResult(finding, "needs-work"),
                string.Empty,
                now,
                WorkerResultPresent: true));
        };
        var reporter = FakeWatchReporter(
            now,
            10,
            0,
            TimeSpan.FromSeconds(5),
            [111, 222],
            [111, 222],
            ["src/Test.cs"]);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                if (developer.Status == WorkTaskStatus.Assigned && developer.LastDispatch is null)
                {
                    StartProcess(kernel, goal, developer, now, "retry-candidate", processId: 222);
                }

                return DispatchStartOutcome.Started();
            },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message));

        new ConductorBatchLoop(
            sweep: sweep,
            watchProgressReporter: reporter,
            conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                NoStopPath(),
                maxIterations: 2,
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => false);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        var transition = Assert.Single(records.Where(record => record.EventKind == "watch-transition"));
        Assert.Equal(goal.Id.Value[..8], transition.GoalId);
        Assert.Contains("Reviewer=✓", transition.Detail, StringComparison.Ordinal);
        Assert.Contains("next=Developer", transition.Detail, StringComparison.Ordinal);
        Assert.Empty(records.Where(record => record.EventKind == "goal-escalation"));
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
}
