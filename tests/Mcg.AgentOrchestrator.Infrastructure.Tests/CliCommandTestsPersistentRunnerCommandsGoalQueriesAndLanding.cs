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
public sealed class CliCommandTestsPersistentRunnerCommandsGoalQueriesAndLanding : CliCommandTestBase
{
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

    [Xunit.Theory(DisplayName = "CliPersistentStateRunner_tick_conflict_rebases_live_kernel_before_next_action")]
    [Xunit.InlineData(GoalSnapshotSaveDisposition.Merged)]
    [Xunit.InlineData(GoalSnapshotSaveDisposition.Skipped)]
    public void PersistentRunnerTickConflictRebasesLiveKernelBeforeNextAction(
        GoalSnapshotSaveDisposition disposition)
    {
        var kernel = new AgentOrchestratorKernel();
        var testerTaskId = TaskId.New();
        var reviewerTaskId = TaskId.New();
        var goal = kernel.CreateGoal(
            "Do not redispatch an obsolete task after a checkpoint merge",
            [
                new TaskSpec(testerTaskId, "Obsolete Tester", AgentRole.Tester),
                new TaskSpec(reviewerTaskId, "Current Reviewer", AgentRole.Reviewer)
            ]);
        var stale = kernel.ExportGoalSnapshot(goal.Id);
        var persisted = stale with
        {
            Tasks = stale.Tasks.Select(task => task.Id == testerTaskId.Value
                ? task with { Status = WorkTaskStatus.Completed }
                : task with { Status = WorkTaskStatus.Assigned }).ToArray()
        };

        var rebased = CliPersistentStateRunner.RebaseCheckpointAfterDurableSave(
            kernel,
            new GoalSnapshotSaveResult(
                goal.Id.Value,
                disposition,
                persisted,
                "stored version advanced during tick"));

        Xunit.Assert.True(rebased);
        var liveGoal = kernel.GetGoal(goal.Id);
        Xunit.Assert.Equal(
            WorkTaskStatus.Completed,
            liveGoal.Tasks.Single(task => task.Id == testerTaskId).Status);
        Xunit.Assert.Equal(
            WorkTaskStatus.Assigned,
            liveGoal.Tasks.Single(task => task.Id == reviewerTaskId).Status);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_tick_conflict_replaces_goal_scoped_human_input_state")]
    public void PersistentRunnerTickConflictReplacesGoalScopedHumanInputState()
    {
        var seed = new AgentOrchestratorKernel();
        var goal = seed.CreateGoal("Restore authoritative human-input state");
        var authoritative = AgentOrchestratorKernel.FromSnapshot(seed.ExportSnapshot());
        var storedOpen = authoritative.RequestHumanInput(goal.Id, null, "Still awaiting operator input.");
        var storedAnswered = authoritative.RequestHumanInput(goal.Id, null, "Already answered by the operator.");
        authoritative.SubmitHumanInput(storedAnswered.Id, "Approved.");
        var live = AgentOrchestratorKernel.FromSnapshot(seed.ExportSnapshot());
        var staleOnly = live.RequestHumanInput(goal.Id, null, "Created only by the rejected tick.");
        var authoritativeSnapshot = authoritative.ExportSnapshot();

        var rebased = CliPersistentStateRunner.RebaseCheckpointAfterDurableSave(
            live,
            new GoalSnapshotSaveResult(
                goal.Id.Value,
                GoalSnapshotSaveDisposition.Skipped,
                authoritative.ExportGoalSnapshot(goal.Id),
                "stored version advanced during tick",
                authoritativeSnapshot.HumanInputRequests));

        Xunit.Assert.True(rebased);
        var restored = live.ExportSnapshot().HumanInputRequests;
        Xunit.Assert.DoesNotContain(restored, request => request.Id == staleOnly.Id.Value);
        Xunit.Assert.False(restored.Single(request => request.Id == storedOpen.Id.Value).IsCompleted);
        var answered = restored.Single(request => request.Id == storedAnswered.Id.Value);
        Xunit.Assert.True(answered.IsCompleted);
        Xunit.Assert.Equal("Approved.", answered.Answer);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_critical_checkpoint_marks_request_and_allows_matching_baseline")]
    public void PersistentRunnerCriticalCheckpointMarksRequestAndAllowsMatchingBaseline()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Start the selected worker from a matching baseline",
            [new TaskSpec(TaskId.New(), "Selected worker", AgentRole.Reviewer)]);
        var baseline = kernel.ExportGoalSnapshot(goal.Id);
        var repository = new InMemoryTransactionalStateRepository(kernel)
        {
            GoalSnapshotSaveResultFactory = requests => requests.Select(request => new GoalSnapshotSaveResult(
                request.Current.Id,
                GoalSnapshotSaveDisposition.Saved,
                request.Current,
                "saved",
                request.HumanInputRequests)).ToArray()
        };
        var applied = false;

        CliPersistentStateRunner.PersistCriticalGoalSnapshotsOrThrow(
            repository,
            kernel,
            [goal.Id],
            new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal) { [goal.Id.Value] = baseline },
            "C:/fixture/state.db",
            _ => applied = true);

        Xunit.Assert.True(applied);
        Xunit.Assert.True(Xunit.Assert.Single(repository.LastGoalSnapshotSaveRequests!).RejectConflict);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_dynamic_load_captures_only_missing_tick_baselines")]
    public void PersistentRunnerDynamicLoadCapturesOnlyMissingTickBaselines()
    {
        var loaded = new AgentOrchestratorKernel();
        var existing = loaded.CreateGoal("Existing tick goal");
        var originalExisting = loaded.ExportGoalSnapshot(existing.Id);
        var baselines = new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal)
        {
            [existing.Id.Value] = originalExisting
        };
        loaded.RequestHumanInput(existing.Id, null, "External update after tick start.");
        var added = loaded.CreateGoal("Dynamically ingested goal");

        CliPersistentStateRunner.CaptureMissingConductLoopTickBaselines(baselines, loaded);

        Xunit.Assert.Equal(originalExisting, baselines[existing.Id.Value]);
        Xunit.Assert.Equal(
            JsonSerializer.Serialize(loaded.ExportGoalSnapshot(added.Id)),
            JsonSerializer.Serialize(baselines[added.Id.Value]));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_critical_checkpoint_fails_closed_without_tick_baseline")]
    public void PersistentRunnerCriticalCheckpointFailsClosedWithoutTickBaseline()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Never dispatch without a tick baseline");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var error = Xunit.Assert.Throws<DispatchCheckpointConflictException>(() =>
            CliPersistentStateRunner.PersistCriticalGoalSnapshotsOrThrow(
                repository,
                kernel,
                [goal.Id],
                new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal),
                "C:/fixture/state.db",
                _ => throw new Xunit.Sdk.XunitException("No durable result may be applied without a baseline.")));

        Xunit.Assert.Contains("no tick baseline", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Null(repository.LastGoalSnapshotSaveRequests);
    }

    [Xunit.Fact(DisplayName = "CliExecutionContext_critical_checkpoint_fails_closed_without_durable_writer")]
    public void CliExecutionContextCriticalCheckpointFailsClosedWithoutDurableWriter()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Do not acknowledge a missing critical writer");
        var context = new CliExecutionContext(
            kernel,
            OrchestratorWorkspace.ForDirectory(root),
            new InMemoryModelProviderRegistry([]),
            [],
            WorkerProfileCatalog.Default(),
            goal);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            context.PersistCriticalGoalCheckpoint(kernel, [goal.Id]));

        Xunit.Assert.Contains("worker start was aborted", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_single_goal_conduct_uses_strict_critical_checkpoint")]
    public void PersistentRunnerSingleGoalConductUsesStrictCriticalCheckpoint()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        EnsureGitRepository(root);
        RunGit(root, "add", ".agents/skills");
        RunGit(root, "commit", "-m", "add test skills");
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Run only from authoritative state.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Fence one-shot conduct dispatch", [task]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "One-shot conduct checkpoint fixture is already refined.",
            ["The selected worker cannot start after an authoritative task update."],
            VerificationClass.TestVerifiable,
            [],
            []));
        var agent = SubscriptionPlanner("safe-profile", "Safe Planner");
        IReadOnlyList<AgentDefinition> agents = [agent];
        kernel.ActivateGoal(goal.Id, agents);
        GoalWorktrees.Ensure(root, goal.Id);

        var authoritativeKernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        authoritativeKernel.ReportTaskProgress(
            goal.Id,
            task.Id,
            WorkTaskStatus.Completed,
            "Operator completed the task before dispatch.");
        authoritativeKernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        var authoritative = authoritativeKernel.ExportGoalSnapshot(goal.Id);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var criticalRequests = 0;
        repository.GoalSnapshotSaveResultFactory = requests => requests.Select(request =>
        {
            criticalRequests++;
            Xunit.Assert.True(request.RejectConflict);
            return new GoalSnapshotSaveResult(
                request.Current.Id,
                GoalSnapshotSaveDisposition.Skipped,
                authoritative,
                "stored version advanced before one-shot conduct dispatch",
                []);
        }).ToArray();
        var workerProfiles = new WorkerProfileCatalog(
            [new WorkerProfile("safe-profile", "Write-Output {subscriptionModelName}; Write-Output {promptPath}")]);
        Goal? currentGoal = goal;

        var changed = CliPersistentStateRunner.ExecuteCommand(
            ["conduct", goal.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([]),
            ref workerProfiles,
            ref currentGoal);

        Xunit.Assert.True(changed);
        Xunit.Assert.True(criticalRequests > 0);
        Xunit.Assert.Equal(0, repository.TransactGoalCount);
        Xunit.Assert.DoesNotContain(
            workspace.LogDirectory is { } logDirectory && Directory.Exists(logDirectory)
                ? Directory.GetFiles(logDirectory, "*.start-gate", SearchOption.AllDirectories)
                : [],
            _ => true);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_critical_checkpoint_accepts_own_paid_retry_reservation_and_start_claim")]
    public void PersistentRunnerCriticalCheckpointAcceptsOwnPaidRetryReservationAndStartClaim()
    {
        var root = CreateTempDirectory();
        var databasePath = Path.Combine(root, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        var at = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry paid work after a source finding.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Accept the dispatch's own reservation write", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RetryTask(goal.Id, task.Id, "Repair the source finding.", RetryCause.NewSourceFinding);
        var fingerprint = RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
            goal.Id.Value,
            task.Id.Value,
            task.RequiredRole,
            "OpenAI",
            AgentCatalog.OpenAiSolSubscriptionModelAlias,
            PaidRouteClassification.Paid,
            "candidate",
            "criteria",
            [],
            [],
            [],
            [],
            "base",
            "main"));
        var dispatch = new TaskDispatchRecord(
            "worker",
            "Write-Output safe",
            root,
            at,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);
        var repository = new SqliteOrchestratorStateRepository(databasePath);
        repository.SaveAsync(kernel).GetAwaiter().GetResult();
        var tickBaseline = kernel.ExportGoalSnapshot(goal.Id);
        var tickBaselines = new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal)
        {
            [goal.Id.Value] = tickBaseline
        };
        var reservation = RetryAdmissionReservationStore.TryReserveAsync(
                databasePath,
                goal.Id,
                task.Id,
                fingerprint,
                PaidRouteClassification.Paid,
                RetryCause.NewSourceFinding,
                dispatch,
                at,
                "owner-a",
                at.AddMinutes(1))
            .GetAwaiter()
            .GetResult();
        var persisted = Xunit.Assert.IsType<RetryAdmissionSnapshotResult>(reservation);
        kernel.ReplaceGoalStateWithSnapshot(persisted.Snapshot, persisted.HumanInputRequests ?? []);
        tickBaselines[goal.Id.Value] = persisted.Snapshot;
        IReadOnlyList<GoalSnapshotSaveResult>? applied = null;

        CliPersistentStateRunner.PersistCriticalGoalSnapshotsOrThrow(
            repository,
            kernel,
            [goal.Id],
            tickBaselines,
            databasePath,
            results => applied = results);

        var result = Xunit.Assert.Single(applied!);
        Xunit.Assert.Equal(GoalSnapshotSaveDisposition.Saved, result.Disposition);
        Xunit.Assert.Single(result.PersistedSnapshot!.Tasks.Single().RetryAdmissionHistory!);

        var claim = RetryAdmissionReservationStore.TryClaimStartSnapshotAsync(
                databasePath,
                goal.Id,
                task.Id,
                dispatch.DispatchedAt,
                "owner-a",
                at.AddSeconds(1))
            .GetAwaiter()
            .GetResult();
        var claimed = Xunit.Assert.IsType<RetryAdmissionStartClaimResult>(claim);
        Xunit.Assert.True(claimed.Claimed);
        kernel.ReplaceGoalWithSnapshot(claimed.Snapshot);
        tickBaselines[goal.Id.Value] = claimed.Snapshot;
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(
                4101,
                dispatch.Command,
                dispatch.WorkingDirectory,
                Path.Combine(root, "worker.out.log"),
                Path.Combine(root, "worker.err.log"),
                Path.Combine(root, "worker.exit"),
                at.AddSeconds(1),
                null,
                null));
        applied = null;

        CliPersistentStateRunner.PersistCriticalGoalSnapshotsOrThrow(
            repository,
            kernel,
            [goal.Id],
            tickBaselines,
            databasePath,
            results => applied = results);

        result = Xunit.Assert.Single(applied!);
        Xunit.Assert.Equal(GoalSnapshotSaveDisposition.Saved, result.Disposition);
        var startedTask = result.PersistedSnapshot!.Tasks.Single();
        Xunit.Assert.NotNull(startedTask.LastProcess);
        Xunit.Assert.NotNull(Xunit.Assert.Single(startedTask.RetryAdmissionHistory!).WorkerStartClaimedAt);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_critical_checkpoint_rebases_before_conflict_abort")]
    public void PersistentRunnerCriticalCheckpointRebasesBeforeConflictAbort()
    {
        var kernel = new AgentOrchestratorKernel();
        var taskId = TaskId.New();
        var goal = kernel.CreateGoal(
            "Abort a stale worker start",
            [new TaskSpec(taskId, "Obsolete worker", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var baseline = kernel.ExportGoalSnapshot(goal.Id);
        kernel.RecordTaskDispatch(
            goal.Id,
            taskId,
            new TaskDispatchRecord("stale-worker", "fixture", "C:/fixture", DateTimeOffset.UtcNow));
        var storedOpen = new HumanInputRequestSnapshot(
            HumanInputRequestId.New().Value,
            goal.Id.Value,
            null,
            "Authoritative operator wait.",
            DateTimeOffset.UtcNow);
        var authoritative = baseline with
        {
            Tasks = baseline.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray()
        };
        var repository = new InMemoryTransactionalStateRepository(kernel)
        {
            GoalSnapshotSaveResultFactory = requests =>
            {
                Xunit.Assert.True(Xunit.Assert.Single(requests).RejectConflict);
                return
                [
                    new GoalSnapshotSaveResult(
                        goal.Id.Value,
                        GoalSnapshotSaveDisposition.Skipped,
                        authoritative,
                        "stored version advanced during tick",
                        [storedOpen])
                ];
            }
        };
        var applied = false;

        _ = Xunit.Assert.Throws<DispatchCheckpointConflictException>(() =>
            CliPersistentStateRunner.PersistCriticalGoalSnapshotsOrThrow(
                repository,
                kernel,
                [goal.Id],
                new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal) { [goal.Id.Value] = baseline },
                "C:/fixture/state.db",
                results =>
                {
                    applied = true;
                    foreach (var result in results)
                        CliPersistentStateRunner.RebaseCheckpointAfterDurableSave(kernel, result);
                }));

        Xunit.Assert.True(applied);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, kernel.GetGoal(goal.Id).Tasks.Single().Status);
        Xunit.Assert.Null(kernel.GetGoal(goal.Id).Tasks.Single().LastDispatch);
        Xunit.Assert.Equal(storedOpen.Id, Xunit.Assert.Single(kernel.ExportSnapshot().HumanInputRequests).Id);
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

}
