using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Unit tests for ConductorBatchLoop covering: loop scheduling (cap, hold/advance),
/// auto-retry-recover, auto-retry-escalate, and kill-switch.
/// END-marker parsing tests live in ChaosGateTests.
/// </summary>
public sealed class ConductorBatchLoopTests
{
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
        var dispatch = new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        var verification = new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", "", DateTimeOffset.UtcNow);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
    }

    private static ConductorDriver MakeDriver(
        Func<Goal, GoalLifecycleFacts>? getFacts = null,
        Func<int>? getRunningCount = null,
        Func<Goal, string>? createWorkspace = null,
        Func<Goal, bool>? dispatchAndStart = null,
        Func<Goal, bool>? runAcceptance = null,
        Func<Goal, LandingResult>? land = null,
        Action<Goal>? record = null,
        Action<Goal>? cleanup = null,
        Action<Goal, GoalLifecycleState, string>? writeEscalation = null,
        Func<Goal, ChangeRiskTier?>? classifyRisk = null) =>
        new ConductorDriver(
            getFacts ?? (_ => GoalLifecycleFacts.None),
            getRunningCount ?? (() => 0),
            createWorkspace ?? (_ => "/tmp/workspace"),
            dispatchAndStart ?? (_ => true),
            runAcceptance ?? (_ => true),
            land ?? (g => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed")),
            record ?? (_ => { }),
            cleanup ?? (_ => { }),
            writeEscalation ?? ((_, _, _) => { }),
            classifyRisk ?? (_ => null));

    // Returns a path to a stop file that does NOT exist yet.
    private static string NoStopPath() =>
        Path.Combine(Path.GetTempPath(), $"conduct-stop-{Guid.NewGuid():N}.txt");

    // Creates a stop file and returns its path.
    private static string ExistingStopPath()
    {
        var path = NoStopPath();
        File.WriteAllText(path, "stop");
        return path;
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
            dispatchAndStart: _ => { advanceCalls++; return true; });

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
            dispatchAndStart: _ => { dispatched++; return true; });

        var stopFile = NoStopPath();
        // Conservative cap = 2; tick 1: 2 dispatched, 1 held → tickAdvanced=2 → loop continues
        // Tick 2: running count = 2 = cap, all 3 held → loop terminates
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile);

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(2, summary.Advanced);
        Assert.Equal(2, dispatched); // exactly 2 dispatches across the whole run
    }

    // ── Auto-retry: transient acceptance flake recovers on retry ─────────

    [Xunit.Fact(DisplayName = "BatchLoop_AutoRetry_flakeOnFirstAttempt_recoverOnRetry")]
    public void BatchLoop_AutoRetry_FlakeOnFirstAttemptRecoverOnRetry()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single()); // goal → Completed → Verified state

        var attempts = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None, // IsMerged=false → Verified state
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
            getFacts: _ => GoalLifecycleFacts.None,
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
                return true;
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
            cleanup: _ => { });

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
                return true;
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
                return true;
            });

        var stopFile = NoStopPath();
        var sleepCallCount = 0;
        var allIntervalsCorrect = true;

        // Inject a sleep func that records calls and returns false (no stop).
        // Stop via stop file after 2nd sleep call.
        Func<TimeSpan, bool> fakeSleep = interval =>
        {
            sleepCallCount++;
            if (interval != TimeSpan.FromSeconds(15))
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
                return true;
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
                return true;
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

    // ── Terminal-goal skip: Cancelled/Superseded goals not in eligible set ─

    [Xunit.Fact(DisplayName = "BatchLoop_TerminalGoals_CancelledExcludedFromEligible")]
    public void BatchLoop_TerminalGoals_CancelledExcludedFromEligible()
    {
        var kernel = new AgentOrchestratorKernel();
        var cancelledGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "cancelled goal");
        var activeGoal    = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active goal");

        kernel.CancelGoal(cancelledGoal.Id, "test cancel");

        var advancedGoalIds = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                advancedGoalIds.Add(g.Id.Value);
                return true;
            });

        var stopFile = NoStopPath();
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 1);

        // Cancelled goal must not appear in the advanced set and must not generate escalations
        Assert.False(advancedGoalIds.Contains(cancelledGoal.Id.Value));
        Assert.Equal(0, summary.Escalated);
        // Active goal must have been advanced
        Assert.True(advancedGoalIds.Contains(activeGoal.Id.Value));
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
