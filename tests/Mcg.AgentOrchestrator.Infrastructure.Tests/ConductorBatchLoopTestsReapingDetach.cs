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
public sealed class ConductorBatchLoopTestsReapingDetach : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsReapingDetach(ITestOutputHelper output)
        : base(output)
    {
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
            Assert.True(kernel.GetTask(watchedGoal.Id, watchedTask.Id).LastProcess!.WasGracefullyDetachedByConductor);
            Assert.False(kernel.GetTask(otherGoal.Id, otherTask.Id).LastProcess!.WasCancelled);
        }
        finally
        {
            File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_stop_retries_busy_checkpoint_until_detached_marker_is_durable")]
    public async Task BatchLoopStopRetriesBusyCheckpointUntilDetachedMarkerIsDurable()
    {
        var root = CreateTempDirectory("mcg-loop-stop-detach-busy-checkpoint");
        try
        {
            var repository = OpenStateRepository(Path.Combine(root, "state.db"));
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "durable detached marker");
            var task = goal.Tasks.Single();
            var now = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("test-worker", "worker.exe", root, now));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                new TaskProcessRecord(444, "worker.exe", root, "out.log", "err.log", "exit.txt",
                    now, null, null, OwnedProcessIds: [444]));
            await repository.SaveAsync(kernel);

            var attempts = 0;
            var runner = new BackgroundDispatchRunner();
            var stopFile = ExistingStopPath();
            try
            {
                var summary = new ConductorBatchLoop(
                    detachGoalRunningDispatches: (loopKernel, loopGoal) =>
                        runner.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id)).Run(
                        kernel,
                        MakeDriver(),
                        ConductorAutonomyPolicy.Conservative,
                        stopFile,
                        onlyGoalId: goal.Id.Value,
                        persistGoalTick: (checkpoint, changedGoalIds) =>
                        {
                            attempts++;
                            if (attempts == 1)
                            {
                                throw SqliteBusy();
                            }

                            var changed = changedGoalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
                            var snapshots = checkpoint.ExportSnapshot().Goals
                                .Where(snapshot => changed.Contains(snapshot.Id))
                                .ToArray();
                            repository.SaveGoalSnapshotsAsync(snapshots, CancellationToken.None).GetAwaiter().GetResult();
                        },
                        busyWriteDelay: _ => { });

                Assert.True(summary.StopRequested);
            }
            finally
            {
                File.Delete(stopFile);
            }

            var reloaded = await repository.LoadAsync();
            Assert.Equal(2, attempts);
            Assert.True(reloaded.GetTask(goal.Id, task.Id).LastProcess!.WasGracefullyDetachedByConductor);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void Stop_TenSimulatedBusyMinutes_HasBoundedOutput()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "bounded detached checkpoint failure");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UnixEpoch;
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("test-worker", "worker.exe", "C:\\goal", now));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(
                444,
                "worker.exe",
                "C:\\goal",
                "out.log",
                "err.log",
                "exit.txt",
                now,
                null,
                null,
                OwnedProcessIds: [444]));

        var attempts = 0;
        var delays = new List<TimeSpan>();
        var runner = new BackgroundDispatchRunner();
        var stopFile = ExistingStopPath();
        try
        {
            BatchLoopSummary? summary = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                summary = new ConductorBatchLoop(
                    detachGoalRunningDispatches: (loopKernel, loopGoal) =>
                        runner.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id),
                    utcNow: () => now,
                    writeJitter: () => 0).Run(
                        kernel,
                        MakeDriver(),
                        ConductorAutonomyPolicy.Conservative,
                        stopFile,
                        onlyGoalId: goal.Id.Value,
                        persistTick: _ =>
                        {
                            attempts++;
                            throw SqliteBusy();
                        },
                        busyWriteDelay: delay =>
                        {
                            delays.Add(delay);
                            now = now.AddMinutes(5);
                        }));

            Assert.NotNull(summary);
            Assert.True(summary!.StopRequested);
            var outputLines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var busyLines = outputLines
                .Where(line => line.StartsWith("TICK_WRITE_BUSY ", StringComparison.Ordinal))
                .ToArray();
            var degradedLines = outputLines
                .Where(line => line.StartsWith("TICK_WRITE_DEGRADED ", StringComparison.Ordinal))
                .ToArray();
            var retryingLines = outputLines
                .Where(line => line.StartsWith("TICK_WRITE_RETRYING ", StringComparison.Ordinal))
                .ToArray();
            Assert.Collection(
                busyLines,
                line => Assert.Contains(" attempt=1 ", line, StringComparison.Ordinal),
                line => Assert.Contains(" attempt=2 ", line, StringComparison.Ordinal),
                line => Assert.Contains(" attempt=3 ", line, StringComparison.Ordinal));
            Assert.Collection(
                degradedLines,
                line => Assert.Contains(" attempt=1 ", line, StringComparison.Ordinal),
                line => Assert.Contains(" attempt=2 ", line, StringComparison.Ordinal),
                line => Assert.Contains(" attempt=3 ", line, StringComparison.Ordinal));
            Assert.Collection(
                retryingLines,
                line => Assert.Contains(" attempt=1 ", line, StringComparison.Ordinal),
                line => Assert.Contains(" attempt=2 ", line, StringComparison.Ordinal));
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(output) < 1024 * 1024);
            Assert.Equal(TimeSpan.FromMinutes(10), now - DateTimeOffset.UnixEpoch);
        }
        finally
        {
            File.Delete(stopFile);
        }

        Assert.Equal(ConductorBatchLoop.DefaultGracefulDetachCheckpointAttempts, attempts);
        Assert.Equal(ConductorBatchLoop.DefaultGracefulDetachCheckpointAttempts - 1, delays.Count);
        Assert.All(delays, delay => Assert.True(delay > TimeSpan.Zero));
        Assert.True(delays[1] >= delays[0]);
        Assert.All(delays, delay => Assert.True(delay <= RetryLoopPolicy.MaximumWriteDelay));
        Assert.True(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasGracefullyDetachedByConductor);
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
        Assert.True(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasGracefullyDetachedByConductor);

        var detachedProcess = kernel.GetTask(goal.Id, task.Id).LastProcess!;
        Assert.True(detachedProcess.WasGracefullyDetachedByConductor);
        var failedAt = DateTimeOffset.UtcNow;
        var syntheticFailure = detachedProcess with
        {
            CompletedAt = failedAt,
            ExitCode = 1,
            ExitArtifactOrigin = DispatchExitArtifactOrigin.Synthetic,
            ExitArtifactReason = "successor synthesized completion without a host artifact"
        };
        kernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            syntheticFailure,
            new TaskVerificationRecord(
                syntheticFailure.Command,
                syntheticFailure.WorkingDirectory,
                1,
                string.Empty,
                "dispatch host disappeared before writing its exit artifact",
                failedAt));
        Assert.Equal(WorkTaskStatus.Failed, kernel.GetTask(goal.Id, task.Id).Status);

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
        Assert.True(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasGracefullyDetachedByConductor);
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
        Assert.True(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasGracefullyDetachedByConductor);
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
        Assert.True(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasGracefullyDetachedByConductor);

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
}
