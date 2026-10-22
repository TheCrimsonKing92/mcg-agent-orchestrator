using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repository, database scope, scripted processes, and a single MutableClock.
public sealed class ConductorGenerationInterruptionRedispatchScenarioTests : WorkerDispatchTestSupport
{
    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData("matching")]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("other-pid")]
    public async Task Refresh_MissingDirtyWorker_UsesOnlyMatchingGenerationProof(string mode)
    {
        var root = CreateSeededDispatchRepository();
        var end = ConductorGenerationTerminationRecorderTests.TerminatedAt;
        var ownerStart = ConductorGenerationTerminationRecorderTests.StartedAt;
        var clock = new MutableClock(end);
        var stateDirectory = Path.Combine(root, ".orchestrator");
        Directory.CreateDirectory(stateDirectory);
        var databasePath = Path.Combine(stateDirectory, "state.db");
        StateDbMigrations.EnsureUpToDate(databasePath);
        var registry = new SpawnRegistry(databasePath);
        // AsyncLocal scope exercises the actual default readers without changing the machine registry.
        using var registryScope = WorkerProcessJobs.UseRegistryScopeForTests(databasePath);
        var events = new SqliteRunEventStore(Path.Combine(stateDirectory, "run-events.db"));
        if (mode != "missing")
        {
            var result = await ConductorGenerationTerminationRecorderTests.RunRevertAsync(events, new ClockAdapter(clock),
                Path.Combine(stateDirectory, "supervisor"), pid: mode == "other-pid" ? 4243 : 4242, startedAt: ownerStart);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(3, result.Launches);
            var recorded = await events.ReadByTypeSinceAsync(RunEventTypes.ConductorSupervision, "generation-terminated");
            Assert.Single(recorded);
        }
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Recover generation-owned dirty worker");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        var dirtyFile = Path.Combine(worktree, "dirty.txt");
        File.WriteAllText(dirtyFile, "uncommitted work");
        var task = goal.Tasks.First(item => item.RequiredRole == AgentRole.Developer);
        var logs = Path.Combine(stateDirectory, "worker");
        Directory.CreateDirectory(logs);
        var dispatchedAt = end.AddMinutes(-3);
        const string command = "claude prompt interrupted";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new("claude-cli", command, worktree, dispatchedAt));
        var process = new TaskProcessRecord(999999, command, worktree, Path.Combine(logs, "out.log"),
            Path.Combine(logs, "err.log"), Path.Combine(logs, "exit.txt"), dispatchedAt, null, null);
        File.WriteAllText(process.StandardOutputPath, string.Empty);
        File.WriteAllText(process.StandardErrorPath, string.Empty);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        registry.Register($"{goal.Id.Value}:{task.Id.Value}", new(process.ProcessId, dispatchedAt, "worker.exe"),
            new(4242, ownerStart, "dotnet.exe"));
        using (var connection = StateDbConnectionFactory.Open(databasePath, StateDbConnectionProfile.ReadWrite))
        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE spawn_registry SET registered_at = $at";
            update.Parameters.AddWithValue("$at", dispatchedAt.ToString("O"));
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        Assert.Equal(4242, registry.TryReadOwnerProcessIdentity($"{goal.Id.Value}:{task.Id.Value}", process.ProcessId, dispatchedAt)!.ProcessId);
        WriteHeartbeat(process, end.AddSeconds(-1), end.AddSeconds(-1), "running", 0, 0, childPid: null, ownedCpuMs: 953);
        var evidence = new ConductorGenerationInterruptionEvidence(clock);
        Assert.Equal(mode == "matching", evidence.TryProve(goal.Id, task, process, out _));
        // Make reconciliation stale, while retaining the termination/heartbeat correlation.
        clock.Advance(TimeSpan.FromMinutes(32));
        var runner = new BackgroundDispatchRunner(clock, isStillRunning: _ => false,
            findBuildDaemons: _ => [], generationInterruption: evidence);
        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal("uncommitted work", File.ReadAllText(dirtyFile));
        Assert.Equal(string.Empty, File.ReadAllText(process.StandardErrorPath));
        Assert.True(DispatchExitArtifacts.TryRead(process.ExitCodePath, out var artifact));
        Assert.Equal("process missing with dirty worktree evidence", artifact.Reason);
        Assert.Equal(DispatchExitArtifactOrigin.Synthetic, artifact.Origin);
        Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Message.Contains("InterruptedDispatchWorkPreserved", StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, evt => evt.Message.Contains("StaleDispatchAutoRequeued", StringComparison.Ordinal));
        if (mode == "matching")
        {
            Assert.Contains(task.Status, new[] { WorkTaskStatus.Pending, WorkTaskStatus.Assigned });
            Assert.NotEqual(GoalStatus.Failed, goal.Status);
            Assert.DoesNotContain(goal.Timeline, evt => evt.Message.Contains("GoalEscalated", StringComparison.Ordinal));
            Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.HumanInputRequested);
            Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed);
            Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskNote && evt.Message.Contains("CLASSIFIER rule=conductor-generation-interrupted", StringComparison.Ordinal));
            Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried && evt.Message.Contains("attempt 1/3", StringComparison.Ordinal));
            Assert.Contains("CONDUCTOR_GENERATION_INTERRUPTED owner_pid=4242", Assert.Single(task.VerificationHistory).AuthoritativeStandardError);
            Assert.Equal(clock.UtcNow.AddMinutes(1), task.SubscriptionRetryAfter);
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Contains(kernel.BuildNextActions(goal.Id).Items, action => action.TaskId == task.Id && action.Kind == NextActionKind.RunAssignedTask);
        }
        else
        {
            Assert.Equal(WorkTaskStatus.Failed, task.Status);
            Assert.Equal("process missing with dirty worktree evidence", task.LastProcess!.ExitArtifactReason);
            Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskNote && evt.Message.Contains("CLASSIFIER rule=unknown-failure", StringComparison.Ordinal));
            Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried);
        }
    }

    private sealed class ClockAdapter(IClock clock) : TimeProvider { public override DateTimeOffset GetUtcNow() => clock.UtcNow; }
}
