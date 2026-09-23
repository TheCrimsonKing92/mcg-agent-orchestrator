using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;


public sealed class GoalWorktreeTestsAcceptanceLanding : GoalWorktreeTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData("Completed", "passed", DogfoodAcceptanceDisposition.Passed)]
    [Xunit.InlineData("Completed", "gate-passed", DogfoodAcceptanceDisposition.Passed)]
    [Xunit.InlineData("Failed", "failed", DogfoodAcceptanceDisposition.Failed)]
    [Xunit.InlineData("Failed", "blocked:timeout", DogfoodAcceptanceDisposition.Inconclusive)]
    [Xunit.InlineData("Aborted", "aborted:state-guard", DogfoodAcceptanceDisposition.Inconclusive)]
    [Xunit.InlineData("Completed", "failed", DogfoodAcceptanceDisposition.Inconclusive)]
    public void AcceptanceProjectionMapsTypedOutcome(
        string statusName,
        string outcome,
        DogfoodAcceptanceDisposition expected)
    {
        var goalId = GoalId.New();
        var status = Enum.Parse<GoalOperationStatus>(statusName);
        var entry = new GoalOperationJournalEntry(
            "receipt",
            goalId,
            "acceptance",
            status,
            DateTimeOffset.Parse("2026-08-09T12:00:00Z", CultureInfo.InvariantCulture),
            "detail is not classified",
            AcceptanceOutcome: outcome);
        var journal = new GoalOperationJournalSummary("unused", [entry], [entry], []);

        var evidence = GoalLandingPostActions.ResolveDogfoodLandingEvidence(journal);

        Assert.Equal(expected, evidence.AcceptanceDisposition);
        Assert.Equal(outcome, evidence.AcceptanceOutcome);
    }

    [Xunit.Fact]
    public void AcceptanceProjectionUsesNewestTypedReceipt()
    {
        var goalId = GoalId.New();
        var older = new GoalOperationJournalEntry(
            "older",
            goalId,
            "acceptance",
            GoalOperationStatus.Completed,
            DateTimeOffset.Parse("2026-08-09T12:00:00Z", CultureInfo.InvariantCulture),
            null,
            AcceptanceOutcome: "passed");
        var newer = new GoalOperationJournalEntry(
            "newer",
            goalId,
            "acceptance",
            GoalOperationStatus.Failed,
            older.At.AddMinutes(1),
            null,
            AcceptanceOutcome: "failed");
        var journal = new GoalOperationJournalSummary("unused", [older, newer], [older, newer], []);

        var evidence = GoalLandingPostActions.ResolveDogfoodLandingEvidence(journal);

        Assert.Equal(DogfoodAcceptanceDisposition.Failed, evidence.AcceptanceDisposition);
        Assert.Equal("failed", evidence.AcceptanceOutcome);
    }

    [Xunit.Fact]
    public void AcceptanceProjectionDoesNotTreatAutomaticMergeEvidenceAsManualLanding()
    {
        var goalId = GoalId.New();
        var terminalDisposition = new GoalTerminalDisposition(
            GoalTerminalDispositionKind.Landed,
            "Goal terminalized from merge evidence.",
            GoalTerminalDispositionSource.MergeEvidence);
        var entry = new GoalOperationJournalEntry(
            "terminal-disposition",
            goalId,
            GoalOperationJournal.TerminalDispositionOperation,
            GoalOperationStatus.Completed,
            DateTimeOffset.Parse("2026-08-09T12:00:00Z", CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(terminalDisposition));
        var journal = new GoalOperationJournalSummary("unused", [entry], [entry], []);

        var evidence = GoalLandingPostActions.ResolveDogfoodLandingEvidence(journal);

        Assert.False(evidence.WasManuallyLanded);
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_retry_treats_landed_cleaned_missing_worktree_as_accepted")]
    public void CliAcceptanceRetryTreatsLandedCleanedMissingWorktreeAsAccepted()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Already landed cleaned acceptance retry", repo);
            kernel.RecordAcceptanceFailure(goal.Id, ["acceptance evidence blocked"]);
            GoalOperationJournal.Completed(repo, goal, "acceptance", "Acceptance passed and merge completed.");
            GoalOperationJournal.Completed(repo, goal, "workspace:remove", "Workspace removed.");

            var verifierRuns = 0;
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                CleanupContext = CreateIsolatedCleanupContext(workspace.ExecutionDirectory),
                AcceptanceVerifier = FakeAcceptanceVerifier.Failed("should not run", onRun: () => verifierRuns++)
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", goal.Id.Value[..8], "--keep-workspace"], context));

            Assert.Equal(0, verifierRuns);
            Assert.Contains("Acceptance repaired:", output);
            Assert.DoesNotContain("Acceptance evidence: blocked", output);
            Assert.Null(kernel.GetGoal(goal.Id).LatestAcceptanceFailure);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_lands_after_transient_state_write_lock_releases")]
    public async Task CliAcceptanceLandsAfterTransientStateWriteLockReleases()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance transient lock test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "transient-lock.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            using var lockConnection = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Mode=ReadWrite;Pooling=False;");
            lockConnection.Open();
            using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN IMMEDIATE";
            lockCommand.ExecuteNonQuery();

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                CleanupContext = CreateIsolatedCleanupContext(workspace.ExecutionDirectory),
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed()
            };

            // A dedicated thread, not the pool: the command blocks synchronously for as long
            // as this test holds the write lock, so on the pool its start would depend on
            // pool growth under whatever else the lane happens to be running.
            var acceptanceTask = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptanceThread = new Thread(() =>
            {
                try
                {
                    acceptanceTask.SetResult(CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context)));
                }
                catch (Exception exception)
                {
                    acceptanceTask.SetException(exception);
                }
            })
            { IsBackground = true };
            acceptanceThread.Start();

            // Minimum hold, not a timeout: claiming the acceptance lease is the command's
            // first state write, so holding the lock past this point is what makes the
            // release path under test actually execute.
            Assert.False(ReferenceEquals(
                acceptanceTask.Task,
                await Task.WhenAny(acceptanceTask.Task, Task.Delay(TimeSpan.FromMilliseconds(100)))));

            using var releaseCommand = lockConnection.CreateCommand();
            releaseCommand.CommandText = "COMMIT";
            releaseCommand.ExecuteNonQuery();

            var output = await acceptanceTask.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Contains("Acceptance evidence bundle: passed", output);
            Assert.Contains("Fast-forwarded", output);
            Assert.True(File.Exists(Path.Combine(repo, "transient-lock.txt")));
            Assert.Equal(GoalStatus.Completed, goal.Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_profile_dispatch_auto_creates_workspace_when_missing")]
    public void CliProfileDispatchAutoCreatesWorkspaceWhenMissing()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Dispatch auto-create test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            MarkGoalRefined(kernel, goal);
            kernel.ActivateGoal(goal.Id, [EchoDeveloper()]);

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                CleanupContext = CreateIsolatedCleanupContext(workspace.ExecutionDirectory)
            };

            // Dispatch must own workspace creation: none exists yet.
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["profile-dispatch", "1", "local"], context));

            Assert.True(output.Contains("Workspace auto-created", StringComparison.Ordinal));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_auto_verifies_from_git_evidence")]
    public void CliAcceptanceAutoVerifiesFromGitEvidence()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Auto verify test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            // No manual verification recorded: the goal is not Completed and the task is Assigned.
            Assert.Equal(GoalStatus.Active, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            // Acceptance derives the verification from git evidence (committed change + clean worktree).
            Assert.True(output.Contains("Auto-verified", StringComparison.Ordinal));
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Gated_progressive_glance_preserves_worktree_and_allows_acceptance_completion")]
    public void GatedProgressiveGlancePreservesWorktreeAndAllowsAcceptanceCompletion()
    {
        var repo = CreateSeededRepository();
        try
        {
            var now = new DateTimeOffset(2026, 8, 3, 4, 20, 0, TimeSpan.Zero);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(
                TaskId.New(),
                "Implement the gated diagnostic.\n\nTarget files/scopes\nIncludes:\n- feature.txt",
                AgentRole.Developer);
            var goal = kernel.CreateGoal("Honor the operator hypothesis gate", [task]);
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
                "Instrument before suppressing",
                ["wire hidden-console acquisition after confirmation"],
                VerificationClass.TestVerifiable,
                [],
                []));
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "diagnostic only");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Add gated diagnostic");
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord(
                    "codex-cli",
                    "codex exec",
                    worktreePath,
                    now.AddMinutes(-10),
                    BaseCommit: "base"));
            var clarification = kernel.RequestHumanInput(
                goal.Id,
                null,
                "Should console suppression ship before the hypothesis is confirmed?");
            kernel.SubmitHumanInput(
                clarification.Id,
                "HYPOTHESIS GATE: do not implement suppression until confirmed.",
                ["hidden-console-spawn"]);

            var runner = new GatedWorkflowGlanceRunner(new ProgressiveReviewGlanceDispatchResult(
                ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
                "hidden-console acquisition is not wired into the max-duration spawn path",
                "missing hidden-console acquisition",
                ReasonCode: ProgressiveReviewGlanceReasonCode.UnmentionedWork,
                Findings:
                [
                    new ProgressiveReviewGlanceFinding(
                        "hidden-console-spawn",
                        "hidden-console acquisition is not wired into the max-duration spawn path")
                ]));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var steeringStore = new InMemoryProgressiveReviewSteeringStore();
            var coordinator = new ProgressiveReviewGlanceCoordinator(
                runner,
                new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory),
                new CollaborationItemStore(Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db")),
                new ProgressiveReviewGlanceOptions(FirstElapsedThreshold: TimeSpan.Zero),
                () => now,
                (_, _) => new DispatchLiveChangeSnapshot(["feature.txt"], ["feature.txt"], 0),
                (_, _) => "diagnostic-only diff",
                _ => "worker remains live",
                steeringStore);

            _ = coordinator.Observe(kernel, [goal]);
            _ = coordinator.Observe(kernel, [goal]);

            Assert.Equal(1, runner.CallCount);
            Assert.Empty(steeringStore.Intents);
            Assert.Equal(WorkTaskStatus.Running, task.Status);
            Assert.Equal(worktreePath, GoalWorktrees.TryResolve(repo, goal.Id));
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", worktreePath, now));
            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["acceptance", goal.Id.Value[..8], "--keep-workspace"],
                CreateAcceptanceContext(kernel, repo, goal)));

            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Contains("Fast-forwarded", output, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_does_not_auto_verify_without_committed_changes")]
    public void CliAcceptanceDoesNotAutoVerifyWithoutCommittedChanges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("No change auto verify test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            GoalWorktrees.Ensure(repo, goal.Id); // worktree exists but has no commits against the base branch

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            // No committed work → no auto-verify → the goal stays un-accepted (anti-fabrication preserved).
            Assert.False(output.Contains("Auto-verified", StringComparison.Ordinal));
            Assert.Equal(GoalStatus.Active, goal.Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_auto_records_dogfood_entry")]
    public async Task CliAcceptanceAutoRecordsDogfoodEntry()
    {
        var repo = CreateSeededRepository();
        try
        {
            File.WriteAllText(Path.Combine(repo, "DOGFOOD_LOG.md"), "# Dogfood Log" + Environment.NewLine);
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Add dogfood log");

            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Autorecord distinctive objective", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--keep-workspace"], context));

            Assert.True(output.Contains("Recorded dogfood-log entry", StringComparison.Ordinal));
            var record = await new DogfoodLogStore(context.Workspace.DogfoodLogStorePath)
                .GetByGoalIdAsync(goal.Id.Value);
            Assert.NotNull(record);
            Assert.Contains("Autorecord distinctive objective", record!.RenderedMarkdown);
            Assert.Contains("Acceptance passed.", record.RenderedMarkdown);
            var log = File.ReadAllText(Path.Combine(repo, "DOGFOOD_LOG.md"));
            Assert.Equal("# Dogfood Log" + Environment.NewLine, log);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_no_record_skips_dogfood_entry")]
    public async Task CliAcceptanceNoRecordSkipsDogfoodEntry()
    {
        var repo = CreateSeededRepository();
        try
        {
            File.WriteAllText(Path.Combine(repo, "DOGFOOD_LOG.md"), "# Dogfood Log" + Environment.NewLine);
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Add dogfood log");

            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "No record objective", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--keep-workspace", "--no-record"], context));

            var record = await new DogfoodLogStore(context.Workspace.DogfoodLogStorePath)
                .GetByGoalIdAsync(goal.Id.Value);
            Assert.Null(record);
            var log = File.ReadAllText(Path.Combine(repo, "DOGFOOD_LOG.md"));
            Assert.False(log.Contains("## ", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_recover_resets_failed_task_to_dispatchable")]
    public void CliRecoverResetsFailedTaskToDispatchable()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Recover test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "boom");
            Assert.Equal(WorkTaskStatus.Failed, task.Status);

            var context = CreateAcceptanceContext(kernel, repo, goal);
            CaptureConsole(() => CliCommandHandlers.Execute(["recover", goal.Id.Value[..8], "redo the work"], context));

            // One recover call brings the failed task back to a dispatchable state.
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_conduct_scoped_reconciles_exited_dispatch_before_advance")]
    public void CliConductScopedReconcilesExitedDispatchBeforeAdvance()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Scoped conduct reconcile", [new TaskSpec(TaskId.New(), "Inspect", AgentRole.Researcher)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var stdout = Path.Combine(repo, "planner.out.log");
            var stderr = Path.Combine(repo, "planner.err.log");
            var exit = Path.Combine(repo, "planner.exit.txt");
            File.WriteAllText(stdout, "plan complete");
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "0");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", repo, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(
                    999999,
                    "codex exec prompt.md",
                    repo,
                    stdout,
                    stderr,
                    exit,
                    startedAt,
                    null,
                    null,
                    OwnedProcessIds: [999999]));

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["conduct", goal.Id.Value[..8]], context));

            var refreshedTask = kernel.GetTask(goal.Id, task.Id);
            Assert.Equal(WorkTaskStatus.Completed, refreshedTask.Status);
            Assert.Equal(0, refreshedTask.LastVerification!.ExitCode);
            Assert.True(output.Contains("[conduct] Reconciled 1 exited dispatch", StringComparison.Ordinal));
            Assert.True(context.RunInjectedAcceptanceVerifierInCurrentProcess);
            Assert.Equal(
                0,
                Assert.IsType<FakeAcceptanceVerifier>(context.AcceptanceVerifier).RunCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_recover_reconciles_dead_running_task_with_exit_file")]
    public void CliRecoverReconcilesDeadRunningTaskWithExitFile()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Recover unreconciled dispatch", [new TaskSpec(TaskId.New(), "Inspect", AgentRole.Researcher)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var stdout = Path.Combine(repo, "planner.out.log");
            var stderr = Path.Combine(repo, "planner.err.log");
            var exit = Path.Combine(repo, "planner.exit.txt");
            File.WriteAllText(stdout, "plan complete");
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "0");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", repo, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(
                    999999,
                    "codex exec prompt.md",
                    repo,
                    stdout,
                    stderr,
                    exit,
                    startedAt,
                    null,
                    null,
                    OwnedProcessIds: [999999]));

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["recover", goal.Id.Value[..8], "reconcile finished dispatch"], context));

            var refreshedTask = kernel.GetTask(goal.Id, task.Id);
            Assert.Equal(WorkTaskStatus.Completed, refreshedTask.Status);
            Assert.Equal(0, refreshedTask.LastVerification!.ExitCode);
            Assert.False(output.Contains("nothing to recover", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_recover_resets_cancelled_tasks_without_disturbing_completed_or_running_tasks")]
    public void CliRecoverResetsCancelledTasksWithoutDisturbingCompletedOrRunningTasks()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelledOne = new TaskSpec(TaskId.New(), "Cancelled one", AgentRole.Planner);
            var cancelledTwo = new TaskSpec(TaskId.New(), "Cancelled two", AgentRole.Researcher);
            var completed = new TaskSpec(TaskId.New(), "Completed", AgentRole.Developer);
            var running = new TaskSpec(TaskId.New(), "Running", AgentRole.Tester);
            var goal = kernel.CreateGoal("Recover cancelled tasks", [cancelledOne, cancelledTwo, completed, running]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);

            kernel.ReportTaskProgress(goal.Id, completed.Id, WorkTaskStatus.Completed, "Already done.");
            RecordCancelledProcess(kernel, goal.Id, cancelledOne.Id, 111, repo);
            RecordCancelledProcess(kernel, goal.Id, cancelledTwo.Id, 222, repo);
            using var runningProcess = StartLongRunningHelper();
            var runningStartedAt = DateTimeOffset.UtcNow;
            try
            {
                kernel.RecordTaskDispatch(
                    goal.Id,
                    running.Id,
                    new TaskDispatchRecord("codex-cli", "codex exec prompt.md", repo, runningStartedAt));
                kernel.RecordTaskProcessStarted(
                    goal.Id,
                    running.Id,
                    new TaskProcessRecord(
                        runningProcess.Id,
                        "codex exec prompt.md",
                        repo,
                        Path.Combine(repo, "running.out.log"),
                        Path.Combine(repo, "running.err.log"),
                        Path.Combine(repo, "running.exit.txt"),
                        runningStartedAt,
                        null,
                        null));

                var context = CreateAcceptanceContext(kernel, repo, goal);
                CaptureConsole(() => CliCommandHandlers.Execute(["recover", goal.Id.Value[..8], "retry cancelled work"], context));

                Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, cancelledOne.Id).Status);
                Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, cancelledTwo.Id).Status);
                Assert.Equal(WorkTaskStatus.Completed, kernel.GetTask(goal.Id, completed.Id).Status);
                var runningTask = kernel.GetTask(goal.Id, running.Id);
                Assert.Equal(WorkTaskStatus.Running, runningTask.Status);
                Assert.True(runningTask.LastProcess is { IsRunning: true });
            }
            finally
            {
                StopProcess(runningProcess);
            }
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_queue_applies_ready_goals_sequentially")]
    public void CliAcceptanceQueueAppliesReadyGoalsSequentially()
    {
        var repo = CreateReducedAcceptanceCohortRepository(renameInitialBranchToMain: false);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "First queued acceptance", repo);
            var second = CreateCompletedGoal(kernel, "Second queued acceptance", repo);

            var firstPath = GoalWorktrees.Ensure(repo, first.Id);
            File.WriteAllText(Path.Combine(firstPath, "first.txt"), "first");
            RunGit(firstPath, "add", "-A");
            RunGit(firstPath, "commit", "-m", "First queued goal");

            RunGit(repo, "branch", GoalWorktrees.BranchName(second.Id), GoalWorktrees.BranchName(first.Id));
            var secondPath = GoalWorktrees.Ensure(repo, second.Id);
            File.WriteAllText(Path.Combine(secondPath, "second.txt"), "second");
            RunGit(secondPath, "add", "-A");
            RunGit(secondPath, "commit", "-m", "Second queued goal");

            var context = CreateAcceptanceContext(kernel, repo, first);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["acceptance-queue", "--apply", "--confirm-acceptance-queue"],
                context));

            Assert.True(output.Contains("Acceptance queue: 2 goal(s), ready=2, held=0, blocked=0", StringComparison.Ordinal));
            Assert.True(output.Contains($"Acceptance queue goal {first.Id.Value[..8]}:", StringComparison.Ordinal));
            Assert.True(output.Contains($"Acceptance queue goal {second.Id.Value[..8]}:", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "first.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "second.txt")));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, first.Id));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, second.Id));
            Assert.True(BranchExists(repo, GoalWorktrees.BranchName(first.Id)));
            Assert.True(BranchExists(repo, GoalWorktrees.BranchName(second.Id)));
            Assert.True(HasAnyCleanupNeededRecord(repo, GoalWorktrees.WorktreePath(repo, first.Id)));
            Assert.True(HasAnyCleanupNeededRecord(repo, GoalWorktrees.WorktreePath(repo, second.Id)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "AcceptanceQueuePlanner_batches_goal_branch_facts_once_per_plan")]
    public void AcceptanceQueuePlannerBatchesGoalBranchFactsOncePerPlan()
    {
        var repo = CreateSeededRepository();
        var originalRunner = GoalGitFactIndex.GitRunner;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var present = kernel.CreateGoal("Present branch queued acceptance filter", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            var alsoPresent = kernel.CreateGoal("Also present branch queued acceptance filter", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            var missing = kernel.CreateGoal("Missing branch queued acceptance filter", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            RunGit(repo, "branch", GoalWorktrees.BranchName(present.Id));
            RunGit(repo, "branch", GoalWorktrees.BranchName(alsoPresent.Id));
            var batchedGitCalls = new List<string>();

            GoalGitFactIndex.GitRunner = (workingDirectory, args) =>
            {
                if (Path.GetFullPath(workingDirectory).Equals(Path.GetFullPath(repo), StringComparison.OrdinalIgnoreCase))
                {
                    batchedGitCalls.Add(string.Join(" ", args));
                }

                var command = string.Join(" ", args);
                if (command == "for-each-ref --format=%(refname:short) %(objectname) refs/heads/goal/")
                {
                    return new GitCli.GitResult(
                        0,
                        $"""
                        {GoalWorktrees.BranchName(present.Id)} 1111111111111111111111111111111111111111
                        {GoalWorktrees.BranchName(alsoPresent.Id)} 2222222222222222222222222222222222222222
                        """,
                        string.Empty);
                }

                return originalRunner(workingDirectory, args);
            };

            var plan = AcceptanceQueuePlanner.Build(kernel, repo, AutonomyPolicy.SupervisedAuto);

            Assert.Equal(2, plan.Items.Count);
            Assert.Contains(plan.Items, item => item.GoalId == present.Id);
            Assert.Contains(plan.Items, item => item.GoalId == alsoPresent.Id);
            Assert.DoesNotContain(plan.Items, item => item.GoalId == missing.Id);
            Assert.Equal(1, batchedGitCalls.Count(call => call == "for-each-ref --format=%(refname:short) %(objectname) refs/heads/goal/"));
            Assert.Equal(1, batchedGitCalls.Count(call => call == "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/"));
            Assert.Equal(1, batchedGitCalls.Count(call => call == "worktree list --porcelain"));
            Assert.DoesNotContain(batchedGitCalls, call => call.StartsWith("rev-parse --verify --quiet refs/heads/goal/", StringComparison.Ordinal));
        }
        finally
        {
            GoalGitFactIndex.GitRunner = originalRunner;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_conduct_completed_goal_lands_through_persistent_runner_without_command_transaction")]
    public async Task CliConductCompletedGoalLandsThroughPersistentRunnerWithoutCommandTransaction()
    {
        var repo = CreateSeededRepository();
        try
        {
            RunGit(repo, "branch", "-M", "main");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Conduct completed goal persistence", repo);
            kernel.RecordAcceptanceFailure(goal.Id, ["previous verifier failure"]);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "conduct-persist.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Conduct persistence goal");

            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() =>
            {
                var changed = CliPersistentStateRunner.ExecuteCommand(
                    ["conduct", goal.Id.Value[..8], "--policy", "Permissive"],
                    stateRepository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal,
                    acceptanceVerifier: FakeAcceptanceVerifier.Passed(),
                    acceptanceCleanupContext: CreateIsolatedCleanupContext(workspace.ExecutionDirectory));
                Assert.True(changed);
            });

            Assert.Contains("Conduct " + goal.Id.Value[..8], output);
            Assert.Contains("Landed:", output);
            Assert.True(File.Exists(Path.Combine(repo, "conduct-persist.txt")));

            var reloaded = await stateRepository.LoadAsync();
            var reloadedGoal = reloaded.GetGoal(goal.Id);
            Assert.Null(reloadedGoal.LatestAcceptanceFailure);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_queue_safe_auto_holds_irreversible_actions")]
    public void CliAcceptanceQueueSafeAutoHoldsIrreversibleActions()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Policy queued acceptance", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "policy-queue.txt"), "goal");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Policy queued goal");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance-queue", "--autonomy", "safe-auto"], context));

            Assert.True(output.Contains("ready=0, held=1, blocked=0, policy=safe-auto", StringComparison.Ordinal));
            Assert.True(output.Contains("blocks irreversible acceptance or cleanup", StringComparison.Ordinal));
            Assert.True(output.Contains("--autonomy supervised-auto --confirm-acceptance-queue", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "policy-queue.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_evidence_blocks_generated_artifact_changes")]
    public void CliAcceptanceEvidenceBlocksGeneratedArtifactChanges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance evidence generated artifact test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            var generatedDirectory = Path.Combine(worktreePath, "src", "Feature", "bin", "Debug");
            Directory.CreateDirectory(generatedDirectory);
            var generatedFile = Path.Combine(generatedDirectory, "generated.dll");
            File.WriteAllText(generatedFile, "generated");
            RunGit(worktreePath, "add", "-f", generatedFile);
            RunGit(worktreePath, "commit", "-m", "Generated artifact");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                CleanupContext = CreateIsolatedCleanupContext(workspace.ExecutionDirectory),
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("generated-artifacts", StringComparison.Ordinal));
            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "src", "Feature", "bin", "Debug", "generated.dll")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_releases_state_write_lock_during_verification")]
    public void CliAcceptanceReleasesStateWriteLockDuringVerification()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance concurrency test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "concurrency.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            Exception? readFailure = null;
            Exception? writeFailure = null;
            TimeSpan? readElapsed = null;
            TimeSpan? writeElapsed = null;
            var fakeVerifier = FakeAcceptanceVerifier.Passed(onRun: () =>
            {
                // Each repository operation opens its own connection. Keep verification on the
                // stack until both probes finish, and report failures outside verifier handling.
                AgentOrchestratorKernel? observed = null;
                var readWatch = Stopwatch.StartNew();
                try
                {
                    observed = stateRepository.LoadAsync().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    readFailure = exception;
                }
                finally
                {
                    readElapsed = readWatch.Elapsed;
                }

                if (observed is null)
                    return;

                var writeWatch = Stopwatch.StartNew();
                try
                {
                    // SaveAsync always acquires BEGIN IMMEDIATE, even for an unchanged snapshot.
                    stateRepository.SaveAsync(observed).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    writeFailure = exception;
                }
                finally
                {
                    writeElapsed = writeWatch.Elapsed;
                }
            });

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var acceptanceWatch = Stopwatch.StartNew();
            AcceptanceStableSlotTestSupport.ExecuteWithIsolatedStableSlot(
                ["acceptance"], stateRepository, workspace, ref agents, providers,
                ref profiles, ref currentGoal, acceptanceVerifier: fakeVerifier,
                acceptanceCleanupContext: CreateIsolatedCleanupContext(workspace.ExecutionDirectory));

            Console.WriteLine($"acceptance-lock-probe read_ms={readElapsed?.TotalMilliseconds} " +
                $"write_ms={writeElapsed?.TotalMilliseconds} acceptance_ms={acceptanceWatch.Elapsed.TotalMilliseconds}");
            Assert.Equal(1, AcceptanceStableSlotTestSupport.LastSelectionCount);
            Assert.Equal(1, fakeVerifier.RunCount);
            Assert.Null(readFailure);
            Assert.Null(writeFailure);
            Assert.NotNull(readElapsed);
            Assert.NotNull(writeElapsed);
            Assert.True(File.Exists(Path.Combine(repo, "concurrency.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_help_does_not_create_goal_worktree")]
    public void CliWorkspaceHelpDoesNotCreateGoalWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Keep workspace clean on help", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            var originalStatus = goal.Status;
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = EchoProfiles();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                CleanupContext = CreateIsolatedCleanupContext(workspace.ExecutionDirectory)
            };

            var output = CaptureConsole(() =>
            {
                var changed = CliCommandHandlers.Execute(
                    ["workspace", "create", goal.Id.Value[..8], "--help"],
                    context);

                Assert.False(changed);
            });

            Assert.Contains("Usage: workspace create", output);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.False(Directory.Exists(Path.Combine(repo, ".orchestrator-worktrees")));
            Assert.Equal(originalStatus, goal.Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_help_does_not_create_backlog_or_goal_state")]
    public void CliBacklogHelpDoesNotCreateBacklogOrGoalState()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = EchoProfiles();
            Goal? currentGoal = null;

            var output = CaptureConsole(() =>
            {
                var changed = CliCommandDispatcher.ExecuteCommand(
                    ["backlog-list", "--help"],
                    kernel,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal,
                    cleanupContext: CreateIsolatedCleanupContext(workspace.ExecutionDirectory));

                Assert.False(changed);
            });

            Assert.Contains("Usage: backlog-list", output);
            Assert.False(File.Exists(workspace.BacklogStorePath));
            Assert.Empty(kernel.Goals);
            Assert.Null(currentGoal);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_list_filters_do_not_change_goal_or_worktree_state")]
    public async Task CliBacklogListFiltersDoNotChangeGoalOrWorktreeState()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new BacklogStore(workspace.BacklogStorePath);
            await store.AddAsync("Foo active one");
            await store.AddAsync("Foo active two");
            await store.AddAsync("Other active");
            var closed = await store.AddAsync("Foo closed");
            await store.CloseAsync(closed.Id, "done");

            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Keep filtered backlog read-only", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            var originalStatus = goal.Status;
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = EchoProfiles();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() =>
            {
                var changed = CliCommandDispatcher.ExecuteCommand(
                    ["backlog-list", "--limit", "1", "--status", "open", "--text", "foo"],
                    kernel,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal,
                    cleanupContext: CreateIsolatedCleanupContext(workspace.ExecutionDirectory));

                Assert.False(changed);
            });

            Assert.Contains("Foo active one", output);
            Assert.DoesNotContain("Foo active two", output);
            Assert.DoesNotContain("Foo closed", output);
            Assert.Equal(originalStatus, goal.Status);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.False(Directory.Exists(Path.Combine(repo, ".orchestrator-worktrees")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_safe_auto_stops_before_acceptance")]
    public void CliLifecycleSimpleGoalSafeAutoStopsBeforeAcceptance()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                CleanupContext = CreateIsolatedCleanupContext(workspace.ExecutionDirectory),
                AcceptanceVerifier = fakeVerifier,
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
            };

            var output = CaptureConsole(() =>
            {
                var ex = Assert.ThrowsAny<InvalidOperationException>(() => CliCommandHandlers.Execute(
                    ["lifecycle-simple-goal", "Ship but pause before merge", "--confirm-batch-start", "--confirm-large-paid-subscription-start", "--autonomy", "safe-auto"],
                    context));
                Assert.True(ex.Message.Contains("stopped before acceptance", StringComparison.Ordinal));
            });

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
            Assert.True(output.Contains("Autonomy policy: safe-auto", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage acceptance: stopped.", StringComparison.Ordinal));
            Assert.True(goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.Contains("blocked lifecycle-simple-goal acceptance", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_requires_confirm_batch_start")]
    public void CliLifecycleSimpleGoalRequiresConfirmBatchStart()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = EchoProfiles();
        var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
        {
            CleanupContext = CreateIsolatedCleanupContext(workspace.ExecutionDirectory)
        };

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandHandlers.Execute(
            ["lifecycle-simple-goal", "Do work"],
            context));

        Xunit.Assert.Contains("--confirm-batch-start", ex.Message);
        Xunit.Assert.Empty(kernel.Goals);
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_requires_large_paid_prompt_confirm")]
    public void CliLifecycleSimpleGoalRequiresLargePaidPromptConfirm()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = EchoProfiles();
        var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
        {
            CleanupContext = CreateIsolatedCleanupContext(workspace.ExecutionDirectory)
        };

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandHandlers.Execute(
            ["lifecycle-simple-goal", "Do work", "--confirm-batch-start"],
            context));

        Xunit.Assert.Contains("--confirm-large-paid-subscription-start", ex.Message);
        Xunit.Assert.Empty(kernel.Goals);
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_fails")]
    public void CliLifecycleSimpleGoalKeepsWorkspaceWhenAcceptanceFails()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            // Touch a real source file so acceptance classifies a behavior
            // change and actually runs verification (the failing fakeVerifier).
            // Relying on the worker committing scratch like .orchestrator-context
            // would no longer make the worktree dirty, so the change must be real.
            var profiles = new WorkerProfileCatalog(
            [
                new WorkerProfile("local", "New-Item -ItemType Directory -Force src | Out-Null; Set-Content -Path src/lifecycle-change.cs -Value '// lifecycle work'; git add -A; git commit -m Lifecycle-work; Write-Output {subscriptionModelName}")
            ]);
            var fakeVerifier = FakeAcceptanceVerifier.Failed("Focused tests failed");
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                CleanupContext = CreateIsolatedCleanupContext(workspace.ExecutionDirectory),
                AcceptanceVerifier = fakeVerifier,
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
            };

            var output = CaptureConsole(() =>
            {
                var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandHandlers.Execute(
                    ["lifecycle-simple-goal", "Run but fail acceptance", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                    context));
                Xunit.Assert.Contains("acceptance", ex.Message);
            });

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.True(output.Contains("Next: acceptance", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_throws")]
    public void CliLifecycleSimpleGoalKeepsWorkspaceWhenAcceptanceThrows()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var fakeVerifier = FakeAcceptanceVerifier.Throws(new InvalidOperationException("fake verifier boom"));
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                CleanupContext = CreateIsolatedCleanupContext(workspace.ExecutionDirectory),
                AcceptanceVerifier = fakeVerifier,
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
            };

            var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandHandlers.Execute(
                ["lifecycle-simple-goal", "Run but verifier throws", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            var goal = context.CurrentGoal!;
            Assert.Equal("fake verifier boom", ex.Message);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
            Assert.Equal(1, fakeVerifier.RunCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_acceptance_failed_retry_clears_completed_task_evidence_before_redispatch")]
    public void GoalWorktreesAcceptanceFailedRetryClearsCompletedTaskEvidenceBeforeRedispatch()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Retry after acceptance failure", [task]);
            kernel.ActivateGoal(goal.Id, EchoAgents());
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            var oldDispatch = new TaskDispatchRecord(
                "codex-cli",
                "codex exec old-prompt.md",
                worktree,
                DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
                BaseCommit: "old-base",
                ResultCommit: "old-result",
                PromptPath: Path.Combine(worktree, ".orchestrator", "prompts", "old-prompt.md"));
            kernel.RecordTaskDispatch(goal.Id, task.Id, oldDispatch);
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(
                    1234,
                    oldDispatch.Command,
                    worktree,
                    "old.out.log",
                    "old.err.log",
                    "old.exit.txt",
                    oldDispatch.DispatchedAt,
                    oldDispatch.DispatchedAt.AddSeconds(5),
                    0));
            kernel.RecordDispatchExecutionResult(
                goal.Id,
                task.Id,
                new TaskVerificationRecord(
                    oldDispatch.Command,
                    worktree,
                    0,
                    "WORKER_RESULT:\nfiles: src/Old.cs\ncommands: old\nEND_WORKER_RESULT",
                    string.Empty,
                    oldDispatch.DispatchedAt.AddSeconds(10)));
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);

            kernel.RecordAcceptanceFailure(goal.Id, ["SqliteOrchestratorStateRepositoryTests.Saves_goal_schema_columns"]);
            kernel.RetryTask(goal.Id, task.Id, "Operator rejection: fix the failing sqlite schema assertion.");
            var newDispatch = new TaskDispatchRecord(
                "codex-cli",
                "codex exec retry-prompt.md",
                worktree,
                DateTimeOffset.Parse("2026-06-26T12:05:00Z"),
                BaseCommit: "retry-base",
                PromptPath: Path.Combine(worktree, ".orchestrator", "prompts", "retry-prompt.md"));
            kernel.RecordTaskDispatch(goal.Id, task.Id, newDispatch);

            Assert.Equal(GoalStatus.Active, goal.Status);
            Assert.Equal(WorkTaskStatus.Running, task.Status);
            Assert.Null(task.LastVerification);
            Assert.Null(task.LastProcess);
            Assert.Equal(newDispatch, task.LastDispatch);
            Assert.Equal("codex exec retry-prompt.md", task.LastDispatch!.Command);
            Assert.Equal("retry-base", task.LastDispatch.BaseCommit);
            Assert.NotEqual(oldDispatch.PromptPath, task.LastDispatch.PromptPath);
            Assert.Equal(Path.Combine(worktree, ".orchestrator", "prompts", "retry-prompt.md"), task.LastDispatch.PromptPath);
            Assert.Null(task.LastDispatch.ResultCommit);
            Assert.Equal(worktree, GoalWorktrees.TryResolve(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
    private sealed class GatedWorkflowGlanceRunner(ProgressiveReviewGlanceDispatchResult result)
        : IProgressiveReviewGlanceRunner
    {
        public int CallCount { get; private set; }

        public Task<ProgressiveReviewGlanceDispatchResult> RunAsync(
            ProgressiveReviewGlanceInputs inputs,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
