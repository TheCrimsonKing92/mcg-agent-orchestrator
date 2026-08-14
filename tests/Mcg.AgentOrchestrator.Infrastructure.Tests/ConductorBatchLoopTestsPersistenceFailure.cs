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
public sealed class ConductorBatchLoopTestsPersistenceFailure : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsPersistenceFailure(ITestOutputHelper output)
        : base(output)
    {
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Reviewer) ReviewerIdentityMovedWithoutTouchProof()
    {
        const string workingDirectory = "C:\\tmp";
        const string diagnostic =
            "Round-diff touch proof unavailable because the carried finding round has no reviewed-commit baseline.";
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review implementation", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Escalate an unclassifiable review finding", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        var prior = new ReviewFinding(
            "F-IDENTITY",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/A.cs", "A.Run", "guard"),
            "Blocking guard is missing.",
            FindingSeverity.Blocking);
        kernel.RecordTaskDispatch(
            goal.Id,
            reviewer.Id,
            new TaskDispatchRecord("reviewer", "review-seed", workingDirectory, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-seed",
            workingDirectory,
            0,
            StructuredReviewerResult(prior, "needs-work"),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        kernel.RetryTask(goal.Id, reviewer.Id, "fresh review");
        var moved = prior with { Location = new ReviewFindingLocation("src/B.cs", "B.Run", "guard") };
        kernel.RecordTaskDispatch(
            goal.Id,
            reviewer.Id,
            new TaskDispatchRecord(
                "reviewer",
                "review-moved",
                workingDirectory,
                DateTimeOffset.UtcNow,
                ReviewFindingTouchedAnchors: [],
                ReviewFindingTouchProofDiagnostic: diagnostic));
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-moved",
            workingDirectory,
            0,
            StructuredReviewerResult(moved, "needs-work"),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        return (kernel, goal, reviewer);
    }

    private static SqliteException TypedSqliteBusy(int sqliteErrorCode = 5) =>
        new($"SQLite Error {sqliteErrorCode}: 'database is locked'.", sqliteErrorCode);

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
        var ex = Assert.Throws<DispatchRecordWriteException>(() =>
            ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                (_, _) =>
                {
                    attempts++;
                    throw TypedSqliteBusy();
                },
                kernel,
                goalId,
                task.Id));

        var reloaded = await repo.LoadAsync();
        var reloadedTask = reloaded.GetTask(goalId, taskId);
        Assert.Contains("DISPATCH_RECORD_WRITE_CONTENTION", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ConductorBatchLoop.DefaultMaxBusyWriteAttempts, attempts);
        Assert.True(reloadedTask.LastDispatch is null);
        Assert.True(reloadedTask.LastProcess is null);
    }

    [Xunit.Theory(DisplayName = "DispatchRecordWrite_classifier_maps_only_busy_and_locked_to_contention")]
    [Xunit.InlineData(5, (int)DispatchRecordWriteFailureCause.Contention)]
    [Xunit.InlineData(6, (int)DispatchRecordWriteFailureCause.Contention)]
    public void DispatchRecordWriteClassifierMapsOnlyBusyAndLockedToContention(
        int sqliteErrorCode,
        int expectedCause)
    {
        Assert.Equal(
            (DispatchRecordWriteFailureCause)expectedCause,
            DispatchRecordWriteException.ClassifySqliteErrorCode(sqliteErrorCode));
    }

    [Xunit.Fact(DisplayName = "DispatchRecordWrite_classifier_maps_all_other_primary_and_unknown_codes_to_unrecoverable")]
    public void DispatchRecordWriteClassifierMapsAllOtherPrimaryAndUnknownCodesToUnrecoverable()
    {
        var otherCodes = Enumerable.Range(0, 29)
            .Concat([100, 101, 999])
            .Where(code => code is not 5 and not 6);

        Assert.All(otherCodes, code => Assert.Equal(
            DispatchRecordWriteFailureCause.Unrecoverable,
            DispatchRecordWriteException.ClassifySqliteErrorCode(code)));
    }

    [Xunit.Theory(DisplayName = "BatchLoop_dispatch_record_contention_skips_then_dispatches_on_next_tick")]
    [Xunit.InlineData(5)]
    [Xunit.InlineData(6)]
    public void BatchLoopDispatchRecordContentionSkipsThenDispatchesOnNextTick(int sqliteErrorCode)
    {
        var (kernel, goal) = SimpleGoal("dispatch record contention recovers");
        var before = JsonSerializer.Serialize(kernel.ExportSnapshot().Goals.Single());
        var persistAttempts = 0;
        var spawns = 0;
        var observedTicks = new List<(int Tick, int Spawns, string Snapshot)>();
        var tickSummaries = new List<BatchTickSummary>();
        ConductorDriver? driver = null;
        driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: currentGoal =>
            {
                var task = currentGoal.Tasks.Single();
                ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                    (_, _) =>
                    {
                        persistAttempts++;
                        if (persistAttempts is 1 or 2)
                            throw TypedSqliteBusy(sqliteErrorCode);
                    },
                    kernel,
                    currentGoal.Id,
                    task.Id);
                driver!.DispatchRecordWriteSucceededSink?.Invoke(currentGoal.Id);
                spawns++;
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            onTick: tick =>
            {
                tickSummaries.Add(tick);
                observedTicks.Add((
                    tick.Tick,
                    spawns,
                    JsonSerializer.Serialize(kernel.ExportSnapshot().Goals.Single())));
            });

        Assert.Equal(3, summary.Ticks);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(1, spawns);
        Assert.Equal([1, 2, 3], observedTicks.Select(item => item.Tick).ToArray());
        Assert.Equal(0, observedTicks[0].Spawns);
        Assert.Equal(before, observedTicks[0].Snapshot);
        Assert.Equal(GoalStatus.Active, kernel.GetGoal(goal.Id).Status);
        var contentionLines = tickSummaries
            .SelectMany(tick => tick.ProgressLines ?? [])
            .Where(line => line.StartsWith("DISPATCH_RECORD_WRITE_CONTENTION ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, contentionLines.Length);
        Assert.Contains("skip=1/5", contentionLines[0], StringComparison.Ordinal);
        Assert.Contains("skip=2/5", contentionLines[1], StringComparison.Ordinal);
        Assert.All(contentionLines, line => Assert.DoesNotContain("DISPATCH_RECORD_WRITE_FAILED", line, StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_dispatch_record_contention_message_with_fatal_token_does_not_stop_loop")]
    public void BatchLoopDispatchRecordContentionMessageWithFatalTokenDoesNotStopLoop()
    {
        var (kernel, goal) = SimpleGoal("typed cause outranks message");
        var task = goal.Tasks.Single();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => throw new DispatchRecordWriteException(
                DispatchRecordWriteFailureCause.Contention,
                DispatchRecordCheckpointPhase.BeforeProcessStart,
                "dispatch-start",
                goal.Id,
                task.Id,
                5,
                TypedSqliteBusy(),
                "quoted diagnostic DISPATCH_RECORD_WRITE_FAILED must not control flow"));

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, summary.Ticks);
        Assert.Equal(0, summary.Escalated);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_unclassified_pre_process_dispatch_record_write_is_visible_and_skipped")]
    public void BatchLoopUnclassifiedPreProcessDispatchRecordWriteIsVisibleAndSkipped()
    {
        var (kernel, goal) = SimpleGoal("unclassified dispatch record failure");
        var task = goal.Tasks.Single();
        var ticks = new List<BatchTickSummary>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => throw DispatchRecordWriteException.From(
                new InvalidOperationException("opaque persistence adapter failure"),
                DispatchRecordCheckpointPhase.BeforeProcessStart,
                "dispatch-start",
                goal.Id,
                task.Id));

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: ConductorBatchLoop.DispatchRecordContentionSkipLimit,
            onTick: ticks.Add);

        Assert.Equal(1, summary.Escalated);
        var lines = ticks.SelectMany(tick => tick.ProgressLines ?? [])
            .Where(progress => progress.StartsWith("DISPATCH_RECORD_WRITE_UNCLASSIFIED ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(ConductorBatchLoop.DispatchRecordContentionSkipLimit, lines.Length);
        Assert.All(lines, line => Assert.Contains("System.InvalidOperationException", line, StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Contains("opaque_persistence_adapter_failure", line, StringComparison.Ordinal));
        Assert.Contains("skip=5/5", lines[^1], StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_dispatch_record_contention_escalates_goal_at_visible_bound_without_stopping_loop")]
    public void BatchLoopDispatchRecordContentionEscalatesGoalAtVisibleBoundWithoutStoppingLoop()
    {
        var (kernel, goal) = SimpleGoal("bounded dispatch record contention");
        var task = goal.Tasks.Single();
        var ticks = new List<BatchTickSummary>();
        var attempts = 0;
        var escalationPersistAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                attempts++;
                throw new DispatchRecordWriteException(
                    DispatchRecordWriteFailureCause.Contention,
                    DispatchRecordCheckpointPhase.BeforeProcessStart,
                    "dispatch-start",
                    goal.Id,
                    task.Id,
                    5,
                    TypedSqliteBusy());
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: ConductorBatchLoop.DispatchRecordContentionSkipLimit,
            onTick: ticks.Add,
            persistGoalTick: (_, _) =>
            {
                escalationPersistAttempts++;
                throw TypedSqliteBusy();
            },
            busyWriteDelay: _ => { });

        Assert.Equal(ConductorBatchLoop.DispatchRecordContentionSkipLimit, attempts);
        Assert.True(escalationPersistAttempts > 0);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(GoalStatus.Active, kernel.GetGoal(goal.Id).Status);
        var contentionLines = ticks.SelectMany(tick => tick.ProgressLines ?? [])
            .Where(line => line.StartsWith("DISPATCH_RECORD_WRITE_CONTENTION ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(ConductorBatchLoop.DispatchRecordContentionSkipLimit, contentionLines.Length);
        Assert.Contains("skip=5/5", contentionLines[^1], StringComparison.Ordinal);
        Assert.Contains(ticks.SelectMany(tick => tick.ProgressLines ?? []), line =>
            line.Contains("dispatch-record-write-contention-limit", StringComparison.Ordinal));
        Assert.Contains(ticks.SelectMany(tick => tick.ProgressLines ?? []), line =>
            line.StartsWith("DISPATCH_RECORD_ESCALATION_PERSIST_DEFERRED ", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_unrecoverable_pre_process_dispatch_record_write_stops_loop")]
    public void BatchLoopUnrecoverablePreProcessDispatchRecordWriteStopsLoop()
    {
        var (kernel, goal) = SimpleGoal("unrecoverable dispatch record failure");
        var task = goal.Tasks.Single();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                    (_, _) => throw new SqliteException("readonly", 8),
                    kernel,
                    goal.Id,
                    task.Id);
                return DispatchStartOutcome.Started();
            });

        var ex = Assert.Throws<DispatchRecordWriteException>(() =>
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: ConductorBatchLoop.DispatchRecordContentionSkipLimit + 1));

        Assert.Equal(DispatchRecordWriteFailureCause.Unrecoverable, ex.Cause);
        Assert.True(ex.IsFatal);
        Assert.Contains("DISPATCH_RECORD_WRITE_FAILED", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(kernel.GetGoal(goal.Id).Timeline, entry =>
            entry.Message.Contains("dispatch-record-write-unrecoverable-limit", StringComparison.Ordinal));
    }

    [Xunit.Theory(DisplayName = "DispatchRecordWrite_post_process_failure_is_fatal_regardless_of_cause")]
    [Xunit.InlineData(5, (int)DispatchRecordWriteFailureCause.Contention)]
    [Xunit.InlineData(8, (int)DispatchRecordWriteFailureCause.Unrecoverable)]
    public void DispatchRecordWritePostProcessFailureIsFatalRegardlessOfCause(
        int sqliteErrorCode,
        int expectedCause)
    {
        var (kernel, goal) = SimpleGoal("post process contention");
        var task = goal.Tasks.Single();

        var ex = Assert.Throws<DispatchRecordWriteException>(() =>
            ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                (_, _) => throw new SqliteException("injected post-process write failure", sqliteErrorCode),
                kernel,
                goal.Id,
                task.Id,
                DispatchRecordCheckpointPhase.ProcessMayHaveStarted));

        Assert.Equal((DispatchRecordWriteFailureCause)expectedCause, ex.Cause);
        Assert.True(ex.ProcessMayHaveStarted);
        Assert.True(ex.IsFatal);
        Assert.Contains("DISPATCH_RECORD_WRITE_FAILED", ex.Message, StringComparison.Ordinal);
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

    [Xunit.Theory(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(5)]
    [Xunit.InlineData(6)]
    public void TransientSqliteCheckpoint_HeldGoalRecoversWithoutRepeatingSideEffect(
        int sqliteErrorCode)
    {
        var kernel = new AgentOrchestratorKernel();
        var goalA = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "held goal");
        var goalB = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "unrelated goal");
        var workspaceExists = new HashSet<string>(StringComparer.Ordinal);
        var workspaceCreates = new Dictionary<string, int>(StringComparer.Ordinal);
        var checkpointCalls = new List<string[]>();
        var ticks = new List<BatchTickSummary>();
        var heldAttempts = 0;
        var sweepHeldGoalIds = new List<string[]>();
        var databasePath = "C:/fixture/a-deliberately-long-database-directory/state.db";
        var operation = $"loop:tick/TransactGoalStateAsync({goalA.Id.Value})";
        var leaseDirectory = Path.Combine(Path.GetTempPath(), $"mcg-checkpoint-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(leaseDirectory);
        using var lease = ConductorLoopLeaseController.Acquire(leaseDirectory);
        var driver = MakeDriver(
            getFacts: goal => new GoalLifecycleFacts(WorkspaceExists: workspaceExists.Contains(goal.Id.Value)),
            createWorkspace: goal =>
            {
                workspaceExists.Add(goal.Id.Value);
                workspaceCreates[goal.Id.Value] = workspaceCreates.GetValueOrDefault(goal.Id.Value) + 1;
                return $"/tmp/{goal.Id.Value}";
            });

        IReadOnlyList<GoalSnapshotCheckpointResult> Checkpoint(
            AgentOrchestratorKernel _,
            IReadOnlyCollection<GoalId> requested)
        {
            Assert.True(lease.IsHeld);
            checkpointCalls.Add(requested.Select(goalId => goalId.Value).ToArray());
            var holdA = requested.Any(goalId => goalId == goalA.Id) && ++heldAttempts <= 2;
            return requested.Select(goalId => holdA && goalId == goalA.Id
                ? new GoalSnapshotCheckpointResult(
                    goalId.Value,
                    GoalSnapshotCheckpointDisposition.Held,
                    null,
                    "state",
                    databasePath,
                    operation,
                    sqliteErrorCode,
                    sqliteErrorCode,
                    AttemptCount: 3,
                    ElapsedMilliseconds: 250)
                : new GoalSnapshotCheckpointResult(
                    goalId.Value,
                    GoalSnapshotCheckpointDisposition.Durable,
                    null,
                    "state",
                    databasePath,
                    operation))
                .ToArray();
        }

        var summary = new ConductorBatchLoop(
            measuredSweepWithCheckpointHolds: (_, heldGoalIds) =>
            {
                sweepHeldGoalIds.Add(heldGoalIds.ToArray());
                return new TerminalGoalSweepResult([]);
            }).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            onTick: ticks.Add,
            sleepFunc: _ => false,
            checkpointGoalTick: Checkpoint);

        Assert.Equal(3, summary.Ticks);
        Assert.True(lease.IsHeld);
        Assert.Equal(1, workspaceCreates[goalA.Id.Value]);
        Assert.Equal(1, workspaceCreates[goalB.Id.Value]);
        Assert.Contains(checkpointCalls, batch => batch.Contains(goalB.Id.Value));
        Assert.Contains(checkpointCalls, batch => batch.Length == 1 && batch[0] == goalA.Id.Value);
        Assert.Contains(sweepHeldGoalIds, heldGoalIds => heldGoalIds.Contains(goalA.Id.Value));
        var lines = ticks.SelectMany(tick => tick.ProgressLines ?? []).ToArray();
        var hold = Assert.Single(lines, line => line.StartsWith("TICK_CHECKPOINT_HOLD ", StringComparison.Ordinal));
        Assert.Contains($"sqlite_code={sqliteErrorCode}", hold, StringComparison.Ordinal);
        Assert.Contains("store=state", hold, StringComparison.Ordinal);
        Assert.Contains($"database={databasePath}", hold, StringComparison.Ordinal);
        Assert.Contains($"operation={operation}", hold, StringComparison.Ordinal);
        Assert.Contains("attempt=3", hold, StringComparison.Ordinal);
        Assert.Contains("elapsed_ms=250", hold, StringComparison.Ordinal);
        Assert.Contains("disposition=exhausted-held", hold, StringComparison.Ordinal);
        Assert.Contains("holder=unknown", hold, StringComparison.Ordinal);
        var recovered = Assert.Single(lines, line => line.StartsWith("TICK_CHECKPOINT_RECOVERED ", StringComparison.Ordinal));
        Assert.Contains($"database={databasePath}", recovered, StringComparison.Ordinal);
        Assert.Contains($"operation={operation}", recovered, StringComparison.Ordinal);
        Assert.Contains($"sqlite_code={sqliteErrorCode}", recovered, StringComparison.Ordinal);
        Assert.Contains($"sqlite_extended_code={sqliteErrorCode}", recovered, StringComparison.Ordinal);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void TransientSqliteCheckpoint_ControlledWriterKeepsLeaseAcrossTicks()
    {
        var root = CreateTempDirectory("mcg-checkpoint-controlled-writer");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        long elapsedMilliseconds = 0;
        var retryDelays = 0;
        var holderReleased = false;
        using var holder = StateDbConnectionFactory.Open(workspace.SqliteStatePath, StateDbConnectionProfile.ReadWrite);
        var repository = new SqliteOrchestratorStateRepository(
            workspace.SqliteStatePath,
            statementObserver: null,
            new SqliteWriteTelemetryOptions
            {
                BusyTimeoutMilliseconds = 1,
                BusyRetryBudget = TimeSpan.FromMilliseconds(20),
                MaxBusyRetries = 2,
                MirrorToConductEventStream = false,
                MonotonicMilliseconds = () => elapsedMilliseconds,
                RetryDelay = (_, delay, _) =>
                {
                    elapsedMilliseconds += Math.Max(1, (long)Math.Ceiling(delay.TotalMilliseconds));
                    retryDelays++;
                    if (retryDelays == 2)
                    {
                        using var rollback = holder.CreateCommand();
                        rollback.CommandText = "ROLLBACK";
                        rollback.ExecuteNonQuery();
                        holderReleased = true;
                    }
                    return Task.CompletedTask;
                }
            });
        var kernel = new AgentOrchestratorKernel();
        var goalA = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "real writer held goal");
        var goalB = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "real writer unrelated goal");
        repository.SaveAsync(kernel).GetAwaiter().GetResult();
        var baselines = kernel.ExportSnapshot().Goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
        var workspaceExists = new HashSet<string>(StringComparer.Ordinal);
        var workspaceCreates = new Dictionary<string, int>(StringComparer.Ordinal);
        var checkpointBatches = new List<IReadOnlyList<GoalSnapshotCheckpointResult>>();
        var ticks = new List<BatchTickSummary>();
        using var lease = ConductorLoopLeaseController.Acquire(workspace.OrchestratorDirectory);
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            begin.ExecuteNonQuery();
        }
        var driver = MakeDriver(
            getFacts: goal => new GoalLifecycleFacts(WorkspaceExists: workspaceExists.Contains(goal.Id.Value)),
            createWorkspace: goal =>
            {
                workspaceExists.Add(goal.Id.Value);
                workspaceCreates[goal.Id.Value] = workspaceCreates.GetValueOrDefault(goal.Id.Value) + 1;
                return $"/tmp/{goal.Id.Value}";
            });

        IReadOnlyList<GoalSnapshotCheckpointResult> Checkpoint(
            AgentOrchestratorKernel currentKernel,
            IReadOnlyCollection<GoalId> requested)
        {
            Assert.True(lease.IsHeld);
            var current = currentKernel.ExportSnapshot().Goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
            IReadOnlyList<GoalSnapshotCheckpointResult> results;
            using (SqliteOrchestratorStateRepository.UseWriteOperationTag("loop:tick"))
            {
                results = repository.CheckpointGoalSnapshotsAsync(requested
                        .Select(goalId => new GoalSnapshotSaveRequest(baselines[goalId.Value], current[goalId.Value]))
                        .ToArray())
                    .GetAwaiter()
                    .GetResult();
            }
            checkpointBatches.Add(results);
            foreach (var result in results.Where(result => result.IsDurable && result.SaveResult?.PersistedSnapshot is not null))
                baselines[result.GoalId] = result.SaveResult!.PersistedSnapshot!;
            return results;
        }

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            onTick: ticks.Add,
            checkpointGoalTick: Checkpoint);

        Assert.Equal(2, summary.Ticks);
        Assert.True(lease.IsHeld);
        Assert.True(holderReleased);
        Assert.Equal(2, retryDelays);
        Assert.Equal(1, workspaceCreates[goalA.Id.Value]);
        Assert.Equal(1, workspaceCreates[goalB.Id.Value]);
        Assert.Contains(checkpointBatches[0], result => result.GoalId == goalA.Id.Value && !result.IsDurable && result.SqliteErrorCode == 5);
        Assert.Contains(checkpointBatches[0], result => result.GoalId == goalB.Id.Value && result.IsDurable);
        Assert.Contains(checkpointBatches.Skip(1).SelectMany(results => results), result => result.GoalId == goalA.Id.Value && result.IsDurable);
        var lines = ticks.SelectMany(tick => tick.ProgressLines ?? []).ToArray();
        Assert.Contains(lines, line => line.StartsWith("TICK_CHECKPOINT_HOLD ", StringComparison.Ordinal) &&
            line.Contains($"goal={goalA.Id.Value[..8]}", StringComparison.Ordinal) &&
            line.Contains("sqlite_code=5", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("TICK_CHECKPOINT_RECOVERED ", StringComparison.Ordinal) &&
            line.Contains($"goal={goalA.Id.Value[..8]}", StringComparison.Ordinal) &&
            line.Contains("sqlite_code=5", StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void TransientSqliteCheckpointNonTransientFailuresRemainFailClosed(bool sqliteCodeEight)
    {
        var (kernel, _) = SimpleGoal("permanent checkpoint failure");
        var exception = sqliteCodeEight
            ? (Exception)new SqliteException("readonly", 8)
            : new InvalidOperationException("non-SQLite persistence failure");

        var actual = Assert.Throws(exception.GetType(), () => new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            checkpointGoalTick: (_, _) => throw exception));

        Assert.Same(exception, actual);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void TransientSqliteCheckpoint_TerminalRecoveryEmitsBeforeExit()
    {
        var (kernel, goal) = SimpleGoal("terminal checkpoint recovery");
        var checkpointCalls = 0;
        BatchLoopSummary? summary = null;

        var output = AsyncLocalConsoleRouter.Capture(() =>
            summary = new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(createWorkspace: _ => "/tmp/terminal-recovery"),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                checkpointGoalTick: (checkpointKernel, requested) =>
                {
                    checkpointCalls++;
                    if (checkpointCalls == 1)
                    {
                        checkpointKernel.CancelGoal(goal.Id, "Terminal state awaits durability.");
                        return requested.Select(goalId => new GoalSnapshotCheckpointResult(
                            goalId.Value,
                            GoalSnapshotCheckpointDisposition.Held,
                            null,
                            "state",
                            "C:/fixture/state.db",
                            $"loop:tick/TransactGoalStateAsync({goalId.Value[..8]})",
                            SqliteErrorCode: 5,
                            SqliteExtendedErrorCode: 5,
                            AttemptCount: 3,
                            ElapsedMilliseconds: 250)).ToArray();
                    }

                    checkpointKernel.EvictTerminalGoalAggregates(requested);
                    return requested.Select(goalId => new GoalSnapshotCheckpointResult(
                        goalId.Value,
                        GoalSnapshotCheckpointDisposition.Durable,
                        null,
                        "state",
                        "C:/fixture/state.db",
                        $"loop:tick/TransactGoalStateAsync({goalId.Value[..8]})")).ToArray();
                }));

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.Ticks);
        Assert.Equal(2, checkpointCalls);
        Assert.Empty(kernel.Goals);
        Assert.Contains("TICK_CHECKPOINT_RECOVERED", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task TransientSqliteCheckpoint_HeldGoalDefersOperatorIntent()
    {
        var root = CreateTempDirectory("mcg-held-goal-intent");
        var (kernel, goal) = SimpleGoal("defer held goal intent");
        var task = goal.Tasks.Single();
        var store = new SqliteOperatorIntentStore(
            Path.Combine(root, "operator-intents.db"),
            Path.Combine(root, "logs"));
        var intent = new OperatorIntentRecord(
            "held-goal-intent",
            "held-goal-key",
            OperatorIntentVerbs.Retry,
            goal.Id.Value,
            task.Id.Value,
            JsonSerializer.Serialize(
                new RetryOperatorIntentPayload("defer until durable", null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            [],
            "operator",
            "test",
            "test",
            DateTimeOffset.UtcNow);
        var tickCount = 0;

        var exception = Record.Exception(() => new ConductorBatchLoop(
            operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(createWorkspace: _ => root),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                onTick: _ =>
                {
                    tickCount++;
                    if (tickCount == 1)
                        store.EnqueueAsync(intent).GetAwaiter().GetResult();
                },
                checkpointGoalTick: (_, requested) => requested.Select(goalId =>
                    new GoalSnapshotCheckpointResult(
                        goalId.Value,
                        GoalSnapshotCheckpointDisposition.Held,
                        null,
                        "state",
                        "C:/fixture/state.db",
                        $"loop:tick/TransactGoalStateAsync({goalId.Value[..8]})",
                        SqliteErrorCode: 5,
                        SqliteExtendedErrorCode: 5,
                        AttemptCount: 3,
                        ElapsedMilliseconds: 250)).ToArray()));

        Assert.Null(exception);
        var stored = await store.GetAsync(intent.Id);
        Assert.NotNull(stored);
        Assert.Equal(OperatorIntentStatus.Pending, stored!.Status);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void TransientSqliteCheckpoint_LoadHoldRechecksThroughLoop()
    {
        var loadHeld = true;
        var sweeps = 0;
        var sleeps = 0;

        var summary = new ConductorBatchLoop(
            measuredSweep: _ =>
            {
                sweeps++;
                if (sweeps == 2)
                    loadHeld = false;
                return new TerminalGoalSweepResult([]);
            }).Run(
                new AgentOrchestratorKernel(),
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                sleepFunc: _ =>
                {
                    sleeps++;
                    return false;
                },
                hasTransientLoadHold: () => loadHeld);

        Assert.Equal(2, sweeps);
        Assert.Equal(1, sleeps);
        Assert.Equal(0, summary.Ticks);
        Assert.Equal("all-terminal", summary.StopReason);
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

    [Xunit.Fact(DisplayName = "ConductorBatchLoop_identity_moved_without_touch_proof_emits_immediate_goal_escalation")]
    public void IdentityMovedWithoutTouchProofEmitsImmediateGoalEscalation()
    {
        var root = CreateTempDirectory("mcg-review-identity-escalation");
        var logPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
        var (kernel, goal, reviewer) = ReviewerIdentityMovedWithoutTouchProof();
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);

        new ConductorBatchLoop(
            conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                kernel,
                MakeDriver(getFacts: _ => GoalLifecycleFacts.None),
                ConductorAutonomyPolicy.Permissive,
                NoStopPath(),
                maxIterations: 1);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        var escalation = Assert.Single(records.Where(record => record.EventKind == "goal-escalation"));
        Assert.Equal(goal.Id.Value[..8], escalation.GoalId);
        Assert.Contains(reviewer.Id.Value[..8], escalation.Detail, StringComparison.Ordinal);
        Assert.Contains("ERR_REVIEW_FINDING_IDENTITY_MOVED", escalation.Detail, StringComparison.Ordinal);
        Assert.Contains("suppression=missing-system-derived-round-diff-proof", escalation.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(records, record => record.EventKind == "goal-stalled");
    }
}
