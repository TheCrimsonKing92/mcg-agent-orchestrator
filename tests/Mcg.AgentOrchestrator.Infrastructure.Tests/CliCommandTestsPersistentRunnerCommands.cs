using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

[Xunit.Collection("GoalWorktreeCleanupHooks")]
public sealed class CliCommandTestsPersistentRunnerCommands : CliCommandTestBase
{
    [Xunit.Fact]
    public void TransientSqliteCheckpointNewGoalBaselineLoadIsContainedPerGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var heldGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "newly ingested held goal");
        var unrelatedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "unrelated durable goal");
        var repository = new InMemoryTransactionalStateRepository(kernel)
        {
            BeforeNextLoadGoalAsync = _ =>
            {
                var exception = new SqliteException("busy baseline load", 5);
                exception.Data["Mcg.AttemptCount"] = 4;
                exception.Data["Mcg.ElapsedMilliseconds"] = 375d;
                throw exception;
            }
        };
        var snapshots = kernel.ExportSnapshot().Goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
        var baselines = new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal)
        {
            [unrelatedGoal.Id.Value] = snapshots[unrelatedGoal.Id.Value]
        };

        var requests = CliPersistentStateRunner.BuildConductLoopCheckpointRequests(
            kernel,
            [heldGoal.Id, unrelatedGoal.Id],
            baselines,
            repository,
            "C:/fixture/state.db",
            containTransientBaselineLoads: true,
            out var holds);

        var hold = Xunit.Assert.Single(holds);
        Xunit.Assert.Equal(heldGoal.Id.Value, hold.GoalId);
        Xunit.Assert.Equal(GoalSnapshotCheckpointDisposition.Held, hold.Disposition);
        Xunit.Assert.Equal(5, hold.SqliteErrorCode);
        Xunit.Assert.Equal(4, hold.AttemptCount);
        Xunit.Assert.Equal(375d, hold.ElapsedMilliseconds);
        Xunit.Assert.Contains("LoadGoalAsync", hold.Operation, StringComparison.Ordinal);
        Xunit.Assert.Equal(unrelatedGoal.Id.Value, Xunit.Assert.Single(requests).Current.Id);
    }

    [Xunit.Fact]
    public void TransientSqliteCheckpointNewGoalBaselineLoadNonTransientFailureRemainsFailClosed()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "newly ingested readonly goal");
        var repository = new InMemoryTransactionalStateRepository(kernel)
        {
            BeforeNextLoadGoalAsync = _ => throw new SqliteException("readonly baseline load", 8)
        };

        var exception = Xunit.Assert.Throws<SqliteException>(() =>
            CliPersistentStateRunner.BuildConductLoopCheckpointRequests(
                kernel,
                [goal.Id],
                new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal),
                repository,
                "C:/fixture/state.db",
                containTransientBaselineLoads: true,
                out _));

        Xunit.Assert.Equal(8, exception.SqliteErrorCode);
    }

    [Xunit.Fact]
    public void TransientSqliteCheckpointHeldPendingSnapshotSurvivesScheduledRefresh()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "held pending refresh goal");
        var durableSnapshot = kernel.ExportSnapshot();
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("fixture", "fixture", "C:/fixture", DateTimeOffset.UtcNow));

        var heldRefreshes = CliCommandHandlers.RefreshTrackedGoalsPreservingCheckpointHolds(
            kernel,
            durableSnapshot,
            new HashSet<string>([goal.Id.Value], StringComparer.Ordinal));

        Xunit.Assert.Equal(0, heldRefreshes);
        Xunit.Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);

        var recoveredRefreshes = CliCommandHandlers.RefreshTrackedGoalsPreservingCheckpointHolds(
            kernel,
            durableSnapshot,
            new HashSet<string>(StringComparer.Ordinal));

        Xunit.Assert.Equal(1, recoveredRefreshes);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, task.Id).Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData(5)]
    [Xunit.InlineData(6)]
    public void TransientSqliteCheckpointStartupLoadHoldsUnderLeaseAndRecovers(int sqliteErrorCode)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var loadAttempts = 0;
        var delays = new List<TimeSpan>();
        using var lease = ConductorLoopLeaseController.Acquire(workspace.OrchestratorDirectory);

        var loaded = CliPersistentStateRunner.LoadInitialConductLoopKernelWithTransientHold(
            () =>
            {
                Assert.True(lease.IsHeld);
                loadAttempts++;
                if (loadAttempts == 1)
                {
                    var exception = new SqliteException("startup database locked", sqliteErrorCode);
                    exception.Data["Mcg.AttemptCount"] = 3;
                    exception.Data["Mcg.ElapsedMilliseconds"] = 250d;
                    throw exception;
                }
                return new AgentOrchestratorKernel();
            },
            workspace,
            stopRequested: () => false,
            holdDelay: delays.Add,
            holdInterval: TimeSpan.FromMilliseconds(25));

        Assert.True(lease.IsHeld);
        Assert.Empty(loaded.Goals);
        Assert.Equal(2, loadAttempts);
        Assert.Equal([TimeSpan.FromMilliseconds(25)], delays);
        var events = File.ReadAllLines(workspace.ConductEventsLogPath);
        Assert.Contains(events, line => line.Contains("LOOP_LOAD_HOLD", StringComparison.Ordinal) &&
            line.Contains($"sqlite_code={sqliteErrorCode}", StringComparison.Ordinal) &&
            line.Contains("attempt=3", StringComparison.Ordinal) &&
            line.Contains("elapsed_ms=250", StringComparison.Ordinal));
        var recovered = Assert.Single(events, line => line.Contains("LOOP_LOAD_RECOVERED", StringComparison.Ordinal));
        Assert.Contains($"sqlite_code={sqliteErrorCode}", recovered, StringComparison.Ordinal);
        Assert.Contains("sqlite_extended_code=", recovered, StringComparison.Ordinal);
        Assert.Contains("elapsed_ms=250", recovered, StringComparison.Ordinal);
        Assert.Contains("disposition=recovered", recovered, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void TransientSqliteCheckpointStartupLoadNonTransientFailuresRemainFailClosed(bool sqliteCodeEight)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var exception = sqliteCodeEight
            ? (Exception)new SqliteException("readonly", 8)
            : new InvalidOperationException("non-SQLite load failure");

        var actual = Assert.Throws(exception.GetType(), () =>
            CliPersistentStateRunner.LoadInitialConductLoopKernelWithTransientHold(
                () => throw exception,
                workspace,
                holdDelay: _ => throw new Xunit.Sdk.XunitException("delay must not run")));

        Assert.Same(exception, actual);
    }

    [Xunit.Fact]
    public void TransientSqliteCheckpoint_StartupLoadYieldsAfterFiniteRetries()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var attempts = 0;
        var delays = new List<TimeSpan>();
        TransientSqliteLoadHold? exhausted = null;

        var kernel = CliPersistentStateRunner.LoadInitialConductLoopKernelWithTransientHold(
            () =>
            {
                attempts++;
                var exception = new SqliteException("startup remains locked", 5);
                exception.Data["Mcg.AttemptCount"] = 3;
                exception.Data["Mcg.ElapsedMilliseconds"] = 250d;
                throw exception;
            },
            workspace,
            holdDelay: delays.Add,
            holdInterval: TimeSpan.FromMilliseconds(25),
            maxHoldRetries: 1,
            onHoldExhausted: hold => exhausted = hold);

        Assert.Empty(kernel.Goals);
        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(25)], delays);
        Assert.NotNull(exhausted);
        Assert.Equal(5, exhausted!.SqliteErrorCode);
        Assert.Equal("loop:startup/load", exhausted.Operation);
        var events = File.ReadAllLines(workspace.ConductEventsLogPath);
        Assert.Equal(2, events.Count(line => line.Contains("LOOP_LOAD_HOLD", StringComparison.Ordinal)));
        Assert.DoesNotContain(events, line => line.Contains("LOOP_LOAD_RECOVERED", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void TransientSqliteCheckpoint_ScheduledLoadEmitsRecovery()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var writer = new ConductEventLogWriter(workspace.ConductEventsLogPath);
        TransientSqliteLoadHold? episode = null;
        var attempts = 0;

        var held = CliPersistentStateRunner.TryReloadConductLoopKernel(
            () =>
            {
                attempts++;
                var exception = new SqliteException("scheduled reload locked", 6);
                exception.Data["Mcg.AttemptCount"] = 4;
                exception.Data["Mcg.ElapsedMilliseconds"] = 375d;
                throw exception;
            },
            workspace,
            writer,
            ref episode,
            out var heldKernel);
        var recovered = CliPersistentStateRunner.TryReloadConductLoopKernel(
            () =>
            {
                attempts++;
                return new AgentOrchestratorKernel();
            },
            workspace,
            writer,
            ref episode,
            out var recoveredKernel);

        Assert.False(held);
        Assert.Null(heldKernel);
        Assert.True(recovered);
        Assert.NotNull(recoveredKernel);
        Assert.Null(episode);
        Assert.Equal(2, attempts);
        var events = File.ReadAllLines(workspace.ConductEventsLogPath);
        var holdLine = Assert.Single(events, line => line.Contains("TICK_LOAD_HOLD", StringComparison.Ordinal));
        Assert.Contains("operation=loop:tick/reload", holdLine, StringComparison.Ordinal);
        Assert.Contains("sqlite_code=6", holdLine, StringComparison.Ordinal);
        Assert.Contains("attempt=4", holdLine, StringComparison.Ordinal);
        Assert.Contains("elapsed_ms=375", holdLine, StringComparison.Ordinal);
        var recoveryLine = Assert.Single(events, line => line.Contains("TICK_LOAD_RECOVERED", StringComparison.Ordinal));
        Assert.Contains("sqlite_code=6", recoveryLine, StringComparison.Ordinal);
        Assert.Contains("disposition=recovered", recoveryLine, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task OperatorDecisionRepositoryBootstrapsFreshStateStoreBeforeUse()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        Xunit.Assert.False(StateDbMigrations.IsUpToDate(workspace.SqliteStatePath));

        var repository = CliCommandHandlers.CreateOperatorDecisionStateRepository(
            ["answer", "request-id", "answer"],
            workspace);

        Xunit.Assert.True(StateDbMigrations.IsUpToDate(workspace.SqliteStatePath));
        var restored = await repository.LoadAsync();
        Xunit.Assert.Empty(restored.Goals);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_backlog_show_loads_kernel_state_for_linked_goals")]
    public async Task PersistentRunnerBacklogShowLoadsKernelStateForLinkedGoals()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlogStore.AddAsync("Persistent linked item");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Persistent backlog-show linked goal", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        await repository.SaveAsync(kernel);

        var output = CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["backlog-show", item.Id[..8]],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("Linked goals:", output);
        Xunit.Assert.Contains(goal.Id.Value[..8], output);
        Xunit.Assert.Contains("landing=", output);
    }

    [Xunit.Fact(DisplayName = "Cli_startup_conduct_help_exits_before_workspace_setup")]
    public void CliStartupConductHelpExitsBeforeWorkspaceSetup()
    {
        var root = CreateTempDirectory();
        var beforeFiles = SnapshotFiles(root);

        var result = RunAppCommand(root, "conduct", "--help");

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("Usage: conduct <goal-id-prefix>", result.Stdout);
        Xunit.Assert.Contains("-h, --help", result.Stdout);
        Xunit.Assert.DoesNotContain("Error:", result.Stderr);
        Xunit.Assert.Equal(beforeFiles, SnapshotFiles(root));
    }

    [Xunit.Theory(DisplayName = "Cli_dispatch_start_registration_failure_commits_before_nonzero_exit")]
    [Xunit.InlineData("start-dispatch", "--confirm-dispatch-start")]
    [Xunit.InlineData("start-dispatches", "--confirm-batch-start")]
    [Xunit.InlineData("advance-subscription", "--confirm-subscription-advance")]
    [Xunit.InlineData("run-goal", "--confirm-batch-start")]
    public async Task CliDispatchStartRegistrationFailureCommitsBeforeNonzeroExit(
        string command,
        string confirmation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var previousDisableStart = Environment.GetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable);
        try
        {
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, null);
            var workspace = CreateRefinedWorkspace(root);
            const string profileName = "registration-failure-fixture";
            WorkerProfileStore.Save(
                workspace.WorkerProfilePath,
                WorkerProfileCatalog.Default().Upsert(new WorkerProfile(profileName, "Write-Output ok")));
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(
                "Commit registration failure before CLI exit",
                [new TaskSpec(TaskId.New(), "Inspect harmless fixture", AgentRole.Planner)]);
            MarkGoalRefined(kernel, goal);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord(profileName, "Write-Output ok", root, DateTimeOffset.UtcNow));
            await repository.SaveAsync(kernel);

            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={workspace.SqliteStatePath};Pooling=False"))
            {
                connection.Open();
                using var trigger = connection.CreateCommand();
                trigger.CommandText = """
                    CREATE TRIGGER fail_spawn_registration
                    BEFORE INSERT ON spawn_registry
                    BEGIN
                        SELECT RAISE(ABORT, 'forced registration failure');
                    END
                    """;
                trigger.ExecuteNonQuery();
            }

            string[] arguments = command switch
            {
                "start-dispatch" =>
                [
                    command,
                    "--goal",
                    goal.Id.Value[..8],
                    "1",
                    confirmation,
                    "--confirm-large-paid-subscription-start"
                ],
                "start-dispatches" =>
                [
                    command,
                    "--goal",
                    goal.Id.Value[..8],
                    confirmation,
                    "--confirm-large-paid-subscription-start"
                ],
                "advance-subscription" =>
                [
                    command,
                    goal.Id.Value[..8],
                    confirmation,
                    "--confirm-large-paid-subscription-start"
                ],
                "run-goal" =>
                [
                    command,
                    goal.Id.Value[..8],
                    confirmation,
                    "--confirm-readiness-risk",
                    "--confirm-large-paid-subscription-start"
                ],
                _ => throw new InvalidOperationException($"Unsupported fixture command '{command}'.")
            };
            var result = RunAppCommand(root, arguments);

            Xunit.Assert.Equal(1, result.ExitCode);
            Xunit.Assert.Contains("worker-process-registration-failed", result.Stderr, StringComparison.Ordinal);
            Xunit.Assert.Contains("stage=durable-registry-write", result.Stderr, StringComparison.Ordinal);

            var restored = await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync();
            var restoredGoal = restored.GetGoal(goal.Id);
            var restoredTask = restoredGoal.Tasks.Single(candidate => candidate.Id == task.Id);
            Xunit.Assert.Equal(WorkTaskStatus.Failed, restoredTask.Status);
            Xunit.Assert.Null(restoredTask.LastProcess);
            Xunit.Assert.NotNull(restoredTask.LastDispatch);
            Xunit.Assert.Contains(restoredGoal.Timeline, evt =>
                evt.TaskId == restoredTask.Id &&
                evt.Kind == ProgressKind.TaskFailed &&
                evt.Message.Contains("worker-process-registration-failed", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, previousDisableStart);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }


    [Xunit.Fact(DisplayName = "Persistent_runner_park_goal_uses_real_sqlite_without_transaction_self_conflict")]
    public async Task PersistentRunnerParkGoalUsesRealSqliteWithoutTransactionSelfConflict()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Park from persistent runner", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        _ = kernel.RequestHumanInput(goal.Id, null, "Need operator decision.", HumanWaitKind.RiskReview);
        await repository.SaveAsync(kernel);

        var output = CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                CliArgumentParser.SplitCommand($"park-goal {goal.Id.Value[..8]} Operator froze churn. --confirm-goal-park"),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        Xunit.Assert.Equal(GoalStatus.Parked, restoredGoal.Status);
        Xunit.Assert.All(
            restored.HumanInputRequests.Where(request => request.GoalId == goal.Id),
            request => Xunit.Assert.True(request.IsCompleted));
        Xunit.Assert.Contains("Goal parked", output);
        Xunit.Assert.Contains("Resolved human waits: 1", output);
    }


    [Xunit.Fact(DisplayName = "Persistent_runner_park_goal_text_file_records_file_reason")]
    public async Task PersistentRunnerParkGoalTextFileRecordsFileReason()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Park from file", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var wait = kernel.RequestHumanInput(goal.Id, null, "Need operator decision.", HumanWaitKind.RiskReview);
        await repository.SaveAsync(kernel);
        var reason = "Operator parked from a text file.\n\nPreserve the full disposition receipt.";
        var reasonPath = Path.Combine(root, "park-reason.md");
        File.WriteAllText(reasonPath, reason, System.Text.Encoding.UTF8);

        var output = CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                CliArgumentParser.SplitCommand($"park-goal {goal.Id.Value[..8]} --text-file {reasonPath} --confirm-goal-park"),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        var restoredWait = restored.HumanInputRequests.Single(request => request.Id == wait.Id);
        Xunit.Assert.Equal(GoalStatus.Parked, restoredGoal.Status);
        Xunit.Assert.Contains(restoredGoal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message == $"Goal parked: {reason}");
        Xunit.Assert.True(restoredWait.IsCompleted);
        Xunit.Assert.Equal($"Goal parked: {reason}", restoredWait.Answer);
        Xunit.Assert.Contains("Goal parked", output);
    }


    [Xunit.Fact(DisplayName = "Persistent_runner_unpark_goal_text_file_records_file_reason_and_exits_zero")]
    public async Task PersistentRunnerUnparkGoalTextFileRecordsFileReasonAndExitsZero()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Unpark from file", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.ParkGoal(goal.Id, "operator parked from test");
        await repository.SaveAsync(kernel);
        var reason = "Operator unparked from a text file.\n\nResume normal conductor handling.";
        var reasonPath = Path.Combine(root, "unpark-reason.md");
        File.WriteAllText(reasonPath, reason, System.Text.Encoding.UTF8);

        var result = RunAppCommand(root, "unpark-goal", goal.Id.Value[..8], "--text-file", reasonPath, "--confirm-goal-unpark");

        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("Goal unparked", result.Stdout);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Xunit.Assert.Equal(GoalStatus.Active, restoredGoal.Status);
        Xunit.Assert.Contains(restoredGoal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message == $"Goal unparked: {reason}");
    }


    [Xunit.Fact(DisplayName = "Persistent_runner_lifecycle_disposition_audit_routes_side_effect_verbs_outside_generic_transaction")]
    public void PersistentRunnerLifecycleDispositionAuditRoutesSideEffectVerbsOutsideGenericTransaction()
    {
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalLifecycleDispositionCommand(["park-goal", "abcdef12", "reason"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalLifecycleDispositionCommand(["unpark-goal", "abcdef12", "reason"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalLifecycleDispositionCommand(["abandon-goal", "abcdef12", "reason"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsGoalLifecycleDispositionCommand(["goal-mark-landed", "abcdef12", "--confirm-goal-mark-landed"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalMarkLandedCommand(["goal-mark-landed", "abcdef12", "--confirm-goal-mark-landed"]));
        Xunit.Assert.DoesNotContain(
            CliArgumentParser.RecognizedCommands,
            command => command.Equals("resume-goal", StringComparison.OrdinalIgnoreCase));
    }


    [Xunit.Theory(DisplayName = "CliPersistentStateRunner_identifies_first_batch_goal_scoped_task_mutations")]
    [Xunit.InlineData(new[] { "progress", "1", "running", "started" }, true)]
    [Xunit.InlineData(new[] { "verify-manual", "1", "passed", "checked" }, true)]
    [Xunit.InlineData(new[] { "retry", "1", "again" }, true)]
    [Xunit.InlineData(new[] { "verification-plan", "1", "dotnet test" }, true)]
    [Xunit.InlineData(new[] { "note", "1", "operator note" }, true)]
    [Xunit.InlineData(new[] { "add-task", "Developer", "new task" }, false)]
    [Xunit.InlineData(new[] { "backlog-add", "new item" }, false)]
    [Xunit.InlineData(new[] { "reassign-agent", "1", "developer" }, false)]
    public void PersistentRunnerIdentifiesFirstBatchGoalScopedTaskMutations(string[] args, bool expected)
    {
        Xunit.Assert.Equal(expected, CliPersistentStateRunner.IsGoalScopedTaskMutationCommand(args));
    }

    [Xunit.Theory(DisplayName = "CliPersistentStateRunner_routes_first_recovery_verbs_to_operator_intent_inbox")]
    [Xunit.InlineData(new[] { "retry", "1", "again" }, true)]
    [Xunit.InlineData(new[] { "verify-manual", "1", "passed", "checked" }, true)]
    [Xunit.InlineData(new[] { "progress", "1", "running", "started" }, true)]
    [Xunit.InlineData(new[] { "verification-plan", "1", "dotnet test" }, false)]
    public void PersistentRunnerRoutesFirstRecoveryVerbsToOperatorIntentInbox(string[] args, bool expected)
    {
        Xunit.Assert.Equal(expected, CliPersistentStateRunner.IsInboxBackedGoalScopedTaskMutationCommand(args));
    }

    [Xunit.Theory(DisplayName = "CliPersistentStateRunner_operator_intent_attribution_enumerates_every_submission_source")]
    [Xunit.InlineData("Cli", "local:operator", "cli", "local-process")]
    [Xunit.InlineData("Discord", "discord:user1", "discord", "discord-operator-allowlist")]
    public void PersistentRunnerOperatorIntentAttributionEnumeratesEverySubmissionSource(
        string sourceName,
        string actor,
        string expectedChannel,
        string expectedAuthenticationAssurance)
    {
        Xunit.Assert.Equal(2, Enum.GetValues<CliPersistentStateRunner.OperatorIntentSubmissionSource>().Length);
        var source = Enum.Parse<CliPersistentStateRunner.OperatorIntentSubmissionSource>(sourceName);

        var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(
            ["retry", "1", "again", "--operator-actor", actor],
            source);

        Xunit.Assert.Equal(actor, attribution.Actor);
        Xunit.Assert.Equal(expectedChannel, attribution.Channel);
        Xunit.Assert.Equal(expectedAuthenticationAssurance, attribution.AuthenticationAssurance);
    }

    [Xunit.Theory(DisplayName = "CliPersistentStateRunner_retry_reports_conductor_liveness_without_state_transaction")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task PersistentRunnerRetryReportsConductorLivenessWithoutStateTransaction(bool conductorActive)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Inbox retry", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed first");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        var configuredChannel = new DiscordOperatorChannel(
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
        using var conductorLease = conductorActive
            ? ConductorLoopLease.Acquire(workspace.OrchestratorDirectory)
            : null;
        var output = CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["retry", "1", "retry through inbox", "--idempotency-key", "retry-test-key"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal,
                configuredChannel);
            Xunit.Assert.False(changed);
        });

        var persistedGoal = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [(await repository.LoadGoalAsync(goal.Id))!],
            [])).GetGoal(goal.Id);
        var intents = await SqliteOperatorIntentStore
            .ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .ListForGoalAsync(goal.Id.Value);

        Xunit.Assert.Equal(0, repository.TransactAsyncCount);
        Xunit.Assert.Equal(0, repository.TransactGoalCount);
        Xunit.Assert.Equal(WorkTaskStatus.Failed, persistedGoal.Tasks.Single().Status);
        var intent = Xunit.Assert.Single(intents);
        Xunit.Assert.Equal(OperatorIntentVerbs.Retry, intent.Verb);
        Xunit.Assert.Equal(task.Id.Value, intent.TaskId);
        Xunit.Assert.Equal("retry-test-key", intent.IdempotencyKey);
        Xunit.Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Xunit.Assert.Equal("operator", intent.Actor);
        Xunit.Assert.Equal("cli", intent.Channel);
        Xunit.Assert.Equal("local-process", intent.AuthenticationAssurance);
        Xunit.Assert.Contains("Operator intent queued", output, StringComparison.Ordinal);
        const string inactiveWarning =
            "WARNING: intent queued but NO conduct loop is running - it will not apply until a loop starts.";
        if (conductorActive)
        {
            Xunit.Assert.DoesNotContain(inactiveWarning, output, StringComparison.Ordinal);
        }
        else
        {
            Xunit.Assert.Contains(inactiveWarning, output, StringComparison.Ordinal);
        }

        Xunit.Assert.True(Directory.EnumerateFiles(
            workspace.LogDirectory,
            $"*{SqliteOperatorIntentStore.WakeFileSuffix}").Any());
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_scoped_task_mutation_delegate_has_no_reload_callback")]
    public void PersistentRunnerGoalScopedTaskMutationDelegateHasNoReloadCallback()
    {
        var source = File.ReadAllText(Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "src",
            "Mcg.AgentOrchestrator.App",
            "Cli",
            "CliPersistentStateRunner.cs"));
        var methodStart = source.IndexOf(
            "private static bool ExecuteGoalScopedTaskMutationCommand",
            StringComparison.Ordinal);
        Xunit.Assert.True(methodStart >= 0, "Could not find ExecuteGoalScopedTaskMutationCommand.");

        var methodEnd = source.IndexOf(
            "private static bool ExecuteProvenanceWithoutFullHydration",
            methodStart,
            StringComparison.Ordinal);

        Xunit.Assert.True(methodEnd > methodStart, "Could not isolate ExecuteGoalScopedTaskMutationCommand.");
        var methodSource = source[methodStart..methodEnd];

        Xunit.Assert.Contains("TransactGoalStateAsync", methodSource, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("LoadSingleGoalKernel", methodSource, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("LoadAsync", methodSource, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("reloadKernel", methodSource, StringComparison.Ordinal);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_scoped_task_mutations_use_goal_CAS_without_LoadAsync")]
    public async Task PersistentRunnerGoalScopedTaskMutationsUseGoalCasWithoutLoadAsync()
    {
        var cases = new (string Name, IReadOnlyList<string> Args, Action<AgentOrchestratorKernel, Goal, TaskSpec> Arrange, Action<Goal, TaskSpec> AssertState)[]
        {
            (
                "verification-plan",
                ["verification-plan", "1", "dotnet test --filter scoped"],
                (_, _, _) => { },
                (goal, task) =>
                {
                    Xunit.Assert.Equal("dotnet test --filter scoped", task.VerificationPlan);
                    Xunit.Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskVerificationPlanUpdated);
                }),
            (
                "note",
                ["note", "1", "operator note"],
                (_, _, _) => { },
                (goal, task) =>
                    Xunit.Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.OperatorTaskNote && evt.Message == "operator note"))
        };

        foreach (var testCase in cases)
        {
            var root = CreateTempDirectory();
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal($"Goal scoped {testCase.Name}", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            var task = goal.Tasks.Single();
            testCase.Arrange(kernel, goal, task);
            var repository = new InMemoryTransactionalStateRepository(kernel)
            {
                ThrowOnLoadAsync = true,
                ThrowOnLoadWhileInTransaction = true
            };

            CaptureConsole(() =>
            {
                var changed = CliPersistentStateRunner.ExecuteCommand(
                    testCase.Args,
                    repository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
                Xunit.Assert.True(changed);
            });

            var storedSnapshot = await repository.LoadGoalAsync(goal.Id);
            var storedKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([storedSnapshot!], []));
            var storedGoal = storedKernel.GetGoal(goal.Id);
            var storedTask = storedGoal.Tasks.Single();
            Xunit.Assert.Equal(0, repository.TransactAsyncCount);
            Xunit.Assert.Equal(1, repository.TransactGoalCount);
            Xunit.Assert.Equal(1, repository.TransactGoalDelegateCalls);
            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.Equal(0, repository.LoadWhileInTransactionCount);
            testCase.AssertState(storedGoal, storedTask);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_scoped_task_mutations_match_full_kernel_baseline")]
    public async Task PersistentRunnerGoalScopedTaskMutationsMatchFullKernelBaseline()
    {
        var cases = new (string Name, IReadOnlyList<string> Args, Action<AgentOrchestratorKernel, Goal, TaskSpec> Arrange)[]
        {
            (
                "verification-plan",
                ["verification-plan", "--goal", "{goal}", "1", "dotnet test --filter scoped"],
                (_, _, _) => { }),
            (
                "note",
                ["note", "--goal", "{goal}", "1", "operator note"],
                (_, _, _) => { })
        };

        foreach (var testCase in cases)
        {
            var root = CreateTempDirectory();
            var workspace = CreateRefinedWorkspace(root);
            var initialKernel = new AgentOrchestratorKernel();
            var goal = initialKernel.CreateGoal(
                $"Baseline {testCase.Name}",
                [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            var agents = AgentCatalog.Default().Agents;
            initialKernel.ActivateGoal(goal.Id, agents);
            var task = goal.Tasks.Single();
            testCase.Arrange(initialKernel, goal, task);
            var initialSnapshot = initialKernel.ExportSnapshot();
            var args = testCase.Args
                .Select(arg => arg == "{goal}" ? goal.Id.Value[..8] : arg)
                .ToArray();

            var fullKernel = AgentOrchestratorKernel.FromSnapshot(initialSnapshot);
            IReadOnlyList<AgentDefinition> fullAgents = AgentCatalog.Default().Agents;
            var fullProviders = new InMemoryModelProviderRegistry([]);
            var fullProfiles = WorkerProfileCatalog.Default();
            Goal? fullCurrentGoal = fullKernel.GetGoal(goal.Id);
            CaptureConsole(() =>
            {
                var changed = CliCommandDispatcher.ExecuteCommand(
                    args,
                    fullKernel,
                    workspace,
                    ref fullAgents,
                    fullProviders,
                    ref fullProfiles,
                    ref fullCurrentGoal,
                    reloadKernel: () => AgentOrchestratorKernel.FromSnapshot(fullKernel.ExportSnapshot()));
                Xunit.Assert.True(changed);
            });

            var scopedRepository = new InMemoryTransactionalStateRepository(AgentOrchestratorKernel.FromSnapshot(initialSnapshot))
            {
                ThrowOnLoadAsync = true,
                ThrowOnLoadWhileInTransaction = true
            };
            IReadOnlyList<AgentDefinition> scopedAgents = AgentCatalog.Default().Agents;
            var scopedProviders = new InMemoryModelProviderRegistry([]);
            var scopedProfiles = WorkerProfileCatalog.Default();
            Goal? scopedCurrentGoal = goal;
            CaptureConsole(() =>
            {
                var changed = CliPersistentStateRunner.ExecuteCommand(
                    args,
                    scopedRepository,
                    workspace,
                    ref scopedAgents,
                    scopedProviders,
                    ref scopedProfiles,
                    ref scopedCurrentGoal);
                Xunit.Assert.True(changed);
            });

            var fullSnapshot = fullKernel.ExportSnapshot().Goals.Single(snapshot => snapshot.Id == goal.Id.Value);
            var scopedSnapshot = await scopedRepository.LoadGoalAsync(goal.Id);
            Xunit.Assert.Equal(NormalizeVolatileTimes(fullSnapshot), NormalizeVolatileTimes(scopedSnapshot!));
            Xunit.Assert.Equal(0, scopedRepository.TransactAsyncCount);
            Xunit.Assert.Equal(1, scopedRepository.TransactGoalCount);
            Xunit.Assert.Equal(0, scopedRepository.LoadCount);
            Xunit.Assert.Equal(0, scopedRepository.LoadWhileInTransactionCount);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_scoped_retry_defers_state_guard_to_tick")]
    public async Task PersistentRunnerGoalScopedRetryDefersStateGuardToTick()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Retry waits for human answer", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        kernel.RequestHumanInput(goal.Id, task.Id, "Choose a retry path.");
        var repository = new InMemoryTransactionalStateRepository(kernel)
        {
            ThrowOnLoadAsync = true
        };

        var changed = CliPersistentStateRunner.ExecuteCommand(
            ["retry", "1", "retry too early"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        var intents = await SqliteOperatorIntentStore
            .ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .ListForGoalAsync(goal.Id.Value);

        Xunit.Assert.False(changed);
        Xunit.Assert.Equal(OperatorIntentStatus.Pending, Xunit.Assert.Single(intents).Status);
        Xunit.Assert.Equal(0, repository.TransactAsyncCount);
        Xunit.Assert.Equal(0, repository.TransactGoalCount);
        Xunit.Assert.Equal(0, repository.LoadCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_progress_queues_without_mutating_waiting_goal")]
    public async Task PersistentRunnerProgressQueuesWithoutMutatingWaitingGoal()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Progress while waiting for human", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Choose a retry path.");
        var repository = new InMemoryTransactionalStateRepository(kernel)
        {
            ThrowOnLoadAsync = true,
            ThrowOnLoadWhileInTransaction = true
        };

        CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["progress", "1", "running", "worker restarted while input remains pending"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        var storedSnapshot = await repository.LoadGoalAsync(goal.Id);
        var storedKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([storedSnapshot!], kernel.ExportSnapshot().HumanInputRequests));
        var storedGoal = storedKernel.GetGoal(goal.Id);
        var storedTask = storedGoal.Tasks.Single();
        var intent = Xunit.Assert.Single(await SqliteOperatorIntentStore
            .OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .ListForGoalAsync(goal.Id.Value));
        Xunit.Assert.Equal(GoalStatus.WaitingForHuman, storedGoal.Status);
        Xunit.Assert.Equal(task.Status, storedTask.Status);
        Xunit.Assert.Contains(storedKernel.HumanInputRequests, item => item.Id == request.Id && !item.IsCompleted);
        Xunit.Assert.Equal(OperatorIntentVerbs.Progress, intent.Verb);
        Xunit.Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Xunit.Assert.Equal(0, repository.TransactAsyncCount);
        Xunit.Assert.Equal(0, repository.TransactGoalCount);
        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(0, repository.LoadWhileInTransactionCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_manual_verification_queues_without_state_CAS")]
    public async Task PersistentRunnerManualVerificationQueuesWithoutStateCas()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Queue manual verification", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        var repository = new InMemoryTransactionalStateRepository(kernel);

        CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["verify-manual", "1", "passed", "operator checked existing work"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        var storedSnapshot = await repository.LoadGoalAsync(goal.Id);
        var intent = Xunit.Assert.Single(await SqliteOperatorIntentStore
            .OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .ListForGoalAsync(goal.Id.Value));
        Xunit.Assert.Null(storedSnapshot!.Tasks.Single().LastVerification);
        Xunit.Assert.Equal(OperatorIntentVerbs.VerifyManual, intent.Verb);
        Xunit.Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Xunit.Assert.Equal(0, repository.TransactAsyncCount);
        Xunit.Assert.Equal(0, repository.TransactGoalCount);
        Xunit.Assert.Equal(0, repository.LoadWhileInTransactionCount);
    }

    public static IEnumerable<object[]> ExplicitGoalMutationCases()
    {
        foreach (var command in new[] { "progress", "retry", "verify-manual" })
        {
            foreach (var goalAfterPositionals in new[] { false, true })
            {
                foreach (var useFullGoalId in new[] { false, true })
                {
                    foreach (var commandSurface in new[] { "direct", "one-shot", "interactive" })
                    {
                        yield return [command, goalAfterPositionals, useFullGoalId, goalAfterPositionals == useFullGoalId, commandSurface];
                    }
                }
            }
        }
    }

    [Xunit.Theory(DisplayName = "CliPersistentStateRunner_explicit_goal_mutations_never_bind_same_number_task_in_current_goal")]
    [Xunit.MemberData(nameof(ExplicitGoalMutationCases))]
    public async Task PersistentRunnerExplicitGoalMutationsNeverBindSameNumberTaskInCurrentGoal(
        string command,
        bool goalAfterPositionals,
        bool useFullGoalId,
        bool useTextFile,
        string commandSurface)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal(
            new GoalId("38d0e2e9000000000000000000000001"),
            "Explicit target",
            [new TaskSpec(TaskId.New(), "Target task one", AgentRole.Developer)]);
        var current = kernel.CreateGoal(
            new GoalId("b4ae70ef000000000000000000000002"),
            "Current wrong goal",
            [new TaskSpec(TaskId.New(), "Wrong task one", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(target.Id, agents);
        kernel.ActivateGoal(current.Id, agents);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = current;
        var selector = useFullGoalId ? target.Id.Value : target.Id.Value[..8];
        var notePath = Path.Combine(root, "operator-note.md");
        File.WriteAllText(notePath, "explicit target evidence");
        var payload = useTextFile ? new[] { "--text-file", notePath } : new[] { "explicit target evidence" };
        string[] positionals = command switch
        {
            "progress" => ["1", "failed"],
            "retry" => ["1"],
            "verify-manual" => ["1", "passed"],
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };
        string[] rawArgs = goalAfterPositionals && !useTextFile
            ? [command, .. positionals, .. payload, "--goal", selector]
            : goalAfterPositionals
                ? [command, .. positionals, "--goal", selector, .. payload]
            : [command, "--goal", selector, .. positionals, .. payload];
        var args = commandSurface switch
        {
            "direct" => rawArgs,
            "one-shot" => CliArgumentParser.NormalizeArgs(rawArgs),
            "interactive" => CliArgumentParser.SplitCommand(string.Join(' ', rawArgs)),
            _ => throw new ArgumentOutOfRangeException(nameof(commandSurface), commandSurface, null)
        };
        if (command == "progress" && goalAfterPositionals && useFullGoalId && useTextFile)
        {
            Xunit.Assert.Equal(
                new[] { "progress", "1", "failed", "--goal", target.Id.Value, "--text-file", notePath },
                rawArgs);
        }

        var output = CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                args,
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        var intentStore = SqliteOperatorIntentStore.OpenExisting(
            workspace.OrchestratorDirectory,
            workspace.LogDirectory);
        var targetIntent = Xunit.Assert.Single(await intentStore.ListForGoalAsync(target.Id.Value));
        var wrongGoalIntents = await intentStore.ListForGoalAsync(current.Id.Value);
        Xunit.Assert.Equal(target.Tasks.Single().Id.Value, targetIntent.TaskId);
        Xunit.Assert.Empty(wrongGoalIntents);
        Xunit.Assert.Equal(target.Id.Value, currentGoal!.Id.Value);
        Xunit.Assert.Contains($"selector={selector}", output, StringComparison.Ordinal);
        Xunit.Assert.Contains($"goal={target.Id.Value}", output, StringComparison.Ordinal);
        Xunit.Assert.Contains($"task={target.Tasks.Single().Id.Value}", output, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "CliPersistentStateRunner_note_trailing_explicit_goal_never_mutates_current_goal")]
    [Xunit.InlineData("direct")]
    [Xunit.InlineData("one-shot")]
    [Xunit.InlineData("interactive")]
    public async Task PersistentRunnerNoteTrailingExplicitGoalNeverMutatesCurrentGoal(string commandSurface)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal(
            new GoalId("38d0e2e9000000000000000000000001"),
            "Explicit note target",
            [new TaskSpec(TaskId.New(), "Target task one", AgentRole.Developer)]);
        var fallback = kernel.CreateGoal(
            new GoalId("b4ae70ef000000000000000000000002"),
            "Current fallback goal",
            [new TaskSpec(TaskId.New(), "Fallback task one", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(target.Id, agents);
        kernel.ActivateGoal(fallback.Id, agents);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = fallback;
        var rawArgs = new[] { "note", "1", "explicit", "target", "note", "--goal", target.Id.Value[..8] };
        var args = commandSurface switch
        {
            "direct" => rawArgs,
            "one-shot" => CliArgumentParser.NormalizeArgs(rawArgs),
            "interactive" => CliArgumentParser.SplitCommand(string.Join(' ', rawArgs)),
            _ => throw new ArgumentOutOfRangeException(nameof(commandSurface), commandSurface, null)
        };

        var changed = CliPersistentStateRunner.ExecuteCommand(
            args,
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        var storedTarget = await repository.LoadGoalAsync(target.Id);
        var storedFallback = await repository.LoadGoalAsync(fallback.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(target.Id, currentGoal!.Id);
        Xunit.Assert.Contains(storedTarget!.Timeline, entry =>
            entry.Kind == ProgressKind.OperatorTaskNote && entry.Message == "explicit target note");
        Xunit.Assert.DoesNotContain(storedFallback!.Timeline, entry => entry.Kind == ProgressKind.OperatorTaskNote);
    }

    [Xunit.Theory(DisplayName = "CliPersistentStateRunner_ambiguous_trailing_goal_fails_closed_on_normalized_surfaces")]
    [Xunit.InlineData("progress", "one-shot")]
    [Xunit.InlineData("progress", "interactive")]
    [Xunit.InlineData("retry", "one-shot")]
    [Xunit.InlineData("retry", "interactive")]
    [Xunit.InlineData("verify-manual", "one-shot")]
    [Xunit.InlineData("verify-manual", "interactive")]
    [Xunit.InlineData("note", "one-shot")]
    [Xunit.InlineData("note", "interactive")]
    public async Task PersistentRunnerAmbiguousTrailingGoalFailsClosedOnNormalizedSurfaces(
        string command,
        string commandSurface)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var first = kernel.CreateGoal(
            new GoalId("38d0e2e9aaaaaaaaaaaaaaaaaaaaaaa1"),
            "First ambiguous target",
            [new TaskSpec(TaskId.New(), "First task one", AgentRole.Developer)]);
        var second = kernel.CreateGoal(
            new GoalId("38d0e2e9bbbbbbbbbbbbbbbbbbbbbbb2"),
            "Second ambiguous target",
            [new TaskSpec(TaskId.New(), "Second task one", AgentRole.Developer)]);
        var fallback = kernel.CreateGoal(
            new GoalId("b4ae70ef000000000000000000000003"),
            "Current fallback goal",
            [new TaskSpec(TaskId.New(), "Fallback task one", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(first.Id, agents);
        kernel.ActivateGoal(second.Id, agents);
        kernel.ActivateGoal(fallback.Id, agents);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = fallback;
        string[] rawArgs = command switch
        {
            "progress" => [command, "1", "failed", "must", "not", "mutate", "--goal", "38d0e2e9"],
            "retry" => [command, "1", "must", "not", "mutate", "--goal", "38d0e2e9"],
            "verify-manual" => [command, "1", "passed", "must", "not", "mutate", "--goal", "38d0e2e9"],
            "note" => [command, "1", "must", "not", "mutate", "--goal", "38d0e2e9"],
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };
        var args = commandSurface switch
        {
            "one-shot" => CliArgumentParser.NormalizeArgs(rawArgs),
            "interactive" => CliArgumentParser.SplitCommand(string.Join(' ', rawArgs)),
            _ => throw new ArgumentOutOfRangeException(nameof(commandSurface), commandSurface, null)
        };

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() =>
            CliPersistentStateRunner.ExecuteCommand(
                args,
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Equal(fallback.Id, currentGoal!.Id);
        Xunit.Assert.Equal(0, repository.TransactGoalCount);
        Xunit.Assert.False(File.Exists(Path.Combine(
            workspace.OrchestratorDirectory,
            SqliteOperatorIntentStore.DatabaseFileName)));
        foreach (var goal in new[] { first, second, fallback })
        {
            var stored = await repository.LoadGoalAsync(goal.Id);
            Xunit.Assert.Equal(goal.Tasks.Single().Status, stored!.Tasks.Single().Status);
            Xunit.Assert.DoesNotContain(stored.Timeline, entry => entry.Kind == ProgressKind.OperatorTaskNote);
        }
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_rejects_ambiguous_explicit_goal_before_intent_write")]
    public async Task PersistentRunnerRejectsAmbiguousExplicitGoalBeforeIntentWrite()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var first = kernel.CreateGoal(
            new GoalId("38d0e2e9aaaaaaaaaaaaaaaaaaaaaaa1"),
            "First collision",
            [new TaskSpec(TaskId.New(), "Task one", AgentRole.Developer)]);
        var second = kernel.CreateGoal(
            new GoalId("38d0e2e9bbbbbbbbbbbbbbbbbbbbbbb2"),
            "Second collision",
            [new TaskSpec(TaskId.New(), "Task one", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(first.Id, agents);
        kernel.ActivateGoal(second.Id, agents);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = second;

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["progress", "1", "failed", "--goal", "38d0e2e9", "must not enqueue"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
        var intentStore = SqliteOperatorIntentStore.ForDirectories(
            workspace.OrchestratorDirectory,
            workspace.LogDirectory);
        Xunit.Assert.Empty(await intentStore.ListForGoalAsync(first.Id.Value));
        Xunit.Assert.Empty(await intentStore.ListForGoalAsync(second.Id.Value));
    }

    [Xunit.Theory(DisplayName = "CliPersistentStateRunner_rejects_malformed_explicit_goal_before_intent_write")]
    [Xunit.InlineData(new[] { "progress", "1", "failed", "must not enqueue", "--goal" }, "requires")]
    [Xunit.InlineData(new[] { "retry", "--goal", "38d0e2e9", "1", "must not enqueue", "--goal", "b4ae70ef" }, "only once")]
    public async Task PersistentRunnerRejectsMalformedExplicitGoalBeforeIntentWrite(
        string[] args,
        string expectedError)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            new GoalId("38d0e2e9000000000000000000000001"),
            "No malformed writes",
            [new TaskSpec(TaskId.New(), "Task one", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var exception = Xunit.Assert.Throws<ArgumentException>(() =>
            CliPersistentStateRunner.ExecuteCommand(
                args,
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.Contains(expectedError, exception.Message, StringComparison.OrdinalIgnoreCase);
        var intentStore = SqliteOperatorIntentStore.ForDirectories(
            workspace.OrchestratorDirectory,
            workspace.LogDirectory);
        Xunit.Assert.Empty(await intentStore.ListForGoalAsync(goal.Id.Value));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_rejects_task_absent_from_explicit_goal_before_intent_write")]
    public async Task PersistentRunnerRejectsTaskAbsentFromExplicitGoalBeforeIntentWrite()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            new GoalId("38d0e2e9000000000000000000000001"),
            "No absent task writes",
            [new TaskSpec(TaskId.New(), "Only task", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var exception = Xunit.Assert.Throws<KeyNotFoundException>(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["progress", "2", "failed", "--goal", goal.Id.Value, "must not enqueue"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.Contains("Task number '2' was not found", exception.Message, StringComparison.Ordinal);
        var intentStore = SqliteOperatorIntentStore.ForDirectories(
            workspace.OrchestratorDirectory,
            workspace.LogDirectory);
        Xunit.Assert.Empty(await intentStore.ListForGoalAsync(goal.Id.Value));
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_concurrent_disjoint_goal_progress_appends_without_state_writes")]
    public async Task PersistentRunnerConcurrentDisjointGoalProgressAppendsWithoutStateWrites()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goalA = kernel.CreateGoal("Concurrent progress A", [new TaskSpec(TaskId.New(), "Do A", AgentRole.Developer)]);
        var goalB = kernel.CreateGoal("Concurrent progress B", [new TaskSpec(TaskId.New(), "Do B", AgentRole.Developer)]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goalA.Id, agents);
        kernel.ActivateGoal(goalB.Id, agents);
        await repository.SaveAsync(kernel);
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);

        async Task RunProgressAsync(GoalId goalId, string message)
        {
            await Task.Yield();
            // The primary helper already migrated this store; concurrent opens must remain schema-free.
            var localRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            IReadOnlyList<AgentDefinition> localAgents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            ready.Signal();
            start.Wait();
            CliPersistentStateRunner.ExecuteCommand(
                ["progress", "--goal", goalId.Value[..8], "1", "running", message],
                localRepository,
                workspace,
                ref localAgents,
                providers,
                ref profiles,
                ref currentGoal);
        }

        var taskA = Task.Run(() => RunProgressAsync(goalA.Id, "A started"));
        var taskB = Task.Run(() => RunProgressAsync(goalB.Id, "B started"));
        Xunit.Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for concurrent progress tasks to be ready.");
        var sw = Stopwatch.StartNew();
        start.Set();
        await Task.WhenAll(taskA, taskB);
        sw.Stop();

        var restored = await repository.LoadAsync();
        var intentStore = SqliteOperatorIntentStore.OpenExisting(
            workspace.OrchestratorDirectory,
            workspace.LogDirectory);
        var intentA = Xunit.Assert.Single(await intentStore.ListForGoalAsync(goalA.Id.Value));
        var intentB = Xunit.Assert.Single(await intentStore.ListForGoalAsync(goalB.Id.Value));
        Xunit.Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Concurrent migrated progress commands completed in {sw.ElapsedMilliseconds}ms; expected no tick-long lock wait.");
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, restored.GetGoal(goalA.Id).Tasks.Single().Status);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, restored.GetGoal(goalB.Id).Tasks.Single().Status);
        Xunit.Assert.Equal(OperatorIntentStatus.Pending, intentA.Status);
        Xunit.Assert.Equal(OperatorIntentStatus.Pending, intentB.Status);
        Xunit.Assert.Equal(OperatorIntentVerbs.Progress, intentA.Verb);
        Xunit.Assert.Equal(OperatorIntentVerbs.Progress, intentB.Verb);
    }


    [Xunit.Fact(DisplayName = "Persistent_runner_unpark_goal_rejects_non_parked_goals_with_nonzero_exit")]
    public async Task PersistentRunnerUnparkGoalRejectsNonParkedGoalsWithNonzeroExit()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var active = kernel.CreateGoal("Active unpark rejection", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var completed = kernel.CreateGoal("Completed unpark rejection", [new TaskSpec(TaskId.New(), "Done work", AgentRole.Developer)]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(active.Id, agents);
        kernel.ActivateGoal(completed.Id, agents);
        kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
        await repository.SaveAsync(kernel);

        var activeResult = RunAppCommand(root, "unpark-goal", active.Id.Value[..8], "resume", "--confirm-goal-unpark");
        var completedResult = RunAppCommand(root, "unpark-goal", completed.Id.Value[..8], "resume", "--confirm-goal-unpark");

        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(1, activeResult.ExitCode);
        Xunit.Assert.Equal(1, completedResult.ExitCode);
        Xunit.Assert.Contains("unpark-goal only applies to Parked goals", activeResult.Stderr);
        Xunit.Assert.Contains("is Active", activeResult.Stderr);
        Xunit.Assert.Contains("is Completed", completedResult.Stderr);
        Xunit.Assert.Equal(GoalStatus.Active, restored.GetGoal(active.Id).Status);
        Xunit.Assert.Equal(GoalStatus.Completed, restored.GetGoal(completed.Id).Status);
        Xunit.Assert.DoesNotContain(restored.GetGoal(active.Id).Timeline, evt => evt.Message.StartsWith("Goal unparked:", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(restored.GetGoal(completed.Id).Timeline, evt => evt.Message.StartsWith("Goal unparked:", StringComparison.Ordinal));
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_routes_bare_goals_to_metadata_only_listing")]
    public void RunnerRoutesBareGoalsToMetadataOnlyListing()
    {
        Xunit.Assert.True(CliPersistentStateRunner.IsMetadataOnlyListing(["goals"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsMetadataOnlyListing(["GOALS"]));
        // Anything beyond the bare verb must fall through to the normal (hydrating) path.
        Xunit.Assert.False(CliPersistentStateRunner.IsMetadataOnlyListing(["goals", "extra"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsMetadataOnlyListing(["next"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsMetadataOnlyListing([]));
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_routes_acceptance_workflows_outside_command_transaction")]
    public void RunnerRoutesAcceptanceWorkflowsOutsideCommandTransaction()
    {
        Xunit.Assert.True(CliPersistentStateRunner.IsAcceptanceCommand(["acceptance"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsAcceptanceCommand(["accept"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsAcceptanceCommand(["acceptance-queue", "--apply"]));

        Xunit.Assert.False(CliPersistentStateRunner.IsAcceptanceCommand(["next"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsAcceptanceCommand([]));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_routes_all_goal_create_forms_outside_command_transaction")]
    public void RunnerRoutesAllGoalCreateFormsOutsideCommandTransaction()
    {
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalCreateCommand(["goal", "Create an inline goal"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalCreateCommand(["goal", "--brief-file", "brief.md"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalCreateCommand(["goal", "--text-file", "brief.md"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalCreateCommand(["goal", "Create a linked goal", "--backlog-item", "abc", "--backlog-coverage", "slice"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalCreateCommand(["goal", "Create and run", "--run"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalCreateCommand(["goal", "Create simply", "--simple"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalReplacementCommand(["goal-replace", "abc"]));

        Xunit.Assert.False(CliPersistentStateRunner.IsGoalCreateCommand(["goal", "--from-backlog"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsBacklogIntakeCommand(["goal", "--from-backlog"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsBacklogIntakeGoalCreationCommand(["goal", "--from-backlog"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsBacklogIntakeGoalCreationCommand(
            ["backlog-intake", "slice", "--create-goal", "--backlog-coverage", "full"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsBacklogIntakeGoalCreationCommand(
            ["backlog-intake", "slice", "--create-simple-goal", "--request-key", "key", "--backlog-coverage", "full"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsBacklogIntakeGoalCreationCommand(["backlog-intake", "slice"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsBacklogIntakeGoalCreationCommand(
            ["backlog-intake", "slice", "--request-key", "preview-key"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalCreateCommand(["simple-goal", "Sibling command"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalCreateCommand(["simple-goal", "Sibling command", "--request-key", "key"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsGoalCreateCommand(["goal-mark-landed", "abc"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsGoalCreateCommand([]));
    }

}
