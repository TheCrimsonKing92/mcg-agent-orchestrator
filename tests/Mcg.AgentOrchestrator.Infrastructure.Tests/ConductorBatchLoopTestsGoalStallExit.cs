using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsGoalStallExit : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsGoalStallExit(ITestOutputHelper output)
        : base(output)
    {
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
    public void GoalStall_FocusedEvidenceFirstAndFourthAttemptsHaveDistinctBlockerText()
    {
        var root = CreateTempDirectory("mcg-focused-evidence-stall-ordinal");
        var (seedKernel, goal) = SimpleGoal("distinguish focused evidence restart ordinals");

        try
        {
            ConductEventRecord RunUntilStalled(int ordinal)
            {
                var kernel = AgentOrchestratorKernel.FromSnapshot(seedKernel.ExportSnapshot());
                var now = new DateTimeOffset(2026, 8, 7, 14, 0, 0, TimeSpan.Zero);
                var logPath = Path.Combine(root, $"attempt-{ordinal}", ConductEventLogWriter.CurrentFileName);
                var attemptId = $"shared-{ordinal}";
                var reason = new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(root, $"attempt-{ordinal}", "attempts"))
                    .DescribeFocusedEvidenceHold(new ConductorParallelAcceptanceAttempt(
                        attemptId,
                        goal.Id.Value,
                        goal.Id.Value[..8],
                        0,
                        null,
                        null,
                        now,
                        now,
                        Environment.ProcessId,
                        ConductorParallelAcceptanceAttemptOutcome.Running,
                        Path.Combine(root, $"{attemptId}.out.log"),
                        Path.Combine(root, $"{attemptId}.err.log"),
                        Path.Combine(root, $"{attemptId}.exit.txt"),
                        Path.Combine(root, $"{attemptId}.heartbeat.json"),
                        Path.Combine(root, $"{attemptId}.result.json"),
                        Path.Combine(root, $"{attemptId}.attempt.json"),
                        Kind: ConductorParallelAcceptanceAttemptCoordinator.PreReviewEvidenceDispatchKind,
                        Ordinal: ordinal));

                new ConductorBatchLoop(
                    conductEventLogWriter: new ConductEventLogWriter(logPath, utcNow: () => now),
                    utcNow: () => now).Run(
                        kernel,
                        MakeDriver(
                            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                            dispatchAndStart: _ => DispatchStartOutcome.EmptyBatch(reason)),
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

                return Assert.Single(File.ReadAllLines(logPath)
                    .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                        line,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                    .Where(record => record.EventKind == "goal-stalled"));
            }

            var first = RunUntilStalled(1);
            var fourth = RunUntilStalled(4);

            Assert.Equal(goal.Id.Value[..8], first.GoalId);
            Assert.Equal(first.GoalId, fourth.GoalId);
            Assert.Contains("attempt_1", first.Detail, StringComparison.Ordinal);
            Assert.Contains("attempt_4", fourth.Detail, StringComparison.Ordinal);
            Assert.NotEqual(first.Detail, fourth.Detail);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
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
            new KeyValuePair<GoalId, string>(dependencyId, GoalStatus.Parked.ToString())
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
    public void UnintendedExit_RecordsLoopStopWithExceptionAndRethrowsOriginal()
    {
        var events = new List<RunEventAppend>();
        var recorder = new ConductorLifecycleRecorder(
            new DelegateRunEventStore(evt => events.Add(evt)),
            generationId: () => "crashing-loop");
        var (kernel, _) = SimpleGoal("crash after one completed tick");
        var expected = new InvalidOperationException("seeded loop crash\nwith a second line");
        Exception? actual = null;

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            actual = Assert.Throws<InvalidOperationException>(() =>
                new ConductorBatchLoop(lifecycleRecorder: recorder).Run(
                    kernel,
                    MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1,
                    onTick: _ => throw expected));
        });

        Assert.Same(expected, actual);
        Assert.Contains(
            "LOOP_STOP tick=1 rechecks=0 reason=unintended-exit exception=InvalidOperationException",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("second line", output, StringComparison.Ordinal);
        var stop = Assert.Single(events, evt => evt.Operation == "stop");
        Assert.Equal("unintended-exit", stop.Status);
        Assert.Contains("exception=InvalidOperationException", stop.Detail, StringComparison.Ordinal);
        Assert.Contains("message=seeded_loop_crash_with_a_second_line", stop.Detail, StringComparison.Ordinal);
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
