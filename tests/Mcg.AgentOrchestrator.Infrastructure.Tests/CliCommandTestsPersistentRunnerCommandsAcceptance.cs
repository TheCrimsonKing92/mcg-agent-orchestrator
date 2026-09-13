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

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsAcceptance : CliCommandTestBase
{
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


}
