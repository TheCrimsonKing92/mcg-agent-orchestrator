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
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord(profileName, "Write-Output ok", root, DateTimeOffset.UtcNow));
            await repository.SaveAsync(kernel);

            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.SqliteStatePath}"))
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

        Xunit.Assert.False(CliPersistentStateRunner.IsGoalCreateCommand(["goal", "--from-backlog"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsBacklogIntakeCommand(["goal", "--from-backlog"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsGoalCreateCommand(["simple-goal", "Sibling command"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsGoalCreateCommand(["goal-mark-landed", "abc"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsGoalCreateCommand([]));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_refinement_allows_concurrent_sqlite_writer")]
    public async Task PersistentRunnerGoalRefinementAllowsConcurrentSqliteWriter()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("blocking-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));

        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var concurrentRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var refiner = new BlockingGoalRefinerProvider();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([refiner]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var createTask = Task.Run(() => CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", "Implement deterministic unlocked goal refinement"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal)));

        try
        {
            Xunit.Assert.True(refiner.Entered.Wait(TimeSpan.FromSeconds(15)), "Goal refinement did not reach the blocking provider.");
            var concurrentWriteElapsed = Stopwatch.StartNew();
            await concurrentRepository.TransactAsync(
                (kernel, _) =>
                {
                    kernel.CreateGoal("Concurrent conductor-style state writer");
                    return Task.FromResult((true, true));
                }).WaitAsync(TimeSpan.FromSeconds(15));
            concurrentWriteElapsed.Stop();
            Console.WriteLine(
                $"GOAL_CREATE_LOCK_MEASUREMENT phase=refinement concurrentWriterElapsedMs={concurrentWriteElapsed.ElapsedMilliseconds}");
        }
        finally
        {
            refiner.Release.Set();
        }

        await createTask.WaitAsync(TimeSpan.FromSeconds(15));

        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(2, restored.Goals.Count);
        Xunit.Assert.Contains(restored.Goals, goal => goal.Objective == "Concurrent conductor-style state writer");
        var created = Xunit.Assert.Single(restored.Goals, goal => goal.Objective == "Implement deterministic unlocked goal refinement");
        Xunit.Assert.NotNull(created.RefinedSpec);
        Xunit.Assert.Equal(GoalStatus.Active, created.Status);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_concurrent_goal_creates_have_exactly_one_backlog_winner")]
    public async Task PersistentRunnerConcurrentGoalCreatesHaveExactlyOneBacklogWinner()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("barrier-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Concurrent single-consumer source");
        var firstRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var secondRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var refiner = new BarrierGoalRefinerProvider(expectedCalls: 2);

        Exception? RunCreate(ITransactionalOrchestratorStateRepository repository, string objective)
        {
            IReadOnlyList<AgentDefinition> localAgents = AgentCatalog.Default().Agents;
            var localProfiles = WorkerProfileCatalog.Default();
            Goal? localGoal = null;
            try
            {
                _ = CliPersistentStateRunner.ExecuteCommand(
                    ["goal", objective, "--backlog-item", item.Id, "--backlog-coverage", "slice"],
                    repository,
                    workspace,
                    ref localAgents,
                    new InMemoryModelProviderRegistry([refiner]),
                    ref localProfiles,
                    ref localGoal);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var firstCreate = Task.Run(() => RunCreate(firstRepository, "Concurrent intake candidate one"));
        var secondCreate = Task.Run(() => RunCreate(secondRepository, "Concurrent intake candidate two"));
        try
        {
            Xunit.Assert.True(refiner.AllEntered.WaitOne(TimeSpan.FromSeconds(15)), "Both goal creations did not reach refinement.");
        }
        finally
        {
            refiner.Release.Set();
        }

        var outcomes = await Task.WhenAll(firstCreate, secondCreate).WaitAsync(TimeSpan.FromSeconds(15));
        Xunit.Assert.Single(outcomes, outcome => outcome is null);
        var rejected = Xunit.Assert.Single(outcomes, outcome => outcome is not null)!;
        Xunit.Assert.Contains("GOAL_CREATE_PRECONDITION_CHANGED reason=source-backlog-consumed", rejected.Message);

        var restored = await firstRepository.LoadAsync();
        var winner = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.Equal(item.Id, winner.SourceBacklogItemId);
        Xunit.Assert.NotNull(winner.RefinedSpec);
        Xunit.Assert.Equal(GoalStatus.Active, winner.Status);
        Xunit.Assert.Single(Directory.GetFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl"));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_rejects_competing_backlog_link_atomically")]
    public async Task PersistentRunnerGoalCreateRejectsCompetingBacklogLinkAtomically()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Single-consumer backlog source");
        var repository = new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel());
        repository.BeforeNextTransaction = stored =>
        {
            var competing = stored.CreateGoal("Competing intake winner");
            stored.SetGoalSourceBacklogItemLink(competing.Id, item.Id, SourceBacklogCoverage.Slice);
        };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["goal", "Create one linked goal", "--backlog-item", item.Id, "--backlog-coverage", "slice"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));

        Xunit.Assert.Contains("GOAL_CREATE_PRECONDITION_CHANGED reason=source-backlog-consumed", error.Message);
        Xunit.Assert.Null(currentGoal);
        var restored = await repository.LoadAsync();
        var winner = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.Equal("Competing intake winner", winner.Objective);
        Xunit.Assert.Equal(item.Id, winner.SourceBacklogItemId);
        Xunit.Assert.Empty(await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).ListAsync());
        Xunit.Assert.Empty(Directory.Exists(workspace.GoalLifecycleEventsDirectory)
            ? Directory.GetFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
            : []);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_flushes_deferred_side_effects_after_commit")]
    public async Task PersistentRunnerGoalCreateFlushesDeferredSideEffectsAfterCommit()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var repository = new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel());
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", "Create a goal whose ambiguous API contract needs an operator decision"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = await repository.LoadAsync();
        var created = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.NotNull(created.RefinedSpec);
        Xunit.Assert.True(created.RefinedSpec!.HasOpenQuestions);
        Xunit.Assert.NotEmpty(await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ListAsync(created.Id.Value));
        Xunit.Assert.True(File.Exists(Path.Combine(
            workspace.GoalLifecycleEventsDirectory,
            $"{created.Id.Value}.jsonl")));
        Xunit.Assert.Contains(created.Id.Value[..8], output);
        var planIndex = output.IndexOf("Goal objective plan:", StringComparison.Ordinal);
        var advisoryIndex = output.IndexOf("Scope collision advisory:", StringComparison.Ordinal);
        var goalIndex = output.IndexOf($"Goal {created.Id.Value}", StringComparison.Ordinal);
        Xunit.Assert.True(planIndex >= 0 && advisoryIndex > planIndex && goalIndex > advisoryIndex, output);
        Xunit.Assert.Contains("\"historicalTimeEstimate\"", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"verdict\"", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task GoalCreateForcedFiveRolePersistsExactTaskOrder()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        const string objective = "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs with one focused assertion.";

        Xunit.Assert.NotEqual(
            GoalIntakePipeline.FiveRole,
            GoalObjectivePlanner.Build(objective).PipelineDecision.Pipeline);

        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", objective, "--pipeline", "five-role"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = await CreateMigratedStateRepository(workspace.SqliteStatePath).LoadAsync();
        var persistedGoal = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.Equal(
            [AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            persistedGoal.Tasks.Select(task => task.RequiredRole));
    }

    [Xunit.Fact]
    public async Task GoalCreateUnsatisfiedPipelineLeavesStateAndOutboxUnchanged()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initialKernel = new AgentOrchestratorKernel();
        var existingGoal = initialKernel.CreateGoal(
            "Existing durable goal",
            [new TaskSpec(TaskId.New(), "Keep existing work", AgentRole.Developer)]);
        await repository.SaveAsync(initialKernel);
        var existingMessage = GoalCreationSideEffectDelivery.CreateMessage(existingGoal.Id, [], []);
        await repository.TransactWithOutboxAsync(
            (_, _) => Task.FromResult((
                ShouldSave: false,
                Result: true,
                OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[existingMessage])));
        var beforeKernel = await CreateMigratedStateRepository(workspace.SqliteStatePath).LoadAsync();
        var beforeSnapshot = JsonSerializer.Serialize(beforeKernel.ExportGoalSnapshot(existingGoal.Id));
        var beforeOutbox = await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents
            .Where(agent => agent.Role != AgentRole.Tester)
            .ToArray();
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["goal", "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs", "--pipeline", "five-role"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));

        var reloadedRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var afterKernel = await reloadedRepository.LoadAsync();
        var afterGoal = Xunit.Assert.Single(afterKernel.Goals);
        Xunit.Assert.Equal(beforeSnapshot, JsonSerializer.Serialize(afterKernel.ExportGoalSnapshot(afterGoal.Id)));
        Xunit.Assert.Equal(beforeOutbox, await reloadedRepository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
        Xunit.Assert.Equal(existingGoal.Id, currentGoal!.Id);
        Xunit.Assert.Contains("missing available agent role(s): Tester", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("No goal was created", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_delivery_failure_is_durable_and_retry_only")]
    public async Task PersistentRunnerGoalCreateDeliveryFailureIsDurableAndRetryOnly()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var lifecycleAttempts = 0;
        GoalCreationSideEffectDelivery.BeforeEffectDelivery = deliveryId =>
        {
            if (deliveryId.Contains(":lifecycle:", StringComparison.Ordinal) &&
                Interlocked.Increment(ref lifecycleAttempts) == 2)
            {
                throw new IOException("Injected lifecycle delivery failure.");
            }
        };

        InvalidOperationException error;
        try
        {
            error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    ["goal", "Create a goal with durably retryable clarification delivery"],
                    repository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeEffectDelivery = null;
        }

        var committedKernel = await repository.LoadAsync();
        var created = Xunit.Assert.Single(committedKernel.Goals);
        Xunit.Assert.Contains("GOAL_CREATE_DELIVERY_INCOMPLETE", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"goal-delivery-retry {created.Id.Value}", error.Message, StringComparison.Ordinal);
        Xunit.Assert.True(lifecycleAttempts >= 2);
        Xunit.Assert.Single(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));

        CreateVersion7StateOutboxFixture(workspace.SqliteStatePath);
        Xunit.Assert.False(StateDbMigrations.IsUpToDate(workspace.SqliteStatePath));
        var deliveryError = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["goal-delivery-retry", created.Id.Value],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));
        var missingColumn = Xunit.Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(deliveryError.InnerException);
        Xunit.Assert.Contains("no such column: quarantined_at", missingColumn.Message, StringComparison.Ordinal);
        ProgramStartupLifecycle.EnsureStateDbInitialized(["conduct", "--loop"], workspace);
        Xunit.Assert.True(StateDbMigrations.IsUpToDate(workspace.SqliteStatePath));

        var collaborationBeforeRetry = await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ListAsync(created.Id.Value);
        Xunit.Assert.NotEmpty(collaborationBeforeRetry);
        var eventPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{created.Id.Value}.jsonl");
        var eventCountBeforeRetry = File.ReadAllLines(eventPath).Length;
        Xunit.Assert.True(eventCountBeforeRetry >= 1);

        var retryOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal-delivery-retry", created.Id.Value],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("GOAL_CREATE_DELIVERY_COMPLETE", retryOutput, StringComparison.Ordinal);
        Xunit.Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
        Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        var collaborationAfterRetry = await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ListAsync(created.Id.Value);
        Xunit.Assert.Equal(
            collaborationAfterRetry.Count,
            collaborationAfterRetry.Select(item => item.CorrelationKey).Distinct(StringComparer.Ordinal).Count());
        Xunit.Assert.Equal(collaborationBeforeRetry.Count, collaborationAfterRetry.Count);

        var deliveredEvents = File.ReadAllLines(eventPath)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToArray();
        Xunit.Assert.True(deliveredEvents.Length > eventCountBeforeRetry);
        var deliveryIds = deliveredEvents
            .Select(evt => evt["deliveryId"]?.GetValue<string>())
            .Where(id => id is not null)
            .ToArray();
        Xunit.Assert.Equal(deliveryIds.Length, deliveryIds.Distinct(StringComparer.Ordinal).Count());

        var eventCountAfterRetry = deliveredEvents.Length;
        var repeatedRetryOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal-delivery-retry", created.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        Xunit.Assert.Contains("disposition=alreadydelivered", repeatedRetryOutput, StringComparison.Ordinal);
        Xunit.Assert.Equal(eventCountAfterRetry, File.ReadAllLines(eventPath).Length);
        Xunit.Assert.Single((await repository.LoadAsync()).Goals);
    }

    [Xunit.Theory(DisplayName = "CliPersistentStateRunner_goal_create_delivery_releases_state_writer_during_external_io")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task PersistentRunnerGoalCreateDeliveryReleasesStateWriterDuringExternalIo(bool retry)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var concurrentRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        if (retry)
        {
            GoalCreationSideEffectDelivery.BeforeEffectDelivery = _ =>
                throw new IOException("Injected initial delivery failure.");
            try
            {
                var error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                    CliPersistentStateRunner.ExecuteCommand(
                        ["goal", "Create a goal whose delivery will be retried outside the state transaction"],
                        repository,
                        workspace,
                        ref agents,
                        providers,
                        ref profiles,
                        ref currentGoal)));
                Xunit.Assert.Contains("GOAL_CREATE_DELIVERY_INCOMPLETE", error.Message, StringComparison.Ordinal);
            }
            finally
            {
                GoalCreationSideEffectDelivery.BeforeEffectDelivery = null;
            }

            Xunit.Assert.Single(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
        }

        using var deliveryEntered = new ManualResetEventSlim();
        using var releaseDelivery = new ManualResetEventSlim();
        var deliveryCalls = 0;
        GoalCreationSideEffectDelivery.BeforeEffectDelivery = _ =>
        {
            if (Interlocked.Increment(ref deliveryCalls) != 1)
                return;

            deliveryEntered.Set();
            Xunit.Assert.True(
                releaseDelivery.Wait(TimeSpan.FromSeconds(15)),
                "Timed out waiting to release the external goal-creation delivery.");
        };

        var deliveryTask = Task.Run(() => CaptureConsole(() =>
        {
            if (retry)
            {
                var created = Xunit.Assert.Single(repository.LoadAsync().GetAwaiter().GetResult().Goals);
                _ = CliPersistentStateRunner.ExecuteCommand(
                    ["goal-delivery-retry", created.Id.Value],
                    repository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
            }
            else
            {
                _ = CliPersistentStateRunner.ExecuteCommand(
                    ["goal", "Create a goal whose initial delivery runs outside the state transaction"],
                    repository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
            }
        }));

        try
        {
            Xunit.Assert.True(
                deliveryEntered.Wait(TimeSpan.FromSeconds(15)),
                "Goal-creation delivery did not reach the blocking external effect.");
            await concurrentRepository.TransactAsync(
                (kernel, _) =>
                {
                    kernel.CreateGoal("Concurrent writer during goal-creation delivery");
                    return Task.FromResult((true, true));
                }).WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            releaseDelivery.Set();
            GoalCreationSideEffectDelivery.BeforeEffectDelivery = null;
        }

        await deliveryTask.WaitAsync(TimeSpan.FromSeconds(15));
        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(2, restored.Goals.Count);
        Xunit.Assert.Contains(restored.Goals, goal => goal.Objective == "Concurrent writer during goal-creation delivery");
        Xunit.Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));

        var createdGoal = Xunit.Assert.Single(restored.Goals, goal =>
            goal.Objective != "Concurrent writer during goal-creation delivery");
        var collaborationItems = await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ListAsync(createdGoal.Id.Value);
        Xunit.Assert.Equal(
            collaborationItems.Count,
            collaborationItems.Select(item => item.CorrelationKey).Distinct(StringComparer.Ordinal).Count());
        var eventPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{createdGoal.Id.Value}.jsonl");
        var deliveryIds = File.ReadLines(eventPath)
            .Select(line => JsonNode.Parse(line)!["deliveryId"]?.GetValue<string>())
            .Where(id => id is not null)
            .ToArray();
        Xunit.Assert.Equal(deliveryIds.Length, deliveryIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_analysis_failure_leaves_no_state_or_delivery_trace")]
    public async Task PersistentRunnerGoalCreateAnalysisFailureLeavesNoStateOrDeliveryTrace()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
            throw new InvalidOperationException("Injected pre-commit analysis failure.");
        InvalidOperationException error;
        try
        {
            error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    ["goal", "Fail after unlocked analysis and before state commit"],
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]),
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }

        Xunit.Assert.Contains("Injected pre-commit analysis failure", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty((await repository.LoadAsync()).Goals);
        Xunit.Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
        Xunit.Assert.Empty(await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).ListAsync());
        Xunit.Assert.Empty(Directory.Exists(workspace.GoalLifecycleEventsDirectory)
            ? Directory.GetFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
            : []);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_rejects_backlog_dependency_change_at_commit")]
    public async Task PersistentRunnerGoalCreateRejectsBacklogDependencyChangeAtCommit()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var kernel = new AgentOrchestratorKernel();
        var dependencyGoal = kernel.CreateGoal("Existing prerequisite goal");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var source = await backlog.AddAsync("Dependent goal source");
        _ = await backlog.AddDependencyAsync(
            source.Id,
            new BacklogDependencyTarget(dependencyGoal.Id.Value, BacklogDependencyTargetKind.Goal),
            goalExists: id => id == dependencyGoal.Id.Value);
        repository.BeforeNextTransaction = _ =>
            backlog.RemoveDependencyAsync(source.Id, dependencyGoal.Id.Value).GetAwaiter().GetResult();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["goal", "Create dependent goal", "--backlog-item", source.Id, "--backlog-coverage", "slice"],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]),
                ref profiles,
                ref currentGoal)));

        Xunit.Assert.Contains("GOAL_CREATE_PRECONDITION_CHANGED reason=source-backlog-dependencies-changed", error.Message, StringComparison.Ordinal);
        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(dependencyGoal.Id, Xunit.Assert.Single(restored.Goals).Id);
        Xunit.Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_rejects_missing_dependency_target_at_commit")]
    public async Task PersistentRunnerGoalCreateRejectsMissingDependencyTargetAtCommit()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var kernel = new AgentOrchestratorKernel();
        var dependencyGoal = kernel.CreateGoal("Prerequisite removed during analysis");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var source = await backlog.AddAsync("Source whose dependency target disappears");
        _ = await backlog.AddDependencyAsync(
            source.Id,
            new BacklogDependencyTarget(dependencyGoal.Id.Value, BacklogDependencyTargetKind.Goal),
            goalExists: id => id == dependencyGoal.Id.Value);
        repository.BeforeNextTransaction = _ =>
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.BacklogStorePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE backlog_dependencies SET prerequisite_id = $missing WHERE dependent_id = $source";
            command.Parameters.AddWithValue("$missing", GoalId.New().Value);
            command.Parameters.AddWithValue("$source", source.Id);
            Xunit.Assert.Equal(1, command.ExecuteNonQuery());
        };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["goal", "Reject missing dependency target", "--backlog-item", source.Id, "--backlog-coverage", "slice"],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]),
                ref profiles,
                ref currentGoal)));

        Xunit.Assert.Contains("GOAL_CREATE_PRECONDITION_CHANGED reason=dependency-target-missing", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(dependencyGoal.Id, Xunit.Assert.Single((await repository.LoadAsync()).Goals).Id);
        Xunit.Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_keeps_overlap_detected_advisory_nonblocking")]
    public async Task PersistentRunnerGoalCreateKeepsOverlapDetectedAdvisoryNonblocking()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var kernel = new AgentOrchestratorKernel();
        _ = kernel.CreateGoal($"Existing work\n\n{BacklogIntakePlanner.TargetScopeHeadingLine}\n- src/Feature/File.cs");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", $"Change shared feature\n\n{BacklogIntakePlanner.TargetScopeHeadingLine}\n- src/Feature/File.cs"],
            repository,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]),
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("overlap-detected", output, StringComparison.Ordinal);
        Xunit.Assert.Equal(2, (await repository.LoadAsync()).Goals.Count);
        Xunit.Assert.NotNull(currentGoal);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_preserves_precommit_stdout_bytes")]
    public void PersistentRunnerGoalCreatePreservesPrecommitStdoutBytes()
    {
        const string objective = "Create deterministic goal output for stdout compatibility";

        (string Output, Goal Goal) Run(bool persistent)
        {
            var root = CreateTempDirectory();
            var workspace = CreateRefinedWorkspace(root);
            ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
                new ModelFunctionBinding(
                    ModelFunctionPurposes.SpecRefiner,
                    ModelLane.CheapApi,
                    new ModelProfile("deterministic-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                    Name: ModelFunctionPurposes.SpecRefiner)
            ]));
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            var providers = new InMemoryModelProviderRegistry([new DeterministicGoalRefinerProvider()]);
            Goal? currentGoal = null;
            var output = persistent
                ? CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                    ["goal", objective],
                    new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel()),
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal))
                : CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                    ["goal", objective],
                    new AgentOrchestratorKernel(),
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal));
            return (output, Xunit.Assert.IsType<Goal>(currentGoal));
        }

        static string PrecommitOutput((string Output, Goal Goal) result)
        {
            var goalOutput = $"Goal {result.Goal.Id.Value}";
            var goalIndex = result.Output.IndexOf(goalOutput, StringComparison.Ordinal);
            Xunit.Assert.True(goalIndex > 0, result.Output);
            return result.Output[..goalIndex];
        }

        var ambientTransactionBaseline = Run(persistent: false);
        var outsideTransactionCandidate = Run(persistent: true);
        Xunit.Assert.Equal(
            PrecommitOutput(ambientTransactionBaseline),
            PrecommitOutput(outsideTransactionCandidate));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_rejects_missing_backlog_source_without_side_effects")]
    public async Task PersistentRunnerGoalCreateRejectsMissingBacklogSourceWithoutSideEffects()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Backlog source removed during refinement");
        var repository = new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel());
        repository.BeforeNextTransaction = _ =>
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.BacklogStorePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM backlog WHERE id = $id";
            command.Parameters.AddWithValue("$id", item.Id);
            Xunit.Assert.Equal(1, command.ExecuteNonQuery());
        };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["goal", "Reject a stale missing backlog source", "--backlog-item", item.Id, "--backlog-coverage", "slice"],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]),
                ref profiles,
                ref currentGoal)));

        Xunit.Assert.Contains("GOAL_CREATE_PRECONDITION_CHANGED reason=source-backlog-missing", error.Message);
        Xunit.Assert.Empty((await repository.LoadAsync()).Goals);
        Xunit.Assert.Empty(await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).ListAsync());
        Xunit.Assert.Empty(Directory.Exists(workspace.GoalLifecycleEventsDirectory)
            ? Directory.GetFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
            : []);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_routes_single_goal_conduct_outside_command_transaction")]
    public void RunnerRoutesSingleGoalConductOutsideCommandTransaction()
    {
        Xunit.Assert.True(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "abc123"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsSingleGoalConductCommand(["CONDUCT", "abc123", "--policy", "Permissive"]));

        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "--loop"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "abc123", "--watch"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "--help"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["next"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand([]));
    }


    [Xunit.Theory(DisplayName = "Cli_dispatcher_help_prints_command_specific_usage")]
    [Xunit.InlineData(new[] { "conduct", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "-h" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "--loop", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "goals", "subscribe", "--help" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "help", "goals", "subscribe" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "reassign-agent", "--help" }, CliCommandHelp.ReassignAgentUsage)]
    [Xunit.InlineData(new[] { "workspace", "--help" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "-h" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "create", "-h" }, CliCommandHelp.WorkspaceCreateUsage)]
    public void CliDispatcherHelpPrintsCommandSpecificUsage(string[] args, string expectedUsage)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                args,
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains(expectedUsage, output);
    }


    [Xunit.Theory(DisplayName = "Cli_startup_help_exits_before_state_repository_creation")]
    [Xunit.InlineData(new[] { "conduct", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "-h" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "--loop", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "goals", "subscribe", "--help" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "help", "goals", "subscribe" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "reassign-agent", "--help" }, CliCommandHelp.ReassignAgentUsage)]
    [Xunit.InlineData(new[] { "workspace", "--help" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "-h" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "create", "-h" }, CliCommandHelp.WorkspaceCreateUsage)]
    public void CliStartupHelpExitsBeforeStateRepositoryCreation(string[] args, string expectedUsage)
    {
        var root = CreateTempDirectory();
        var result = RunAppCli(root, args);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains(expectedUsage, result.StandardOutput);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.StandardError), result.StandardError);
        Xunit.Assert.False(File.Exists(Path.Combine(root, ".orchestrator", "state.db")));
        Xunit.Assert.False(Directory.Exists(Path.Combine(root, ".orchestrator")));
    }


    [Xunit.Fact(DisplayName = "Cli_startup_short_read_command_bootstraps_fresh_state_and_releases_it")]
    public async Task CliStartupShortReadCommandExitsWithinTwoSecondsAndReleasesState()
    {
        var root = CreateTempDirectory();

        var result = await RunAppCliWithExitTimeout(root, ["next", "--full"], TimeSpan.FromSeconds(60));

        Xunit.Assert.True(
            result.ExitedWithinTimeout,
            $"CLI did not exit within the 60 second hang guard. stdout: {result.StandardOutput} stderr: {result.StandardError}");
        Xunit.Assert.Equal(1, result.ExitCode);
        Xunit.Assert.Contains("Create a goal first", result.StandardError);

        var statePath = Path.Combine(root, ".orchestrator", "state.db");
        Xunit.Assert.True(File.Exists(statePath));
        Xunit.Assert.True(StateDbMigrations.IsUpToDate(statePath));
        using var stateLockProbe = File.Open(statePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Xunit.Assert.True(stateLockProbe.CanWrite);
    }

    [Xunit.Fact(DisplayName = "Cli_startup_read_command_bootstraps_unmigrated_non_wal_state")]
    public void CliStartupReadCommandBootstrapsUnmigratedNonWalState()
    {
        var root = CreateTempDirectory();
        var orchestratorDirectory = Path.Combine(root, ".orchestrator");
        var statePath = Path.Combine(orchestratorDirectory, "state.db");
        Directory.CreateDirectory(orchestratorDirectory);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={statePath}"))
        {
            connection.Open();
        }
        Xunit.Assert.False(StateDbMigrations.IsUpToDate(statePath));

        var result = RunAppCli(root, ["goals"]);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.True(StateDbMigrations.IsUpToDate(statePath));
        using var migrated = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={statePath};Mode=ReadOnly;Pooling=False");
        migrated.Open();
        using var journalMode = migrated.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode";
        Xunit.Assert.Equal(
            "wal",
            Convert.ToString(journalMode.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Xunit.Fact]
    public void CliStartupReadCommandDoesNotMigratePublishedVersion7State()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        CreateVersion7StateOutboxFixture(workspace.SqliteStatePath);

        var result = RunAppCli(root, ["goals"]);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.False(StateDbMigrations.IsUpToDate(workspace.SqliteStatePath));
        using (var unchanged = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={workspace.SqliteStatePath};Mode=ReadOnly;Pooling=False"))
        {
            unchanged.Open();
            using var columns = unchanged.CreateCommand();
            columns.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_table_info('state_outbox') ORDER BY cid)";
            Xunit.Assert.Equal(
                "id,kind,payload_json,created_at",
                Convert.ToString(columns.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            using var migrationCount = unchanged.CreateCommand();
            migrationCount.CommandText = "SELECT COUNT(*) FROM schema_migrations";
            Xunit.Assert.Equal(7L, migrationCount.ExecuteScalar());
        }

        ProgramStartupLifecycle.EnsureStateDbInitialized(["conduct", "--loop"], workspace);

        Xunit.Assert.True(StateDbMigrations.IsUpToDate(workspace.SqliteStatePath));
    }


    [Xunit.Fact(DisplayName = "ConsoleViews_PrintGoals_renders_metadata_summaries")]
    public void PrintGoalsRendersMetadataSummaries()
    {
        IReadOnlyList<GoalSummary> summaries =
        [
            new GoalSummary("0123456789abcdef0123456789abcdef", "Active", "Build the widget", "2026-06-15T00:00:00.0000000+00:00"),
            new GoalSummary("fedcba98", "Completed", "Ship the gadget", "2026-06-14T00:00:00.0000000+00:00"),
            new GoalSummary(
                "badc0ffe",
                "Active",
                "Recover failed work",
                "2026-06-13T00:00:00.0000000+00:00",
                Condition: GoalLifecycle.ActiveWithFailedTaskCondition)
        ];

        var output = CaptureConsole(() => ConsoleViews.PrintGoals(summaries));

        Xunit.Assert.Contains("01234567 Active: Build the widget", output);
        Xunit.Assert.Contains("fedcba98 Completed: Ship the gadget", output);
        Xunit.Assert.Contains("badc0ffe Active [active-with-failed-task]: Recover failed work", output);
    }

    [Xunit.Fact(DisplayName = "ConsoleViews_PrintGoal_renders_effective_acceptance_criteria_corrections")]
    public void PrintGoalRendersEffectiveAcceptanceCriteriaCorrections()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Render correction overlay");
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        kernel.RecordOperatorTaskNote(
            goal.Id,
            task.Id,
            "CRITERIA CORRECTION: supersedes=\"full suite required\"; correction=\"focused build-check accepted\"");

        var output = CaptureConsole(() => ConsoleViews.PrintGoal(goal));

        Xunit.Assert.Contains("Effective acceptance criteria corrections:", output);
        Xunit.Assert.Contains("supersedes: full suite required", output);
        Xunit.Assert.Contains("correction: focused build-check accepted", output);
        Xunit.Assert.Contains("provenance: operator", output);
    }

    [Xunit.Fact]
    public async Task GoalAmendWaivePersistsBriefAndAuditEvent()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement the slice", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Review the slice", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Recover acceptance scope", [developer, reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Ship recoverable acceptance scope",
            ["focused tests pass", "  measure unavailable makespan  "],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repository.SaveAsync(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var reasonPath = Path.Combine(root, "waiver-reason.txt");
        await File.WriteAllTextAsync(reasonPath, "requires conductor evidence:\r\nno worker substitute is acceptable");

        var output = CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                CliArgumentParser.SplitCommand(
                    $"goal-amend {goal.Id.Value[..8]} --waive 2 --reason-file {reasonPath} --actor miles"),
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
        var waiver = Xunit.Assert.Single(restoredGoal.EffectiveAcceptanceCriteriaCorrections);
        var brief = restored.BuildTaskBrief(goal.Id, reviewer.Id).Content;
        var eventPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
        var auditLine = File.ReadLines(eventPath).Single(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("eventType").GetString() == "AcceptanceCriterionWaived";
        });
        using var auditEvent = JsonDocument.Parse(auditLine);

        Xunit.Assert.Contains("Acceptance criterion waived", output);
        Xunit.Assert.Contains("criterion=2", output);
        Xunit.Assert.Equal("measure unavailable makespan", waiver.SupersededCriterion);
        Xunit.Assert.Equal("requires conductor evidence: no worker substitute is acceptable", waiver.WaiverReason);
        Xunit.Assert.Contains("- [WAIVED] measure unavailable makespan", brief);
        Xunit.Assert.Contains("Reason: requires conductor evidence: no worker substitute is acceptable", brief);
        Xunit.Assert.Equal("measure unavailable makespan", auditEvent.RootElement.GetProperty("criterion").GetString());
        Xunit.Assert.Equal("miles", auditEvent.RootElement.GetProperty("actor").GetString());
        Xunit.Assert.Equal("requires conductor evidence: no worker substitute is acceptable", auditEvent.RootElement.GetProperty("reason").GetString());
        Xunit.Assert.True(auditEvent.RootElement.TryGetProperty("recordedAt", out _));
        Xunit.Assert.Equal(waiver.CapturedAcceptanceCriteriaHash, auditEvent.RootElement.GetProperty("capturedAcceptanceCriteriaHash").GetString());
        Xunit.Assert.All(restoredGoal.Tasks, task => Xunit.Assert.Null(task.LastDispatch));
    }

    [Xunit.Fact]
    public async Task OperatorCommandsCreateAndExplicitlySatisfyStructuredGates()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Exercise operator gate commands");
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.First(candidate => candidate.RequiredRole == AgentRole.Developer);
            var request = kernel.RequestHumanInput(goal.Id, task.Id, "Should console suppression ship?");
            var repository = new InMemoryTransactionalStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            Xunit.Assert.True(CliPersistentStateRunner.ExecuteCommand(
                CliArgumentParser.SplitCommand(
                    $"note {goal.Id.Value[..8]} 3 Gate the correlation work --gate-deliverable correlation-evidence"),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
            Xunit.Assert.True(CliPersistentStateRunner.ExecuteCommand(
                CliArgumentParser.SplitCommand(
                    $"answer {request.Id.Value[..8]} Wait for confirmation --gate-deliverable hidden-console-spawn"),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
            var noteGateSource = currentGoal!.Timeline
                .Single(evt => evt.Kind == ProgressKind.OperatorTaskNote && evt.Message == "Gate the correlation work")
                .OperatorGates!
                .Single()
                .SourceRecordId;
            Xunit.Assert.True(CliPersistentStateRunner.ExecuteCommand(
                CliArgumentParser.SplitCommand(
                    $"gate-satisfied {noteGateSource} correlation-evidence Operator confirmed the correlation"),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
            Xunit.Assert.True(CliPersistentStateRunner.ExecuteCommand(
                CliArgumentParser.SplitCommand(
                    $"gate-satisfied {request.Id.Value[..8]} hidden-console-spawn Operator confirmed the observation"),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            var restored = await repository.LoadAsync();
            var restoredGoal = restored.GetGoal(goal.Id);
            var taskNote = restoredGoal.Timeline.Single(evt =>
                evt.Kind == ProgressKind.OperatorTaskNote && evt.Message == "Gate the correlation work");
            Xunit.Assert.Contains(taskNote.OperatorGates!, gate => gate.DeliverableId == "correlation-evidence");
            var clarificationGate = Xunit.Assert.Single(restored.GetHumanInputRequest(request.Id).OperatorGates);
            Xunit.Assert.False(clarificationGate.IsActive);
            Xunit.Assert.Equal("Operator confirmed the observation", clarificationGate.SatisfactionEvidence);
            var satisfactionEvents = restoredGoal.Timeline.Where(evt => evt.Kind == ProgressKind.OperatorGateSatisfied).ToArray();
            Xunit.Assert.Equal(2, satisfactionEvents.Length);
            Xunit.Assert.Contains(satisfactionEvents.SelectMany(evt => evt.OperatorGates ?? []), gate =>
                gate.SourceRecordId == noteGateSource &&
                !gate.IsActive &&
                gate.SatisfactionEvidence == "Operator confirmed the correlation");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_ignores_volatile_snapshot_churn")]
    public void PersistentRunnerAcceptanceIgnoresVolatileSnapshotChurn()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Land despite volatile metadata churn", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
        var worktree = CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var fullSnapshotChanged = false;
        var stableProjectionChanged = true;
        repository.BeforeNextTransaction = stored =>
        {
            var before = stored.ExportSnapshot().Goals.Single(candidate => candidate.Id == goal.Id.Value);
            stored.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed again.", root, DateTimeOffset.Parse("2026-06-25T15:01:00Z")));
            var after = stored.ExportSnapshot().Goals.Single(candidate => candidate.Id == goal.Id.Value);
            fullSnapshotChanged = JsonSerializer.Serialize(before) != JsonSerializer.Serialize(after);
            stableProjectionChanged = BuildTaskStatusProjectionJson(before) != BuildTaskStatusProjectionJson(after);
        };

        CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["acceptance", "--skip-verify", "--keep-workspace"],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.True(fullSnapshotChanged);
        Xunit.Assert.False(stableProjectionChanged);
        Xunit.Assert.Equal("main", RunGitOutput(root, "branch", "--show-current").Trim());
        Xunit.Assert.Equal("goal work", File.ReadAllText(Path.Combine(root, "feature.txt")));
        Xunit.Assert.True(Directory.Exists(worktree));
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_verifier_runs_outside_state_write_transaction")]
    public void PersistentRunnerAcceptanceVerifierRunsOutsideStateWriteTransaction()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Verify outside transaction", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var verifierObservedUnlockedState = false;
        var verifier = new ProbeAcceptanceVerifier(() =>
        {
            Xunit.Assert.False(repository.IsInTransaction);
            verifierObservedUnlockedState = true;
        });

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["acceptance", "--keep-workspace"],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: verifier));

        Xunit.Assert.True(verifierObservedUnlockedState);
        Xunit.Assert.Equal(1, verifier.RunCount);
        Xunit.Assert.Equal(2, repository.TransactionCount);
        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Equal(1, repository.LoadGoalCount);
        Xunit.Assert.Equal(2, repository.LoadedGoalIds.Count(id => id == goal.Id.Value));
        Xunit.Assert.False(repository.IsInTransaction);
        Xunit.Assert.Equal("goal work", File.ReadAllText(Path.Combine(root, "feature.txt")));
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=startup-goal-resolve", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=startup-load-target-goal", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=reconcile-sweep", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=workspace-rebase", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=verification-suite", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=verification-check", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=workspace-merge", output);
        Xunit.Assert.Matches(@"PHASE_TIMING command=acceptance phase=verification-check elapsedMs=\d+ .*name=""probe verifier""", output);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_target_scoped_reconcile_preserves_target_dispatch_refresh")]
    public async Task PersistentRunnerAcceptanceTargetScopedReconcilePreservesTargetDispatchRefresh()
    {
        var root = CreateShortAcceptanceRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var currentTask = new TaskSpec(TaskId.New(), "Current work", AgentRole.Developer);
            var targetTask = new TaskSpec(TaskId.New(), "Target work", AgentRole.Developer);
            var current = kernel.CreateGoal("Current acceptance context", [currentTask]);
            var target = kernel.CreateGoal("Target acceptance context", [targetTask]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = current;
            kernel.ActivateGoal(current.Id, agents);
            kernel.ActivateGoal(target.Id, agents);
            RecordRunningProcess(kernel, target, targetTask, root);
            File.WriteAllText(targetTask.LastProcess!.ExitCodePath, "0");
            File.WriteAllText(targetTask.LastProcess.StandardOutputPath, "done");
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["acceptance", target.Id.Value[..8], "--skip-verify", "--keep-workspace", "--no-record"],
                repository,
                CreateRefinedWorkspace(root),
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=reconcile-sweep", output);
            Xunit.Assert.Contains("mode=target-scoped-fast-path", output);
            Xunit.Assert.Contains("goalsWalked=1", output);
            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Contains(target.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.DoesNotContain(current.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.Equal(target.Id, currentGoal!.Id);

            var currentSnapshot = await repository.LoadGoalAsync(current.Id);
            var targetSnapshot = await repository.LoadGoalAsync(target.Id);
            var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([currentSnapshot!, targetSnapshot!], []));
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(current.Id, currentTask.Id).Status);
            var restoredTargetTask = restored.GetTask(target.Id, targetTask.Id);
            Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTargetTask.Status);
            Xunit.Assert.Equal(0, restoredTargetTask.LastProcess!.ExitCode);
            Xunit.Assert.NotNull(restoredTargetTask.LastVerification);
        }
        finally
        {
            CleanupAcceptanceRepository(root, null);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_final_state_is_persisted_by_goal_cas")]
    public async Task PersistentRunnerAcceptanceFinalStateIsPersistedByGoalCas()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
            var goal = kernel.CreateGoal("Persist acceptance final state with CAS", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
            kernel.RecordAcceptanceFailure(goal.Id, ["previous acceptance failure"]);
            CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var changed = false;
            var output = CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
                ["acceptance", "--skip-verify", "--keep-workspace"],
                repository,
                CreateRefinedWorkspace(root),
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Equal(2, repository.TransactionCount);
            Xunit.Assert.Equal(2, repository.SaveGoalSnapshotsCount);
            Xunit.Assert.False(changed);
            Xunit.Assert.Contains("Acceptance evidence bundle: passed", output);
            Xunit.Assert.Contains($"Goal {goal.Id.Value[..8]} acceptance: accepted", output);
            var storedGoal = (await repository.LoadAsync()).GetGoal(goal.Id);
            Xunit.Assert.Null(storedGoal.LatestAcceptanceFailure);
            Xunit.Assert.Equal("goal work", File.ReadAllText(Path.Combine(root, "feature.txt")));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_journals_backlog_close_noop_and_persists_missing_warning")]
    public async Task PersistentRunnerAcceptanceJournalsBacklogCloseNoopAndPersistsMissingWarning()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var store = new BacklogStore(workspace.BacklogStorePath);
            var closedItem = await store.AddAsync("Already closed linked item");
            await store.CloseAsync(closedItem.Id);

            var closedKernel = new AgentOrchestratorKernel();
            var closedTask = new TaskSpec(TaskId.New(), "Implement closed-item landing", AgentRole.Developer);
            var closedGoal = closedKernel.CreateGoal("Land with already closed source item", [closedTask]);
            cleanupGoalId = closedGoal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = closedGoal;
            closedKernel.ActivateGoal(closedGoal.Id, agents);
            closedKernel.RecordTaskVerification(
                closedGoal.Id,
                closedTask.Id,
                ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
            closedKernel.SetGoalSourceBacklogItemId(closedGoal.Id, closedItem.Id);
            CommitGoalWork(root, closedGoal.Id, "closed-item.txt", "goal work");
            var closedRepository = new InMemoryTransactionalStateRepository(closedKernel);

            var closedOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["acceptance", "--skip-verify", "--keep-workspace", "--no-record"],
                closedRepository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("already closed", closedOutput, StringComparison.OrdinalIgnoreCase);
            var closeJournal = GoalOperationJournal.Read(root, closedGoal.Id);
            Xunit.Assert.Contains(closeJournal.Entries, entry =>
                entry.Operation == "conductor:backlog-close" &&
                entry.Status == GoalOperationStatus.Completed &&
                entry.Detail.Contains("No linked source backlog item closed.", StringComparison.Ordinal));

            CleanupAcceptanceRepository(root, cleanupGoalId);
            cleanupGoalId = null;
            root = CreateShortAcceptanceRepository();
            workspace = CreateRefinedWorkspace(root);

            var missingKernel = new AgentOrchestratorKernel();
            var missingTask = new TaskSpec(TaskId.New(), "Implement missing-item landing", AgentRole.Developer);
            var missingGoal = missingKernel.CreateGoal("Land with missing source item", [missingTask]);
            cleanupGoalId = missingGoal.Id;
            agents = AgentCatalog.Default().Agents;
            profiles = WorkerProfileCatalog.Default();
            currentGoal = missingGoal;
            missingKernel.ActivateGoal(missingGoal.Id, agents);
            missingKernel.RecordTaskVerification(
                missingGoal.Id,
                missingTask.Id,
                ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
            missingKernel.SetGoalSourceBacklogItemId(missingGoal.Id, "deadbeefdeadbeefdeadbeefdeadbeef");
            CommitGoalWork(root, missingGoal.Id, "missing-item.txt", "goal work");
            var missingRepository = new InMemoryTransactionalStateRepository(missingKernel);

            var missingOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["acceptance", "--skip-verify", "--keep-workspace", "--no-record"],
                missingRepository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("not found", missingOutput, StringComparison.OrdinalIgnoreCase);
            var restoredGoal = (await missingRepository.LoadAsync()).GetGoal(missingGoal.Id);
            Xunit.Assert.Contains(restoredGoal.Timeline, entry =>
                entry.Message.Contains("linked backlog item deadbeefdeadbeefdeadbeefdeadbeef was not found", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_guard_status_demotion_aborts_without_failure_history")]
    public void PersistentRunnerAcceptanceGuardStatusDemotionAbortsWithoutFailureHistory()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Block stale task state", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        repository.BeforeNextTransaction = stored =>
            stored.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Task moved during acceptance.");

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["acceptance", "--skip-verify", "--keep-workspace"],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("BLOCKER step=acceptance-state-guard", output);
        Xunit.Assert.Contains("Goal.Status", output);
        Xunit.Assert.Contains("expected Verified, actual Active", output);
        Xunit.Assert.Contains("result=aborted", output);
        Xunit.Assert.Contains("No passing gate receipt was recorded", output);
        Xunit.Assert.DoesNotContain("passing gate receipt remains recorded", output, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.DoesNotContain("retry acceptance", output, StringComparison.OrdinalIgnoreCase);
        var storedGoal = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
        Xunit.Assert.Null(storedGoal.LatestAcceptanceFailure);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        var abort = Xunit.Assert.Single(journal.Entries.Where(entry => entry.AcceptanceOutcome == "aborted:state-guard"));
        Xunit.Assert.Equal(GoalOperationStatus.Aborted, abort.Status);
        Xunit.Assert.DoesNotContain(journal.Entries, entry => entry.AcceptanceOutcome == "failed");
        Xunit.Assert.Equal("main", RunGitOutput(root, "branch", "--show-current").Trim());
        Xunit.Assert.False(File.Exists(Path.Combine(root, "feature.txt")));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_guard_task_projection_aborts_without_failure_history")]
    public void PersistentRunnerAcceptanceGuardTaskProjectionAbortsWithoutFailureHistory()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Block stale task projection", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var verifier = new ProbeAcceptanceVerifier(() => { });
        repository.BeforeNextTransaction = stored =>
        {
            stored.BeginGoalAcceptanceVerification(goal.Id, "Arrange a task-set-only mismatch.");
            stored.AddTask(goal.Id, AgentRole.Tester, "Concurrent task");
            stored.ReconcileGoalAcceptanceVerified(goal.Id, "Keep the goal status Verified so the task field is reported.");
        };

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["acceptance", "--keep-workspace"],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: verifier));

        Xunit.Assert.Equal(1, verifier.RunCount);
        Xunit.Assert.Contains("Tasks added/removed", output);
        Xunit.Assert.Contains("result=aborted", output);
        Xunit.Assert.Contains("passing gate receipt remains recorded", output, StringComparison.OrdinalIgnoreCase);
        var restored = repository.LoadAsync().GetAwaiter().GetResult();
        Xunit.Assert.Equal(GoalStatus.Verified, restored.GetGoal(goal.Id).Status);
        Xunit.Assert.Null(restored.GetGoal(goal.Id).LatestAcceptanceFailure);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        Xunit.Assert.Contains(journal.Entries, entry => entry.AcceptanceOutcome == "gate-passed");
        Xunit.Assert.Contains(journal.Entries, entry =>
            entry.AcceptanceOutcome == "aborted:state-guard" && entry.Status == GoalOperationStatus.Aborted);
        Xunit.Assert.DoesNotContain(journal.Entries, entry => entry.AcceptanceOutcome == "failed");
        var acceptance = GoalAcceptanceStatusProjector.Build(restored, restored.GetGoal(goal.Id), root);
        Xunit.Assert.Contains(acceptance.Blockers, blocker => blocker.Kind == GoalAcceptanceBlockerKind.AcceptanceAborted);
        Xunit.Assert.DoesNotContain(acceptance.Blockers, blocker => blocker.Kind == GoalAcceptanceBlockerKind.AcceptanceFailed);
        Xunit.Assert.Equal("main", RunGitOutput(root, "branch", "--show-current").Trim());
        Xunit.Assert.False(File.Exists(Path.Combine(root, "feature.txt")));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_guard_preflight_skips_slot_and_verifier")]
    public void PersistentRunnerAcceptanceGuardPreflightSkipsSlotAndVerifier()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Abort before an expensive gate", [task]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        kernel.RecordAcceptanceFailure(goal.Id, ["superseded failure"], "old-candidate", "old-main");
        var verifier = new ProbeAcceptanceVerifier(() => throw new InvalidOperationException("Verifier must not run after a preflight mismatch."));
        var repository = new InMemoryTransactionalStateRepository(kernel)
        {
            BeforeNextLoadGoalAsync = stored =>
                stored.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Concurrent conductor mutation.")
        };
        IReadOnlyList<AgentDefinition> persistentAgents = agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["acceptance", "--keep-workspace"],
            repository,
            CreateRefinedWorkspace(root),
            ref persistentAgents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: verifier));

        Xunit.Assert.Equal(0, verifier.RunCount);
        Xunit.Assert.Contains("stage=preflight-state-guard", output);
        Xunit.Assert.Contains("Goal.Status expected Verified, actual Active", output);
        Xunit.Assert.Contains("No passing gate receipt was recorded", output);
        var storedGoal = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
        Xunit.Assert.Equal(GoalStatus.Active, storedGoal.Status);
        Xunit.Assert.Equal(WorkTaskStatus.Running, storedGoal.Tasks.Single(candidate => candidate.Id == task.Id).Status);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_preflight_persists_completed_normalization_before_guard")]
    public void PersistentRunnerAcceptancePreflightPersistsCompletedNormalizationBeforeGuard()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement normalized landing", AgentRole.Developer);
            var goal = kernel.CreateGoal("Normalize completed acceptance persistently", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            kernel.ActivateGoal(goal.Id, agents);
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
            CommitGoalWork(root, goal.Id, "normalized.txt", "goal work");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
            goal = kernel.GetGoal(goal.Id);
            Goal? currentGoal = goal;
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["acceptance", "--skip-verify", "--keep-workspace", "--no-record"],
                repository,
                CreateRefinedWorkspace(root),
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("kind=completed-branch-normalized", output, StringComparison.Ordinal);
            Xunit.Assert.Contains("was normalized to Verified", output, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("result=aborted", output, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.Equal(GoalStatus.Completed, repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id).Status);
            Xunit.Assert.Equal("goal work", File.ReadAllText(Path.Combine(root, "normalized.txt")));
            Xunit.Assert.True(GoalWorktrees.IsBranchMergedIntoCurrent(root, goal.Id));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_preflight_persists_git_auto_verification_before_guard")]
    public void PersistentRunnerAcceptancePreflightPersistsGitAutoVerificationBeforeGuard()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement auto-verified landing", AgentRole.Developer);
            var goal = kernel.CreateGoal("Auto-verify acceptance persistently", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            kernel.ActivateGoal(goal.Id, agents);
            CommitGoalWork(root, goal.Id, "auto-verified.txt", "goal work");
            Goal? currentGoal = goal;
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["acceptance", "--skip-verify", "--keep-workspace", "--no-record"],
                repository,
                CreateRefinedWorkspace(root),
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("Auto-verified task", output, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("result=aborted", output, StringComparison.OrdinalIgnoreCase);
            var storedGoal = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
            Xunit.Assert.Equal(GoalStatus.Completed, storedGoal.Status);
            Xunit.Assert.Equal(WorkTaskStatus.Completed, storedGoal.Tasks.Single(candidate => candidate.Id == task.Id).Status);
            Xunit.Assert.NotNull(storedGoal.Tasks.Single(candidate => candidate.Id == task.Id).LastVerification);
            Xunit.Assert.Equal("goal work", File.ReadAllText(Path.Combine(root, "auto-verified.txt")));
            Xunit.Assert.True(GoalWorktrees.IsBranchMergedIntoCurrent(root, goal.Id));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_guard_worktree_head_change_remains_blocked")]
    public void PersistentRunnerAcceptanceGuardWorktreeHeadChangeRemainsBlocked()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Block a changed tested worktree", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
        var worktree = CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        repository.BeforeNextTransaction = _ =>
        {
            File.WriteAllText(Path.Combine(worktree, "concurrent.txt"), "concurrent change");
            RunGit(worktree, "add", "concurrent.txt");
            RunGit(worktree, "commit", "-m", "Concurrent acceptance change");
        };

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["acceptance", "--skip-verify", "--keep-workspace"],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("WorktreeHead expected", output);
        Xunit.Assert.Contains("result=aborted", output);
        Xunit.Assert.Null(repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id).LatestAcceptanceFailure);
        Xunit.Assert.Equal("main", RunGitOutput(root, "branch", "--show-current").Trim());
        Xunit.Assert.False(File.Exists(Path.Combine(root, "feature.txt")));
    }

    [Xunit.Fact(DisplayName = "Acceptance_merge_guard_names_every_invalidating_task_projection_field")]
    public void AcceptanceMergeGuardNamesEveryInvalidatingTaskProjectionField()
    {
        var baseline = new AcceptanceMergeGuardSnapshot(
            GoalStatus.Verified,
            [new AcceptanceMergeGuardTask("task-one", AgentRole.Developer, WorkTaskStatus.Completed)]);

        Xunit.Assert.Equal("Goal.Status", AcceptanceMergeGuard.Compare(
            baseline,
            baseline with { GoalStatus = GoalStatus.Active })!.Field);
        Xunit.Assert.Equal("Tasks added/removed", AcceptanceMergeGuard.Compare(
            baseline,
            baseline with { Tasks = [] })!.Field);
        Xunit.Assert.Equal("Tasks added/removed", AcceptanceMergeGuard.Compare(
            baseline,
            baseline with { Tasks = [.. baseline.Tasks, new("task-two", AgentRole.Tester, WorkTaskStatus.Pending)] })!.Field);
        Xunit.Assert.Equal("Task task-one.RequiredRole", AcceptanceMergeGuard.Compare(
            baseline,
            baseline with { Tasks = [new("task-one", AgentRole.Tester, WorkTaskStatus.Completed)] })!.Field);
        Xunit.Assert.Equal("Task task-one.Status", AcceptanceMergeGuard.Compare(
            baseline,
            baseline with { Tasks = [new("task-one", AgentRole.Developer, WorkTaskStatus.Running)] })!.Field);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_recover_reconciles_terminal_running_exit_file")]
    public async Task PersistentRunnerRecoverReconcilesTerminalRunningExitFile()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("No implicit reconcile", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
        goal = kernel.GetGoal(goal.Id);
        currentGoal = goal;
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["recover", goal.Id.Value[..8], "operator note"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restoredTask = (await repository.LoadAsync()).GetTask(goal.Id, task.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTask.Status);
        Xunit.Assert.Equal(0, restoredTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTask.LastVerification);
        Xunit.Assert.Equal(1, repository.TransactionCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_next_persists_terminal_sweep_repairs")]
    public async Task PersistentRunnerNextPersistsTerminalSweepRepairs()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Premature completion", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
        currentGoal = kernel.GetGoal(goal.Id);
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["next", goal.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(GoalStatus.Active, restored.Status);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
        Xunit.Assert.Equal([goal.Id.Value], repository.LoadedGoalIds);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_reconcile_applies_exit_file_outside_command_transaction")]
    public async Task PersistentRunnerReconcileAppliesExitFileOutsideCommandTransaction()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Explicit reconcile", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["reconcile"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restoredTask = (await repository.LoadAsync()).GetTask(goal.Id, task.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTask.Status);
        Xunit.Assert.Equal(0, restoredTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTask.LastVerification);
        Xunit.Assert.Equal(1, repository.TransactionCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatch_uses_goal_scoped_state")]
    public async Task PersistentRunnerRefreshDispatchUsesGoalScopedState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Explicit refresh", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        var output = CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatch", goal.Id.Value[..8], "1"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
        Xunit.Assert.Equal([goal.Id.Value], repository.LoadedGoalIds);
        Xunit.Assert.Contains("Dispatch state:", output);
        Xunit.Assert.Contains("Next action:", output);
        Xunit.Assert.DoesNotContain("Task timeline:", output);

        var restoredSnapshot = await repository.LoadGoalAsync(goal.Id);
        var restoredTask = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([restoredSnapshot!], [])).GetTask(goal.Id, task.Id);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTask.Status);
        Xunit.Assert.Equal(0, restoredTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTask.LastVerification);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatch_history_limit_matches_compact_command_contract")]
    public void PersistentRunnerRefreshDispatchHistoryLimitMatchesCompactCommandContract()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Explicit bounded refresh history", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        for (var index = 0; index < 12; index++)
        {
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, $"PERSISTENT_HISTORY_{index:00}");
        }
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, """
            WORKER_RESULT:
            files: none
            commands: inspected dispatch output
            tests: pass - focused verification passed
            commit: none
            blockers: none
            model_fit: OpenAI/gpt-test - adequate - verification - sufficient
            skills: none
            confidence: high
            END_WORKER_RESULT
            """);
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatch", goal.Id.Value[..8], "1", "--history-limit", "4"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var timeline = output[(output.IndexOf("Task timeline:", StringComparison.Ordinal) + "Task timeline:".Length)..];
        var newest = timeline.IndexOf("PERSISTENT_HISTORY_11", StringComparison.Ordinal);
        var verification = timeline.IndexOf("Dispatch execution passed", StringComparison.Ordinal);
        var completion = timeline.IndexOf("Dispatch completed successfully", StringComparison.Ordinal);

        Xunit.Assert.Equal(4, CountNonEmptyLines(timeline));
        Xunit.Assert.True(newest >= 0 && newest < verification && verification < completion, timeline);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatch_history_prints_complete_history_oldest_first")]
    public void PersistentRunnerRefreshDispatchHistoryPrintsCompleteHistoryOldestFirst()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Explicit full refresh history", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        for (var index = 0; index < 12; index++)
        {
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, $"PERSISTENT_HISTORY_{index:00}");
        }
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatch", goal.Id.Value[..8], "1", "--history"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var first = output.IndexOf("PERSISTENT_HISTORY_00", StringComparison.Ordinal);
        var middle = output.IndexOf("PERSISTENT_HISTORY_06", StringComparison.Ordinal);
        var newest = output.IndexOf("PERSISTENT_HISTORY_11", StringComparison.Ordinal);
        var verification = output.IndexOf("Dispatch execution passed", StringComparison.Ordinal);
        var completion = output.IndexOf("Dispatch completed successfully", StringComparison.Ordinal);

        Xunit.Assert.Contains("Task timeline:", output);
        Xunit.Assert.True(
            first >= 0 && first < middle && middle < newest && newest < verification && verification < completion,
            output);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_invalid_refresh_history_limit_does_not_load_or_mutate_goal_state")]
    public void PersistentRunnerInvalidRefreshHistoryLimitDoesNotLoadOrMutateGoalState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Reject invalid refresh history", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var exception = Xunit.Assert.Throws<ArgumentException>(() => CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatch", goal.Id.Value[..8], "1", "--history-limit", "1", "--history-limit", "2"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains(CliCommandHelp.RefreshDispatchUsage, exception.Message);
        Xunit.Assert.Equal(0, repository.LoadGoalsCount);
        Xunit.Assert.Equal(0, repository.TransactionCount);
        Xunit.Assert.True(task.LastProcess!.IsRunning);
        Xunit.Assert.Null(task.LastVerification);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatch_atomically_persists_premise_invalid_human_wait")]
    public async Task PersistentRunnerRefreshDispatchAtomicallyPersistsPremiseInvalidHumanWait()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Validate the implementation premise", AgentRole.Planner);
        var goal = kernel.CreateGoal("Premise validation canary", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, """
            WORKER_RESULT:
            files: none
            commands: inspected src/Mcg.AgentOrchestrator.Core/Domain/OrchestrationEnums.cs
            tests: not-run - read-only premise validation
            commit: none
            blockers: premise-invalid - AgentRole.Judge is absent from the inspected enum at the current HEAD
            model_fit: Anthropic/claude-opus-5 - adequate - premise validation - sufficient
            skills: orchestrator-worker-verification
            confidence: high
            END_WORKER_RESULT
            """);
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatch", goal.Id.Value[..8], "1"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = await repository.LoadAsync();
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, restoredTask.Status);
        Xunit.Assert.True(restoredTask.LastVerification?.WorkerResultPresent);
        Xunit.Assert.Equal(1, repository.LastSavedGoalStateHumanInputCount);
        var request = Xunit.Assert.Single(restored.HumanInputRequests);
        Xunit.Assert.False(request.IsCompleted);
        Xunit.Assert.Contains("premise-invalid", request.Question, StringComparison.Ordinal);
        Xunit.Assert.Equal(task.Id, request.TaskId);
        Xunit.Assert.Equal(0, repository.TransactAsyncCount);
        Xunit.Assert.Equal(1, repository.TransactGoalCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatches_goal_flag_uses_requested_goal_scoped_state")]
    public async Task PersistentRunnerRefreshDispatchesGoalFlagUsesRequestedGoalScopedState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var currentTask = new TaskSpec(TaskId.New(), "Current work", AgentRole.Developer);
        var targetTask = new TaskSpec(TaskId.New(), "Target work", AgentRole.Developer);
        var current = kernel.CreateGoal("Current refresh", [currentTask]);
        var target = kernel.CreateGoal("Target refresh", [targetTask]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = current;
        kernel.ActivateGoal(current.Id, agents);
        kernel.ActivateGoal(target.Id, agents);
        RecordRunningProcess(kernel, target, targetTask, root);
        File.WriteAllText(targetTask.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(targetTask.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatches", "--goal", target.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
        Xunit.Assert.Equal([target.Id.Value], repository.LoadedGoalIds);
        Xunit.Assert.Equal(target.Id, currentGoal!.Id);

        var currentSnapshot = await repository.LoadGoalAsync(current.Id);
        var targetSnapshot = await repository.LoadGoalAsync(target.Id);
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([currentSnapshot!, targetSnapshot!], []));
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(current.Id, currentTask.Id).Status);
        var restoredTargetTask = restored.GetTask(target.Id, targetTask.Id);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTargetTask.Status);
        Xunit.Assert.Equal(0, restoredTargetTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTargetTask.LastVerification);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_reconcile_discards_stale_process_identity")]
    public async Task PersistentRunnerReconcileDiscardsStaleProcessIdentity()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Stale reconcile", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        repository.BeforeNextTransaction = stored =>
        {
            var current = stored.GetTask(goal.Id, task.Id);
            var replacement = current.LastProcess! with
            {
                ProcessId = 424242,
                StartedAt = current.LastProcess.StartedAt.AddSeconds(1),
                ExitCodePath = current.LastProcess.ExitCodePath + ".next"
            };
            stored.RecordTaskProcessRefreshed(goal.Id, task.Id, replacement, null);
        };

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["reconcile"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restoredTask = (await repository.LoadAsync()).GetTask(goal.Id, task.Id);
        Xunit.Assert.False(changed);
        Xunit.Assert.Equal(WorkTaskStatus.Running, restoredTask.Status);
        Xunit.Assert.Equal(424242, restoredTask.LastProcess!.ProcessId);
        Xunit.Assert.Null(restoredTask.LastProcess.ExitCode);
        Xunit.Assert.Equal(1, repository.TransactionCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_excludes_terminal_goals_from_hydration")]
    public void PersistentRunnerConductLoopExcludesTerminalGoalsFromHydration()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var terminalGoalIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var completed = kernel.CreateGoal($"Completed audit goal {i}", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
            kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
                new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
            terminalGoalIds.Add(completed.Id.Value);
        }

        var cleanedUp = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Cleaned-up audit goal");
        var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Cancelled audit goal");
        var superseded = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Superseded audit goal");
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active conductor goal");
        var failed = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Failed conductor goal");
        kernel.ReportTaskProgress(failed.Id, failed.Tasks.Single().Id, WorkTaskStatus.Failed, "Still needs conductor/operator attention");
        kernel = WithGoalStatus(kernel, cancelled.Id, GoalStatus.Cancelled);
        kernel = WithGoalStatus(kernel, superseded.Id, GoalStatus.Superseded);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        repository.CleanedUpGoalIds.Add(cleanedUp.Id.Value);
        terminalGoalIds.AddRange([cleanedUp.Id.Value, cancelled.Id.Value, superseded.Id.Value]);

        var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository);

        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.All(terminalGoalIds, id => Xunit.Assert.DoesNotContain(id, repository.LoadedGoalIds));
        Xunit.Assert.Contains(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Contains(failed.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.All(terminalGoalIds, id => Xunit.Assert.DoesNotContain(loaded.Goals, goal => goal.Id.Value == id));
        Xunit.Assert.Contains(loaded.Goals, goal => goal.Id == active.Id);
        Xunit.Assert.Contains(loaded.Goals, goal => goal.Id == failed.Id);
        Xunit.Assert.All(
            terminalGoalIds,
            id => Xunit.Assert.False(loaded.IsKnownCompletedDependencyGoal(new GoalId(id))));
        var expectedLoadedIds = new[] { active.Id.Value, failed.Id.Value }
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Xunit.Assert.Equal(
            expectedLoadedIds,
            repository.LoadGoalBatches.Single().OrderBy(id => id, StringComparer.Ordinal).ToArray());
        var terminalSnapshot = repository.LoadGoalAsync(new GoalId(terminalGoalIds[0])).GetAwaiter().GetResult();
        Xunit.Assert.NotNull(terminalSnapshot);
        Xunit.Assert.Equal(terminalGoalIds[0], terminalSnapshot.Id);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_keeps_parked_goals_metadata_only")]
    public void PersistentRunnerConductLoopKeepsParkedGoalsMetadataOnly()
    {
        const int parkedGoalCount = 53;
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active conductor goal");
        var parkedGoalIds = new List<GoalId>();
        var largePayload = new string('x', 8192);
        for (var i = 0; i < parkedGoalCount; i++)
        {
            var parked = kernel.CreateGoal(
                $"Parked memory fixture {i}: {largePayload}",
                [new TaskSpec(TaskId.New(), $"Preserve parked metadata {i}", AgentRole.Researcher)]);
            kernel.ActivateGoal(parked.Id, AgentCatalog.Default().Agents);
            kernel.ParkGoal(parked.Id, "memory fixture");
            parkedGoalIds.Add(parked.Id);
        }

        var repository = new InMemoryTransactionalStateRepository(kernel);
        var preFixHydratedIds = kernel.Goals
            .Where(goal => goal.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
            .Select(goal => goal.Id)
            .ToArray();
        var preFixBytes = repository.EstimateGoalSnapshotJsonBytes(preFixHydratedIds);
        var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;

        var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository);

        var workingSetAfter = Process.GetCurrentProcess().WorkingSet64;
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Contains(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.DoesNotContain(id.Value, repository.LoadedGoalIds));
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.DoesNotContain(loaded.Goals, goal => goal.Id == id));
        Xunit.Assert.All(parkedGoalIds, id =>
        {
            Xunit.Assert.True(loaded.TryGetKnownDependencyGoalStatus(id, out var status));
            Xunit.Assert.Equal(GoalStatus.Parked.ToString(), status);
        });
        var reduction = preFixBytes == 0
            ? 0
            : (double)(preFixBytes - repository.LoadedGoalSnapshotJsonBytes) / preFixBytes;
        var artifactPath = WriteParkedHydrationMeasurementArtifact(
            parkedGoalCount,
            preFixBytes,
            repository.LoadedGoalSnapshotJsonBytes,
            reduction,
            workingSetBefore,
            workingSetAfter);
        Console.WriteLine($"parked hydration measurement artifact: {artifactPath}");
        Xunit.Assert.True(File.Exists(artifactPath));
        Xunit.Assert.True(
            reduction >= 0.70,
            $"parked_count={parkedGoalCount}; pre_fix_goal_json_bytes={preFixBytes}; after_goal_json_bytes={repository.LoadedGoalSnapshotJsonBytes}; reduction={reduction:P1}; working_set_before={workingSetBefore}; working_set_after={workingSetAfter}");
    }

    private static string WriteParkedHydrationMeasurementArtifact(
        int parkedGoalCount,
        long preFixGoalJsonBytes,
        long afterGoalJsonBytes,
        double reduction,
        long workingSetBefore,
        long workingSetAfter)
    {
        var artifactPath = Path.Combine(Path.GetTempPath(), "mcg-conduct-loop-parked-hydration-measurement-latest.json");
        File.WriteAllText(
            artifactPath,
            JsonSerializer.Serialize(
                new
                {
                    fixture = "conduct-loop-parked-hydration",
                    parked_goal_count = parkedGoalCount,
                    safety_net_sweep_cadence_ticks = CliPersistentStateRunner.ParkedGoalSafetyNetSweepCadenceTicks,
                    pre_fix_goal_json_bytes = preFixGoalJsonBytes,
                    after_goal_json_bytes = afterGoalJsonBytes,
                    reduction,
                    working_set_before = workingSetBefore,
                    working_set_after = workingSetAfter
                },
                new JsonSerializerOptions { WriteIndented = true }));

        return artifactPath;
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_hydrates_unparked_goal_on_next_kernel_load")]
    public void PersistentRunnerConductLoopHydratesUnparkedGoalOnNextKernelLoad()
    {
        const int parkedGoalCount = 100;
        var kernel = new AgentOrchestratorKernel();
        var parkedGoalIds = new List<GoalId>();
        for (var i = 0; i < parkedGoalCount; i++)
        {
            var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                $"Parked fixture {i}");
            kernel.ParkGoal(parked.Id, "operator deferred");
            parkedGoalIds.Add(parked.Id);
        }

        var initiallyParkedRepository = new InMemoryTransactionalStateRepository(kernel);
        var initiallyLoaded = CliPersistentStateRunner.LoadConductLoopKernel(initiallyParkedRepository);
        Xunit.Assert.Empty(initiallyLoaded.Goals);
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.DoesNotContain(id.Value, initiallyParkedRepository.LoadedGoalIds));

        var unparkedGoalId = parkedGoalIds[42];
        kernel = WithGoalStatus(kernel, unparkedGoalId, GoalStatus.Active);
        var nextTickRepository = new InMemoryTransactionalStateRepository(kernel);
        var nextTickLoaded = CliPersistentStateRunner.LoadConductLoopKernel(nextTickRepository);

        Xunit.Assert.Contains(unparkedGoalId.Value, nextTickRepository.LoadedGoalIds);
        Xunit.Assert.Contains(nextTickLoaded.Goals, goal => goal.Id == unparkedGoalId);
        Xunit.Assert.All(
            parkedGoalIds.Where(id => id != unparkedGoalId),
            id => Xunit.Assert.DoesNotContain(id.Value, nextTickRepository.LoadedGoalIds));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_parked_safety_net_sweeps_every_four_ticks")]
    public void PersistentRunnerConductLoopParkedSafetyNetSweepsEveryFourTicks()
    {
        const int parkedGoalCount = 100;
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Active conductor goal");
        var parkedGoalIds = new List<GoalId>();
        for (var i = 0; i < parkedGoalCount; i++)
        {
            var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                $"Parked safety-net fixture {i}");
            kernel.ParkGoal(parked.Id, "operator deferred");
            parkedGoalIds.Add(parked.Id);
        }

        Xunit.Assert.Equal(4, CliPersistentStateRunner.ParkedGoalSafetyNetSweepCadenceTicks);
        Xunit.Assert.False(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(1));
        Xunit.Assert.False(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(2));
        Xunit.Assert.False(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(3));
        Xunit.Assert.True(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(4));
        Xunit.Assert.False(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(5));
        Xunit.Assert.True(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(8));

        var repository = new InMemoryTransactionalStateRepository(kernel);
        var sweepKernel = CliPersistentStateRunner.LoadConductLoopParkedGoalSafetyNetKernel(repository);

        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.DoesNotContain(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.Contains(id.Value, repository.LoadedGoalIds));
        Xunit.Assert.Equal(parkedGoalCount, sweepKernel.Goals.Count);
        Xunit.Assert.All(sweepKernel.Goals, goal => Xunit.Assert.Equal(GoalStatus.Parked, goal.Status));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_safety_net_promotes_resolved_parked_wait_within_four_ticks")]
    public void PersistentRunnerConductLoopSafetyNetPromotesResolvedParkedWaitWithinFourTicks()
    {
        const int parkedGoalCount = 101;
        var kernel = new AgentOrchestratorKernel();
        var parkedGoalIds = new List<GoalId>();
        for (var i = 0; i < parkedGoalCount; i++)
        {
            var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                $"Parked wait fixture {i}");
            kernel.ParkGoal(parked.Id, "operator deferred");
            parkedGoalIds.Add(parked.Id);
        }

        var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Parked answered wait fixture");
        var targetTask = target.Tasks.Single();
        kernel.RecordTaskDispatch(target.Id, targetTask.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", DateTimeOffset.UtcNow));
        var wait = kernel.RequestHumanInput(target.Id, targetTask.Id, "Which option?");
        kernel.SubmitHumanInput(wait.Id, "Use option A.");
        var answeredSnapshot = kernel.ExportSnapshot();
        var answeredGoal = answeredSnapshot.Goals.Single(goal => goal.Id == target.Id.Value);
        var answeredAt = answeredGoal.Timeline
            .Last(evt => evt.Kind == ProgressKind.HumanInputReceived)
            .OccurredAt;
        var parkedAfterAnsweredSnapshot = answeredSnapshot with
        {
            Goals = answeredSnapshot.Goals
                .Select(goal => goal.Id == target.Id.Value
                    ? goal with
                    {
                        Status = GoalStatus.Parked,
                        Timeline = goal.Timeline
                            .Append(new ProgressEventSnapshot(
                                target.Id.Value,
                                null,
                                ProgressKind.GoalPolicyDecision,
                                "Goal parked: waiting for operator answer",
                                answeredAt.AddTicks(-1)))
                            .ToArray()
                    }
                    : goal)
                .ToArray()
        };
        kernel = AgentOrchestratorKernel.FromSnapshot(parkedAfterAnsweredSnapshot);

        var normalTickRepository = new InMemoryTransactionalStateRepository(kernel);
        for (var tick = 1; tick < CliPersistentStateRunner.ParkedGoalSafetyNetSweepCadenceTicks; tick++)
        {
            var normalTickKernel = CliPersistentStateRunner.LoadConductLoopKernel(normalTickRepository);
            Xunit.Assert.DoesNotContain(target.Id, normalTickKernel.Goals.Select(goal => goal.Id));
            Xunit.Assert.False(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(tick));
        }

        var safetyNetRepository = new InMemoryTransactionalStateRepository(kernel);
        var currentTickKernel = AgentOrchestratorKernel.FromSnapshot(parkedAfterAnsweredSnapshot with
        {
            Goals = [parkedAfterAnsweredSnapshot.Goals.Single(goal => goal.Id == target.Id.Value)],
            HumanInputRequests = parkedAfterAnsweredSnapshot.HumanInputRequests
                .Where(request => request.GoalId == target.Id.Value)
                .ToArray()
        });
        var safetyNetKernel = CliPersistentStateRunner.LoadConductLoopParkedGoalSafetyNetKernel(safetyNetRepository);
        var promoted = safetyNetKernel.RefreshParkedGoalsWithResolvedHumanWaits();
        var promotedSnapshots = safetyNetKernel.ExportSnapshot().Goals
            .Where(goal => goal.Status != GoalStatus.Parked)
            .ToArray();
        safetyNetRepository.SaveGoalSnapshotsAsync(promotedSnapshots).GetAwaiter().GetResult();
        var nextPrewalkKernel = CliPersistentStateRunner.LoadConductLoopKernel(safetyNetRepository);

        Xunit.Assert.Equal(4, CliPersistentStateRunner.ParkedGoalSafetyNetSweepCadenceTicks);
        Xunit.Assert.True(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(4));
        Xunit.Assert.Equal(1, promoted);
        Xunit.Assert.Contains(target.Id.Value, safetyNetRepository.LoadedGoalIds);
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.Contains(id.Value, safetyNetRepository.LoadedGoalIds));
        Xunit.Assert.Contains(currentTickKernel.Goals, goal => goal.Id == target.Id && goal.Status == GoalStatus.Parked);
        Xunit.Assert.Contains(nextPrewalkKernel.Goals, goal => goal.Id == target.Id && goal.Status == GoalStatus.Active);
        Xunit.Assert.DoesNotContain(nextPrewalkKernel.Goals, goal => parkedGoalIds.Contains(goal.Id));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_targeted_query_promotes_resolved_parked_wait_on_next_load")]
    public void PersistentRunnerConductLoopTargetedQueryPromotesResolvedParkedWaitOnNextLoad()
    {
        const int parkedGoalCount = 100;
        var kernel = new AgentOrchestratorKernel();
        var parkedGoalIds = new List<GoalId>();
        for (var i = 0; i < parkedGoalCount; i++)
        {
            var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                $"Parked targeted fixture {i}");
            kernel.ParkGoal(parked.Id, "operator deferred");
            parkedGoalIds.Add(parked.Id);
        }

        var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Parked targeted answered wait fixture");
        var targetTask = target.Tasks.Single();
        kernel.RecordTaskDispatch(target.Id, targetTask.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", DateTimeOffset.UtcNow));
        var wait = kernel.RequestHumanInput(target.Id, targetTask.Id, "Which option?");
        kernel.SubmitHumanInput(wait.Id, "Use option A.");
        var answeredSnapshot = kernel.ExportSnapshot();
        var answeredAt = answeredSnapshot.Goals
            .Single(goal => goal.Id == target.Id.Value)
            .Timeline
            .Last(evt => evt.Kind == ProgressKind.HumanInputReceived)
            .OccurredAt;
        kernel = AgentOrchestratorKernel.FromSnapshot(answeredSnapshot with
        {
            Goals = answeredSnapshot.Goals
                .Select(goal => goal.Id == target.Id.Value
                    ? goal with
                    {
                        Status = GoalStatus.Parked,
                        Timeline = goal.Timeline
                            .Append(new ProgressEventSnapshot(
                                target.Id.Value,
                                null,
                                ProgressKind.GoalPolicyDecision,
                                "Goal parked: waiting for operator answer",
                                answeredAt.AddTicks(-1)))
                            .ToArray()
                    }
                    : goal)
                .ToArray()
        });

        var repository = new InMemoryTransactionalStateRepository(kernel);
        var currentTickKernel = AgentOrchestratorKernel.FromSnapshot(answeredSnapshot with
        {
            Goals = answeredSnapshot.Goals
                .Where(goal => goal.Id == target.Id.Value)
                .Select(goal => goal with
                {
                    Status = GoalStatus.Parked,
                    Timeline = goal.Timeline
                        .Append(new ProgressEventSnapshot(
                            target.Id.Value,
                            null,
                            ProgressKind.GoalPolicyDecision,
                            "Goal parked: waiting for operator answer",
                            answeredAt.AddTicks(-1)))
                        .ToArray()
                })
                .ToArray(),
            HumanInputRequests = answeredSnapshot.HumanInputRequests
                .Where(request => request.GoalId == target.Id.Value)
                .ToArray()
        });
        var targetedKernel = CliPersistentStateRunner.LoadConductLoopResolvedParkedHumanWaitKernel(repository);
        var promoted = targetedKernel.RefreshParkedGoalsWithResolvedHumanWaits();
        var promotedSnapshots = targetedKernel.ExportSnapshot().Goals
            .Where(goal => goal.Status != GoalStatus.Parked)
            .ToArray();
        repository.SaveGoalSnapshotsAsync(promotedSnapshots).GetAwaiter().GetResult();
        var nextTickKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);

        Xunit.Assert.Equal(1, repository.CompletedHumanInputQueryCount);
        Xunit.Assert.Contains(target.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.DoesNotContain(id.Value, targetedKernel.Goals.Select(goal => goal.Id.Value)));
        Xunit.Assert.Equal(1, promoted);
        Xunit.Assert.Contains(currentTickKernel.Goals, goal => goal.Id == target.Id && goal.Status == GoalStatus.Parked);
        Xunit.Assert.Contains(nextTickKernel.Goals, goal => goal.Id == target.Id && goal.Status == GoalStatus.Active);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_unpark_persist_failure_is_surfaced")]
    public void PersistentRunnerConductLoopUnparkPersistFailureIsSurfaced()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Parked targeted persist failure fixture");
        var targetTask = target.Tasks.Single();
        kernel.RecordTaskDispatch(target.Id, targetTask.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", DateTimeOffset.UtcNow));
        var wait = kernel.RequestHumanInput(target.Id, targetTask.Id, "Which option?");
        kernel.SubmitHumanInput(wait.Id, "Use option A.");
        var answeredSnapshot = kernel.ExportSnapshot();
        var answeredAt = answeredSnapshot.Goals
            .Single(goal => goal.Id == target.Id.Value)
            .Timeline
            .Last(evt => evt.Kind == ProgressKind.HumanInputReceived)
            .OccurredAt;
        kernel = AgentOrchestratorKernel.FromSnapshot(answeredSnapshot with
        {
            Goals = answeredSnapshot.Goals
                .Select(goal => goal.Id == target.Id.Value
                    ? goal with
                    {
                        Status = GoalStatus.Parked,
                        Timeline = goal.Timeline
                            .Append(new ProgressEventSnapshot(
                                target.Id.Value,
                                null,
                                ProgressKind.GoalPolicyDecision,
                                "Goal parked: waiting for operator answer",
                                answeredAt.AddTicks(-1)))
                            .ToArray()
                    }
                    : goal)
                .ToArray()
        });
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var loopKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        var persistAttempts = 0;
        var context = new CliExecutionContext(
            loopKernel,
            workspace,
            providers,
            agents,
            profiles,
            currentGoal: null,
            reloadKernel: () => CliPersistentStateRunner.LoadConductLoopKernel(repository),
            persistKernel: _ => { },
            persistGoalKernel: (_, changedGoalIds) =>
            {
                if (changedGoalIds.Contains(target.Id))
                {
                    persistAttempts++;
                    throw new InvalidOperationException("resolved parked promotion write failed");
                }
            },
            reloadResolvedParkedHumanWaitKernel: () => CliPersistentStateRunner.LoadConductLoopResolvedParkedHumanWaitKernel(repository),
            reloadParkedGoalSafetyNetKernel: () => new AgentOrchestratorKernel());

        var output = CaptureConsole(() => CliCommandHandlers.Execute(["conduct", "--loop", "--max-iterations", "1"], context));

        var eventText = File.ReadAllText(workspace.ConductEventsLogPath);
        Xunit.Assert.Equal(1, persistAttempts);
        Xunit.Assert.Contains("PARKED_UNPARK_PERSISTENCE_FAILED", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("resolved parked promotion write failed", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("LOOP_JANITORIAL_FAILED", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("PARKED_UNPARK_PERSISTENCE_FAILED", eventText, StringComparison.Ordinal);
        Xunit.Assert.Contains("loop-janitorial-failure", eventText, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Cancelled, false)]
    [Xunit.InlineData(GoalStatus.Cancelled, true)]
    [Xunit.InlineData(GoalStatus.Superseded, false)]
    [Xunit.InlineData(GoalStatus.Failed, false)]
    public async Task ConductLoop_ExternalTerminalStatus_EvictsBeforePrewalk(
        GoalStatus storedStatus,
        bool enqueueIntent)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var completedTask = new TaskSpec(TaskId.New(), "Completed predecessor.", AgentRole.Developer);
            var failedTask = new TaskSpec(TaskId.New(), "Failed successor.", AgentRole.Tester);
            var goal = kernel.CreateGoal("Externally terminalized conductor goal", [completedTask, failedTask]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, completedTask.Id, WorkTaskStatus.Completed, "done");
            kernel.ReportTaskProgress(goal.Id, failedTask.Id, WorkTaskStatus.Failed, "failed");
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var loopKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
            var staleLoopGoal = loopKernel.GetGoal(goal.Id);
            var terminalKernel = WithGoalStatus(kernel, goal.Id, storedStatus);
            await repository.SaveGoalSnapshotsAsync(terminalKernel.ExportSnapshot().Goals);
            var nonTargetedReload = CliPersistentStateRunner.LoadConductLoopKernel(repository);
            Xunit.Assert.Equal(GoalStatus.Active, staleLoopGoal.Status);
            Xunit.Assert.DoesNotContain(nonTargetedReload.Goals, candidate => candidate.Id == goal.Id);

            var intentStore = SqliteOperatorIntentStore.ForDirectories(
                workspace.OrchestratorDirectory,
                workspace.LogDirectory);
            var intent = new OperatorIntentRecord(
                $"terminal-eviction-{storedStatus}",
                $"terminal-eviction-key-{storedStatus}",
                OperatorIntentVerbs.Progress,
                goal.Id.Value,
                failedTask.Id.Value,
                JsonSerializer.Serialize(
                    new ProgressOperatorIntentPayload(WorkTaskStatus.Running, "must not resurrect"),
                    OperatorIntentJson.Options),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            if (enqueueIntent)
                await intentStore.EnqueueAsync(intent);

            var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            var context = new CliExecutionContext(
                loopKernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                currentGoal: null,
                reloadKernel: () => CliPersistentStateRunner.LoadConductLoopKernel(repository),
                persistKernel: _ => { },
                persistGoalKernel: (_, _) => { },
                reloadResolvedParkedHumanWaitKernel: () => new AgentOrchestratorKernel(),
                reloadParkedGoalSafetyNetKernel: () => new AgentOrchestratorKernel(),
                reloadKernelForGoals: trackedIds =>
                    CliPersistentStateRunner.LoadConductLoopKernel(repository, trackedIds))
            {
                EventWriter = eventWriter
            };

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--max-iterations", "1"],
                context));

            var eventsPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
            var events = File.ReadAllLines(eventsPath)
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                var escalations = events.Where(document =>
                    document.RootElement.GetProperty("eventType").GetString() == "GoalEscalated").ToArray();
                Xunit.Assert.All(escalations, escalation => Xunit.Assert.Equal(
                    "Goal is in Failed state; operator action required",
                    escalation.RootElement.GetProperty("reason").GetString()));
                Xunit.Assert.Empty(escalations);
                var eviction = Xunit.Assert.Single(events.Where(document =>
                    document.RootElement.GetProperty("eventType").GetString() == "GoalEvictedFromConductor"));
                Xunit.Assert.Equal(storedStatus.ToString(), eviction.RootElement.GetProperty("status").GetString());
                Xunit.Assert.Equal(
                    enqueueIntent ? "operator-intent-forced-reload" : "scheduled-reload",
                    eviction.RootElement.GetProperty("trigger").GetString());
            }
            finally
            {
                foreach (var document in events)
                    document.Dispose();
            }

            Xunit.Assert.Empty(loopKernel.Goals);
            Xunit.Assert.All(staleLoopGoal.Tasks, task => Xunit.Assert.Null(task.LastDispatch));
            Xunit.Assert.True(loopKernel.TryGetKnownDependencyGoalStatus(goal.Id, out var reconciledStatus));
            Xunit.Assert.Equal(storedStatus.ToString(), reconciledStatus);
            Xunit.Assert.Contains(repository.LoadGoalBatches, batch => batch.Contains(goal.Id.Value));

            if (enqueueIntent)
            {
                var intentOutcome = await intentStore.GetAsync(intent.Id);
                Xunit.Assert.Equal(OperatorIntentStatus.Rejected, intentOutcome!.Status);
                Xunit.Assert.Contains("reasonCode=goal-terminal-evicted", intentOutcome.Outcome, StringComparison.Ordinal);
                Xunit.Assert.Contains($"stored status is {storedStatus}", intentOutcome.Outcome, StringComparison.Ordinal);
            }

            var storedKernel = await repository.LoadAsync();
            var storedGoal = storedKernel.GetGoal(goal.Id);
            var stopPlan = GoalAbandonPlanner.Build(storedKernel, storedGoal, workspace, "operator stop");
            Xunit.Assert.Equal(storedStatus, stopPlan.GoalStatus);
            Xunit.Assert.Contains(stopPlan.Steps, step =>
                step.Kind == GoalAbandonStepKind.GoalStatus &&
                step.Detail == $"Goal is already {storedStatus}.");

            IReadOnlyList<AgentDefinition> readerAgents = AgentCatalog.Default().Agents;
            var readerProfiles = WorkerProfileCatalog.Default();
            Goal? readerCurrentGoal = null;
            var goalsOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["goals"],
                repository,
                workspace,
                ref readerAgents,
                new InMemoryModelProviderRegistry([]),
                ref readerProfiles,
                ref readerCurrentGoal));
            var statusOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["status", goal.Id.Value[..8]],
                repository,
                workspace,
                ref readerAgents,
                new InMemoryModelProviderRegistry([]),
                ref readerProfiles,
                ref readerCurrentGoal));
            Xunit.Assert.Contains($"{goal.Id.Value[..8]} {storedStatus}", goalsOutput, StringComparison.Ordinal);
            Xunit.Assert.Contains(storedStatus.ToString(), statusOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ConductLoop_LiveGoal_ReloadsAndWalks()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var completedTask = new TaskSpec(TaskId.New(), "Completed predecessor.", AgentRole.Developer);
            var failedTask = new TaskSpec(TaskId.New(), "Failed successor.", AgentRole.Tester);
            var goal = kernel.CreateGoal("Live conductor goal", [completedTask, failedTask]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, completedTask.Id, WorkTaskStatus.Completed, "done");
            kernel.ReportTaskProgress(goal.Id, failedTask.Id, WorkTaskStatus.Failed, "failed");
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var loopKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
            var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            var context = new CliExecutionContext(
                loopKernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                currentGoal: null,
                reloadKernel: () => CliPersistentStateRunner.LoadConductLoopKernel(repository),
                persistKernel: _ => { },
                persistGoalKernel: (_, _) => { },
                reloadResolvedParkedHumanWaitKernel: () => new AgentOrchestratorKernel(),
                reloadParkedGoalSafetyNetKernel: () => new AgentOrchestratorKernel(),
                reloadKernelForGoals: trackedIds =>
                    CliPersistentStateRunner.LoadConductLoopKernel(repository, trackedIds))
            {
                EventWriter = eventWriter
            };

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--max-iterations", "1"],
                context));

            var trackedGoal = Xunit.Assert.Single(loopKernel.Goals);
            Xunit.Assert.Equal(goal.Id, trackedGoal.Id);
            Xunit.Assert.Equal(GoalStatus.Active, trackedGoal.Status);
            Xunit.Assert.Contains(repository.LoadGoalBatches, batch => batch.Contains(goal.Id.Value));
            var eventsPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
            var escalationLine = Xunit.Assert.Single(File.ReadLines(eventsPath).Where(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("eventType").GetString() == "GoalEscalated";
            }));
            using var escalation = JsonDocument.Parse(escalationLine);
            Xunit.Assert.Equal("GoalEscalated", escalation.RootElement.GetProperty("eventType").GetString());
            Xunit.Assert.Equal(GoalStatus.Active.ToString(), escalation.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ConductLoop_EvictedGoal_LaterIntentKeepsReason()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Cancelled work.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Externally cancelled conductor goal", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var loopKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
            await repository.SaveGoalSnapshotsAsync(
                WithGoalStatus(kernel, goal.Id, GoalStatus.Cancelled).ExportSnapshot().Goals);
            var context = new CliExecutionContext(
                loopKernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                currentGoal: null,
                reloadKernel: () => CliPersistentStateRunner.LoadConductLoopKernel(repository),
                persistKernel: _ => { },
                persistGoalKernel: (_, _) => { },
                reloadResolvedParkedHumanWaitKernel: () => new AgentOrchestratorKernel(),
                reloadParkedGoalSafetyNetKernel: () => new AgentOrchestratorKernel(),
                reloadKernelForGoals: trackedIds =>
                    CliPersistentStateRunner.LoadConductLoopKernel(repository, trackedIds));

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--max-iterations", "1"],
                context));
            Xunit.Assert.Empty(loopKernel.Goals);

            var intentStore = SqliteOperatorIntentStore.ForDirectories(
                workspace.OrchestratorDirectory,
                workspace.LogDirectory);
            var intent = new OperatorIntentRecord(
                "later-terminal-eviction",
                "later-terminal-eviction-key",
                OperatorIntentVerbs.Progress,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new ProgressOperatorIntentPayload(WorkTaskStatus.Running, "must remain cancelled"),
                    OperatorIntentJson.Options),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            await intentStore.EnqueueAsync(intent);

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--max-iterations", "1"],
                context));

            var outcome = await intentStore.GetAsync(intent.Id);
            Xunit.Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
            Xunit.Assert.Contains("reasonCode=goal-terminal-evicted", outcome.Outcome, StringComparison.Ordinal);
            Xunit.Assert.Contains("stored status is Cancelled", outcome.Outcome, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_targeted_query_ignores_synthetic_parked_wait_completion")]
    public void PersistentRunnerConductLoopTargetedQueryIgnoresSyntheticParkedWaitCompletion()
    {
        var kernel = new AgentOrchestratorKernel();
        var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Parked synthetic wait fixture");
        var targetTask = target.Tasks.Single();
        kernel.RecordTaskDispatch(target.Id, targetTask.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", DateTimeOffset.UtcNow));
        kernel.RequestHumanInput(target.Id, targetTask.Id, "Need operator decision.");
        kernel.ParkGoal(target.Id, "waiting for operator answer");

        var repository = new InMemoryTransactionalStateRepository(kernel);
        var targetedKernel = CliPersistentStateRunner.LoadConductLoopResolvedParkedHumanWaitKernel(repository);
        var promoted = targetedKernel.RefreshParkedGoalsWithResolvedHumanWaits();

        Xunit.Assert.Equal(1, repository.CompletedHumanInputQueryCount);
        Xunit.Assert.DoesNotContain(target.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Equal(0, promoted);
        Xunit.Assert.Empty(targetedKernel.Goals);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_sweeps_terminal_candidates_loaded_on_demand")]
    public void PersistentRunnerConductLoopSweepsTerminalCandidatesLoadedOnDemand()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement terminal cleanup", AgentRole.Developer);
            var goal = kernel.CreateGoal("Terminal cleanup candidate", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/terminal-cleanup.txt", "goal work");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["conduct", "--loop", "--max-iterations", "0"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            var restored = repository.LoadGoalAsync(goal.Id).GetAwaiter().GetResult()!;
            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.True(repository.LoadGoalsCount >= 2);
            Xunit.Assert.Contains(goal.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.Equal(GoalStatus.Verified, restored.Status);
            Xunit.Assert.Contains("completed-branch-normalized", output, StringComparison.Ordinal);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_timeline_loads_terminal_goal_on_demand")]
    public void PersistentRunnerTimelineLoadsTerminalGoalOnDemand()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var completed = kernel.CreateGoal("Completed timeline goal", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active goal");
        kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
            new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["timeline", completed.Id.Value[..8]],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Contains(completed.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.DoesNotContain(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Equal(completed.Id, currentGoal!.Id);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_single_goal_reports_load_terminal_goal_on_demand")]
    public void PersistentRunnerSingleGoalReportsLoadTerminalGoalOnDemand()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        IReadOnlyList<Func<string, string[]>> commands =
        [
            prefix => ["status", prefix],
            prefix => ["monitor", prefix],
            prefix => ["readiness", prefix],
            prefix => ["next", prefix],
            prefix => ["next", prefix, "--full"],
            prefix => ["evidence", prefix],
            prefix => ["stages", prefix],
            prefix => ["gates", prefix],
            prefix => ["verify-needed", prefix],
            prefix => ["input-needed", prefix],
            prefix => ["goal-diagnostics", prefix],
            prefix => ["subscription-plan", prefix],
            prefix => ["failure-triage", prefix],
            prefix => ["retention-plan", prefix]
        ];

        foreach (var command in commands)
        {
            var kernel = new AgentOrchestratorKernel();
            var completed = kernel.CreateGoal("Completed report goal", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
            var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active report bystander");
            kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
                new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
            var repository = new InMemoryTransactionalStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                command(completed.Id.Value[..8]),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Contains(completed.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.DoesNotContain(active.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.Equal(completed.Id, currentGoal!.Id);
        }
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_input_needed_hydrates_goal_human_waits")]
    public void PersistentRunnerInputNeededHydratesGoalHumanWaits()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Validate premise", AgentRole.Planner);
        var goal = kernel.CreateGoal("Answerable premise wait", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = kernel.RequestHumanInput(
            goal.Id,
            task.Id,
            "Planner reported premise-invalid; clarify or abandon.");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["input-needed", goal.Id.Value[..8]],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("human input worklist: 1 open", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("occurrences=1", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("premise-invalid", output, StringComparison.Ordinal);
        Xunit.Assert.Contains(request.Id.Value, output, StringComparison.Ordinal);
        Xunit.Assert.Contains($"answer {request.Id.Value} <answer>", output, StringComparison.Ordinal);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Equal(0, repository.LoadGoalCount);
        Xunit.Assert.Equal(goal.Id, currentGoal!.Id);
    }

    [Xunit.Fact]
    public void ResolveHumanInputRequestGoalPrefixReturnsOnlyOpenRequest()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Resolve one question by goal",
            [new TaskSpec(TaskId.New(), "Ask one question", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = kernel.RequestHumanInput(goal.Id, goal.Tasks.Single().Id, "Proceed?");

        var resolved = OrchestratorEntityResolver.ResolveHumanInputRequest(
            kernel,
            goal.Id.Value[..8]);

        Xunit.Assert.Equal(request.Id, resolved.Id);
    }

    [Xunit.Fact]
    public void ResolveHumanInputRequestAmbiguousGoalListsCandidates()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Resolve multiple questions by goal",
            [
                new TaskSpec(TaskId.New(), "Ask first question", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Ask second question", AgentRole.Tester)
            ]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var first = kernel.RequestHumanInput(goal.Id, goal.Tasks[0].Id, "First?");
        var second = kernel.RequestHumanInput(goal.Id, goal.Tasks[1].Id, "Second?");

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            OrchestratorEntityResolver.ResolveHumanInputRequest(kernel, goal.Id.Value[..8]));

        Xunit.Assert.Contains(first.Id.Value, error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains(second.Id.Value, error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PrintHumanInputWorklist_UsesCustomResumeCommand()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Authenticate provider");
        var request = kernel.RequestHumanInput(
            goal.Id,
            null,
            "Authenticate the provider.",
            HumanWaitKind.ProviderAuth,
            resumeCommand: "provider auth resume");

        var output = CaptureConsole(() =>
            ConsoleViews.PrintHumanInputWorklist(goal, kernel.BuildHumanInputWorklist(goal.Id)));

        Xunit.Assert.Contains("command: provider auth resume", output, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain($"answer {request.Id.Value} <answer>", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ResolveHumanInputRequest_CompletedId_PreservesAnsweredError()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Answer once");
        var request = kernel.RequestHumanInput(goal.Id, null, "Proceed?");
        kernel.SubmitHumanInput(request.Id, "Yes.");

        var resolved = OrchestratorEntityResolver.ResolveHumanInputRequest(kernel, request.Id.Value[..8]);
        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            kernel.SubmitHumanInput(resolved.Id, "Again."));

        Xunit.Assert.Contains("already been answered", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_provenance_loads_completed_goals_on_demand")]
    public void PersistentRunnerProvenanceLoadsCompletedGoalsOnDemand()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var completed = kernel.CreateGoal("Backed completed goal", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active provenance bystander");
        kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
            new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = active;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["provenance"],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalCount);
        Xunit.Assert.Contains(completed.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.DoesNotContain(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Contains("BACKED", output, StringComparison.Ordinal);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_tick_merge_skip_formats_receipt")]
    public void PersistentRunnerTickMergeSkipFormatsReceipt()
    {
        var receipt = CliPersistentStateRunner.FormatTickMergeReceipt(new GoalSnapshotSaveResult(
            "abcdef123456",
            GoalSnapshotSaveDisposition.Skipped,
            null,
            "stored goal no longer contains task 12345678 changed by tick"));

        Xunit.Assert.Equal(
            "TICK_MERGE goal=abcdef12 disposition=SKIPPED stored goal no longer contains task 12345678 changed by tick",
            receipt);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_does_not_treat_completed_metadata_as_landed")]
    public void PersistentRunnerConductLoopDoesNotTreatCompletedMetadataAsLanded()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var completed = kernel.CreateGoal("Completed dependency", [new TaskSpec(TaskId.New(), "Done", AgentRole.Planner)]);
        var active = kernel.CreateGoal("Ready dependent goal", [new TaskSpec(TaskId.New(), "Plan src/Ready.cs", AgentRole.Planner)]);
        var agents = new[] { SubscriptionPlanner("codex-cli", "Planner Codex") };
        kernel.ActivateGoal(completed.Id, agents);
        kernel.ActivateGoal(active.Id, agents);
        kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
            new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
        completed = kernel.GetGoal(completed.Id);
        kernel.SetGoalDependency(active.Id, completed.Id);
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository);
        var plan = CrossGoalSubscriptionStartPlanner.Build(loaded, agents, WorkerProfileCatalog.Default());

        Xunit.Assert.DoesNotContain(completed.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Contains(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Contains(loaded.Goals, goal => goal.Id == active.Id);
        Xunit.Assert.DoesNotContain(loaded.Goals, goal => goal.Id == completed.Id);
        Xunit.Assert.False(loaded.IsKnownCompletedDependencyGoal(completed.Id));
        Xunit.Assert.Empty(plan.FirstBatchCandidates);
        Xunit.Assert.Contains(plan.ParallelPlan.Decisions.SelectMany(decision => decision.Reasons),
            reason => reason.Equals("dependency could not be scheduled", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_routes_retired_without_landing_as_terminal_dependency")]
    public void PersistentRunnerConductLoopRoutesRetiredWithoutLandingAsTerminalDependency()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var dependency = kernel.CreateGoal("Unlanded retired dependency", [new TaskSpec(TaskId.New(), "Done", AgentRole.Planner)]);
        var dependent = kernel.CreateGoal("Held dependent", [new TaskSpec(TaskId.New(), "Plan src/Held.cs", AgentRole.Planner)]);
        var agents = new[] { SubscriptionPlanner("codex-cli", "Planner Codex") };
        kernel.ActivateGoal(dependency.Id, agents);
        kernel.ActivateGoal(dependent.Id, agents);
        kernel.RecordTaskVerification(dependency.Id, dependency.Tasks.Single().Id,
            new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel = WithGoalStatus(kernel, dependency.Id, GoalStatus.Completed);
        kernel.SetGoalDependency(dependent.Id, dependency.Id);
        GoalOperationJournal.RecordTerminalDisposition(
            root,
            dependency,
            new GoalTerminalDisposition(
                GoalTerminalDispositionKind.Retired,
                "Landing could not be verified for the missing branch."));
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
        var plan = CrossGoalSubscriptionStartPlanner.Build(loaded, agents, WorkerProfileCatalog.Default());

        Xunit.Assert.False(loaded.IsKnownCompletedDependencyGoal(dependency.Id));
        Xunit.Assert.True(loaded.TryGetKnownDependencyGoalStatus(dependency.Id, out var status));
        Xunit.Assert.Equal("Retired", status);
        Xunit.Assert.Empty(plan.FirstBatchCandidates);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_keeps_goal_mark_landed_dependency_satisfied")]
    public void PersistentRunnerConductLoopKeepsGoalMarkLandedDependencySatisfied()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var dependency = kernel.CreateGoal("Out-of-band landed dependency", [new TaskSpec(TaskId.New(), "Done", AgentRole.Planner)]);
        var agents = new[] { SubscriptionPlanner("codex-cli", "Planner Codex") };
        kernel.ActivateGoal(dependency.Id, agents);
        kernel.RecordTaskVerification(dependency.Id, dependency.Tasks.Single().Id,
            new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel = WithGoalStatus(kernel, dependency.Id, GoalStatus.Completed);
        GoalOperationJournal.RecordLandingIntent(
            root,
            dependency,
            $"goal/{dependency.Id.Value[..8]}",
            "main",
            "abcdef1234567890",
            "goal-mark-landed");
        GoalOperationJournal.Completed(root, dependency, "conductor:land", "Out-of-band landing verified.");
        GoalOperationJournal.RecordTerminalDisposition(
            root,
            dependency,
            new GoalTerminalDisposition(
                GoalTerminalDispositionKind.Retired,
                "Goal was marked landed out-of-band via goal-mark-landed."));
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);

        Xunit.Assert.True(loaded.IsKnownCompletedDependencyGoal(dependency.Id));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_mark_landed_carries_prompt_budget_through_state_commit")]
    public void PersistentRunnerGoalMarkLandedCarriesPromptBudgetThroughStateCommit()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
            var goal = kernel.CreateGoal("Already landed persistent cleanup", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "dotnet test", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/landed.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var output = CaptureConsole(() =>
            {
                var changed = CliPersistentStateRunner.ExecuteCommand(
                    ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                    repository,
                    CreateRefinedWorkspace(root),
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
                Xunit.Assert.True(changed);
            });

            Xunit.Assert.Equal(2, repository.TransactionCount);
            Xunit.Assert.Contains("Workspace cleanup deferred", output);
            Xunit.Assert.NotNull(GoalWorktrees.TryResolve(root, goal.Id));
            Xunit.Assert.Contains(GoalWorktrees.BranchName(goal.Id), RunGitOutput(root, "branch", "--list", GoalWorktrees.BranchName(goal.Id)), StringComparison.Ordinal);
            Xunit.Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(root, goal.Id));
            var cleanupEntry = GoalOperationJournal.Read(root, goal.Id).LatestByOperation.FirstOrDefault(e =>
                e.Operation == "conductor:cleanup" && e.Status == GoalOperationStatus.Failed);
            Xunit.Assert.NotNull(cleanupEntry);
            Xunit.Assert.Contains("Deferred cleanup after goal-mark-landed", cleanupEntry.Detail, StringComparison.Ordinal);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_mark_landed_returns_success_after_durable_deferred_cleanup")]
    public void PersistentRunnerGoalMarkLandedReturnsSuccessAfterDurableDeferredCleanup()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
            var goal = kernel.CreateGoal("Already landed deferred cleanup", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "dotnet test", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/landed.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");
            GoalOperationJournal.Completed(root, goal, "conductor:record", "recorded");
            GoalOperationJournal.Failed(root, goal, "conductor:cleanup", "Deferred cleanup after landing: cleanup-needed");
            GoalWorktrees.RecordGoalCleanupNeeded(root, goal.Id, "remove:cleanup-budget-exhausted");
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var stderr = CaptureConsoleError(() =>
            {
                var output = CaptureConsole(() =>
                {
                    var changed = CliPersistentStateRunner.ExecuteCommand(
                        ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                        repository,
                        CreateRefinedWorkspace(root),
                        ref agents,
                        providers,
                        ref profiles,
                        ref currentGoal);
                    Xunit.Assert.True(changed);
                });
                Xunit.Assert.Contains("Workspace cleanup deferred", output);
            });

            Xunit.Assert.DoesNotContain("warning: goal-mark-landed state commit failed", stderr);
            Xunit.Assert.Equal(2, repository.TransactionCount);
            Xunit.Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(root, goal.Id));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_mark_landed_returns_success_with_deferred_cleanup_backoff")]
    public void PersistentRunnerGoalMarkLandedReturnsSuccessWithDeferredCleanupBackoff()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
            var goal = kernel.CreateGoal("Already landed cleanup-needed without backoff", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "dotnet test", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/landed.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");
            GoalOperationJournal.Completed(root, goal, "conductor:record", "recorded");
            GoalOperationJournal.Failed(root, goal, "conductor:cleanup", "Deferred cleanup after landing: cleanup-needed");
            GoalWorktrees.RecordGoalCleanupNeeded(root, goal.Id, "remove:cleanup-budget-exhausted");
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var stderr = CaptureConsoleError(() =>
            {
                var output = CaptureConsole(() =>
                {
                    var changed = CliPersistentStateRunner.ExecuteCommand(
                        ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                        repository,
                        CreateRefinedWorkspace(root),
                        ref agents,
                        providers,
                        ref profiles,
                        ref currentGoal);
                    Xunit.Assert.True(changed);
                });
                Xunit.Assert.Contains("Cleanup backoff:", output);
            });

            Xunit.Assert.DoesNotContain("warning: goal-mark-landed state commit failed", stderr);
            Xunit.Assert.Equal(2, repository.TransactionCount);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_mark_landed_state_commit_timeout_requires_recorded_evidence")]
    public void PersistentRunnerGoalMarkLandedStateCommitTimeoutRequiresRecordedEvidence()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
            var goal = kernel.CreateGoal("Already landed missing record evidence", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "dotnet test", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/landed.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            var journalPath = GoalOperationJournal.PathFor(root, goal.Id);
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");
            GoalOperationJournal.Failed(root, goal, "conductor:cleanup", "Deferred cleanup after landing: cleanup-needed");
            GoalWorktrees.RecordGoalCleanupNeeded(root, goal.Id, "remove:cleanup-budget-exhausted");
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var stderr = CaptureConsoleError(() =>
            {
                var output = CaptureConsole(() =>
                {
                    var changed = CliPersistentStateRunner.ExecuteCommand(
                        ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                        repository,
                        CreateRefinedWorkspace(root),
                        ref agents,
                        providers,
                        ref profiles,
                        ref currentGoal);
                    Xunit.Assert.True(changed);
                });
                Xunit.Assert.Contains("Workspace cleanup deferred", output);
            });

            Xunit.Assert.DoesNotContain("warning: goal-mark-landed state commit failed", stderr);
            Xunit.Assert.Equal(2, repository.TransactionCount);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    private sealed class BlockingGoalRefinerProvider : IModelProvider
    {
        public string ProviderName => "blocking-refiner";

        public ManualResetEventSlim Entered { get; } = new(initialState: false);

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Entered.Set();
            Release.Wait(cancellationToken);
            const string response = """
                ```json
                {
                  "behavioralContract": "Create the goal after unlocked refinement.",
                  "acceptanceCriteria": ["The goal is committed atomically."],
                  "verificationClass": "TestVerifiable",
                  "decisions": [],
                  "forks": []
                }
                ```
                """;
            return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
        }
    }

    private sealed class ClarifyingGoalRefinerProvider : IModelProvider
    {
        public string ProviderName => "clarifying-refiner";

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            const string response = """
                ```json
                {
                  "behavioralContract": "Integrate with an external API selected by the operator.",
                  "acceptanceCriteria": ["The selected API contract is implemented."],
                  "verificationClass": "TestVerifiable",
                  "decisions": [],
                  "forks": [
                    {
                      "kind": "external-contract",
                      "topicKey": "goal-create-api-contract",
                      "refinerConfidence": "low",
                      "blastRadius": "high",
                      "question": "Which external API contract should the goal target?",
                      "choice": "",
                      "rationale": "Operator decision required."
                    }
                  ]
                }
                ```
                """;
            return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
        }
    }

    private sealed class DeterministicGoalRefinerProvider : IModelProvider
    {
        public string ProviderName => "deterministic-refiner";

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            const string response = """
                ```json
                {
                  "behavioralContract": "Create a deterministic goal.",
                  "acceptanceCriteria": ["The goal output remains compatible."],
                  "verificationClass": "TestVerifiable",
                  "decisions": [],
                  "forks": []
                }
                ```
                """;
            return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
        }
    }

    private sealed class BarrierGoalRefinerProvider(int expectedCalls) : IModelProvider
    {
        private readonly CountdownEvent _entered = new(expectedCalls);

        public string ProviderName => "barrier-refiner";

        public WaitHandle AllEntered => _entered.WaitHandle;

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            _entered.Signal();
            Release.Wait(cancellationToken);
            const string response = """
                ```json
                {
                  "behavioralContract": "Create exactly one linked goal.",
                  "acceptanceCriteria": ["Exactly one goal is linked to the source backlog item."],
                  "verificationClass": "TestVerifiable",
                  "decisions": [],
                  "forks": []
                }
                ```
                """;
            return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
        }
    }

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

}
