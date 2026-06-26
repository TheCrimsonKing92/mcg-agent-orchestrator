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

    private static GoalWorktreeRebaseResult DefaultRebaseSuccess() =>
        new(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "OK", [], null);

    private static ConductorDriver MakeDriver(
        Func<Goal, GoalLifecycleFacts>? getFacts = null,
        Func<int>? getRunningCount = null,
        Func<Goal, string>? createWorkspace = null,
        Func<Goal, DispatchStartOutcome>? dispatchAndStart = null,
        Func<Goal, bool>? runAcceptance = null,
        Func<Goal, GoalWorktreeRebaseResult>? rebaseOntoMain = null,
        Func<Goal, LandingResult>? land = null,
        Action<Goal>? record = null,
        Action<Goal>? cleanup = null,
        Action<Goal, GoalLifecycleState, string>? writeEscalation = null,
        Func<Goal, ChangeRiskTier?>? classifyRisk = null,
        Func<GoalId, TaskId, string, TaskSpec>? retryTask = null) =>
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
            null,
            rebaseOntoMain ?? (_ => DefaultRebaseSuccess()),
            land is null
                ? ((g, _) => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"))
                : ((g, _) => land(g)),
            null,
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

    private static IReadOnlyList<AgentDefinition> BuildAgents(params AgentRole[] roles)
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
            completedGoalIds.Add(completed.Id);
        }

        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active conductor goal");
        var createdWorkspaces = new List<GoalId>();
        var driver = MakeDriver(
            getFacts: goal => completedGoalIds.Contains(goal.Id)
                ? new GoalLifecycleFacts(IsCleanedUp: true)
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
        var repo = new SqliteOrchestratorStateRepository(db);

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
        Assert.Single(ticks);
        Assert.Equal(1, ticks.Single().Done);
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
            new TaskVerificationRecord("dev.exe", "C:\\goal", 1, "", "", now));

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
                onlyGoalId: escalatedGoal.Id.Value);

        Assert.Equal(1, summary.Escalated);
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
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(444, "first.exe", "C:\\goal", "out.log", "err.log", "exit.txt",
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

    [Xunit.Fact(DisplayName = "BatchLoop_prior_cancelled_sdlc_task_is_requeued_before_ready_batch")]
    public void BatchLoopPriorCancelledSdlcTaskIsRequeuedBeforeReadyBatch()
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

        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var ready = g.Tasks.Single(t => t.Status == WorkTaskStatus.Assigned && t.RequiredRole == AgentRole.Developer);
                kernel.RecordTaskDispatch(g.Id, ready.Id,
                    new TaskDispatchRecord("test-worker", "dev-redo.exe", "C:\\goal", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, ready.Id,
                    new TaskProcessRecord(777, "dev-redo.exe", "C:\\goal", "out2.log", "err2.log", "exit2.txt",
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

        Assert.Equal(0, summary.Escalated);
        Assert.Equal(1, dispatches);
        var recoveredDeveloper = kernel.GetTask(goal.Id, developer.Id);
        Assert.Equal(WorkTaskStatus.Running, recoveredDeveloper.Status);
        Assert.Equal(777, recoveredDeveloper.LastProcess!.ProcessId);
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
        Assert.True(sweepCalls >= 2);
        Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.IsRunning);
        Assert.False(summary.StopRequested);
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

        public bool Wait(TimeSpan timeout)
        {
            Timeouts.Add(timeout);
            return _wait?.Invoke(++_waits) ?? false;
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
        Assert.True(ticks.Skip(1).All(t => t.ProgressLines is not null && t.ProgressLines.Count == 0));
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
        Assert.Contains("next=Developer", transition);
        Assert.True(ticks.SelectMany(t => t.ProgressLines ?? []).Any(l => l.Contains("role=Developer", StringComparison.Ordinal)));
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
                return DispatchStartOutcome.Started();
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

    // ── Per-tick write scope: persistGoalTick fires exactly for goals that changed ──

    [Xunit.Fact(DisplayName = "PersistGoalTick_FiresExactlyForGoalsThatChangedDisposition")]
    public void PersistGoalTick_FiresExactlyForGoalsThatChangedDisposition()
    {
        // Two goals: A advances (workspace creation), B is held by concurrent cap.
        // persistGoalTick must be called exactly once for A and zero times for B.
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

        var persistedGoalIds = new List<GoalId>();

        new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 1,
            persistGoalTick: (_, changedGoalId) => persistedGoalIds.Add(changedGoalId));

        // A changed disposition (workspace created); B was held with no state change.
        Assert.Contains(goalA.Id, persistedGoalIds);
        Assert.DoesNotContain(goalB.Id, persistedGoalIds);
        Assert.Single(persistedGoalIds);
    }
}
