using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorDriverTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<AgentDefinition> DefaultAgents() => AgentCatalog.Default().Agents;

    private static (AgentOrchestratorKernel Kernel, Goal Goal) SimpleGoal(string objective = "Test goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), objective);
        return (kernel, goal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) SoftwareGoal(string objective = "Review retry goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(kernel, DefaultAgents(), objective);
        return (kernel, goal);
    }

    private static void DispatchTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string command = "test.exe")
    {
        var dispatch = new TaskDispatchRecord("test-worker", command, "C:\\tmp", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
    }

    private static void PassVerification(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        DispatchTask(kernel, goal, task);
        var verification = new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", "", DateTimeOffset.UtcNow);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
    }

    private static void FailVerification(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        DispatchTask(kernel, goal, task);
        // RecordDispatchExecutionResult sets WorkTaskStatus.Failed; RecordTaskVerification does not
        var verification = new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "fail", "error", DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
    }

    private static void FailReviewerNeedsWork(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string blocker,
        string? evidenceRequest = null,
        string? stdoutPath = "C:\\tmp\\reviewer.out.log")
    {
        DispatchTask(kernel, goal, reviewer, "review");
        var lines = new List<string>
        {
            "Findings first.",
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            $"blockers: {blocker}"
        };
        if (!string.IsNullOrWhiteSpace(evidenceRequest))
        {
            lines.Add($"evidence-request: {evidenceRequest}");
        }

        lines.Add("verdict: needs-work");
        lines.Add("END_WORKER_RESULT");
        var stdout = string.Join(Environment.NewLine, lines);
        var verification = new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: stdoutPath,
            WorkerResultPresent: true);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, verification);
    }

    private static WorkerSandboxPrepRecoverableAction NewSandboxRecoveryAction() =>
        new(
            Worktree: @"C:\repo\.orchestrator-worktrees\abc12345",
            SandboxRoot: @"C:\repo\.orchestrator-worktrees\abc12345\.mcg-sandbox",
            FailedRoot: @"C:\repo\.orchestrator-worktrees\abc12345",
            Reason: "Failed to apply inheritable Low integrity label.",
            RequiresRecursiveRemediation: true);

    private static GoalWorktreeRebaseResult DefaultRebaseSuccess() =>
        new(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "Already fast-forwardable", [], null);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-conductor-driver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var result = GitCli.Run(workingDirectory, args);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}");
        }
    }

    private static GoalLifecycleFacts ReadFactsPerGoal(OrchestratorWorkspace workspace, Goal goal)
    {
        var dir = workspace.ExecutionDirectory;
        var workspaceExists = GoalWorktrees.TryResolve(dir, goal.Id) is not null;
        var journal = GoalOperationJournal.Read(dir, goal.Id);
        var isMerged = GoalOperationJournal.HasCompletedLandingEvidence(journal);
        var isRecorded = GoalOperationJournal.HasCompletedRecordEvidence(journal);
        var isCleanedUp = GoalOperationJournal.HasCompletedCleanupEvidence(journal);
        var hasOpenClarification = GoalRefinementGate.HasOpenClarification(workspace, goal);
        return new GoalLifecycleFacts(workspaceExists, IsBlocked: false, isMerged, isRecorded, isCleanedUp, hasOpenClarification);
    }

    private static ConductorDriver MakeDriver(
        Func<Goal, GoalLifecycleFacts>? getFacts = null,
        Func<int>? getRunningCount = null,
        Func<Goal, string>? createWorkspace = null,
        Func<Goal, DispatchStartOutcome>? dispatchAndStart = null,
        Func<Goal, DispatchStartOutcome>? startRecordedDispatches = null,
        Action? buildServerShutdown = null,
        Func<Goal, bool>? runAcceptance = null,
        Func<Goal, AcceptanceVerificationSummary>? runAcceptanceSummary = null,
        Action<Goal, AcceptanceVerificationSummary>? runAdvisorySemanticAcceptance = null,
        Func<Goal, string, FocusedEvidenceRunResult>? runFocusedEvidence = null,
        Func<GoalId, TaskId, string, TaskSpec>? retryTask = null,
        Func<GoalId, TaskId, string, RetryRoundKind?, TaskSpec>? retryTaskWithRoundKind = null,
        Action<GoalId, TaskId, string>? recordTaskNote = null,
        Action<GoalId, TaskId, string>? recordReviewerEvidenceRequestReceived = null,
        Action<GoalId, TaskId, string>? recordReviewerEvidenceRunRecorded = null,
        Func<GoalId, TaskId, IReadOnlyList<string>, int>? recordCriterionRetryFeedback = null,
        Action<GoalId, TaskId>? clearCriterionRetryFeedback = null,
        Func<Goal, GoalWorktreeRebaseResult>? rebaseOntoMain = null,
        Func<Goal, LandingResult>? land = null,
        Action<Goal, LandingResult>? afterSuccessfulLanding = null,
        Action<Goal>? record = null,
        Func<Goal, GoalWorktreeRemoveResult>? cleanup = null,
        Action<Goal, GoalLifecycleState, string>? writeEscalation = null,
        Func<Goal, ChangeRiskTier?>? classifyRisk = null,
        Action<TimeSpan>? emptyOutputBackoffDelay = null,
        Func<Goal, DispatchReadinessVerdict>? evaluateReadiness = null,
        Action<Goal>? completeGoal = null,
        Func<Goal, string, bool>? normalizeLifecycleState = null,
        Func<WorkerSandboxPrepRecoverableAction, bool>? recoverSandboxPrep = null,
        Func<bool>? hasGateReadyGoal = null)
    {
        return new ConductorDriver(
            getFacts ?? (_ => GoalLifecycleFacts.None),
            getRunningCount ?? (() => 0),
            createWorkspace ?? (_ => "/tmp/workspace"),
            dispatchAndStart ?? (_ => DispatchStartOutcome.Started()),
            startRecordedDispatches,
            buildServerShutdown,
            runAcceptanceSummary ?? (goal => (runAcceptance ?? (_ => true))(goal)
                ? AcceptanceVerificationSummary.PassedWithNoUnmetCriteria
                : AcceptanceVerificationSummary.Failed),
            runAdvisorySemanticAcceptance,
            retryTask,
            recordTaskNote,
            recordCriterionRetryFeedback,
            clearCriterionRetryFeedback,
            rebaseOntoMain ?? (_ => DefaultRebaseSuccess()),
            land is null
                ? ((g, _) => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"))
                : ((g, _) => land(g)),
            afterSuccessfulLanding,
            record ?? (_ => { }),
            cleanup ?? (_ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null)),
            writeEscalation ?? ((_, _, _) => { }),
            classifyRisk ?? (_ => null),
            emptyOutputBackoffDelay,
            evaluateReadiness,
            completeGoal: completeGoal,
            normalizeLifecycleState: normalizeLifecycleState,
            recoverSandboxPrep: recoverSandboxPrep,
            hasGateReadyGoal: hasGateReadyGoal,
            runFocusedEvidence: runFocusedEvidence,
            recordReviewerEvidenceRequestReceived: recordReviewerEvidenceRequestReceived,
            recordReviewerEvidenceRunRecorded: recordReviewerEvidenceRunRecorded,
            retryTaskWithRoundKind: retryTaskWithRoundKind);
    }

    private sealed class FakeAcceptanceVerifier : IGoalAcceptanceVerifier
    {
        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AcceptanceVerificationResult(true, false, 0, "ok"));

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "focused evidence passed",
                Checks: [
                    new AcceptanceCheckResult(
                        "reviewer focused evidence",
                        true,
                        0,
                        null,
                        ArtifactsPath: "C:\\tmp\\focused-evidence.trx")
                ]));
    }

    private sealed class ThrowingAcceptanceVerifier(Exception exception) : IGoalAcceptanceVerifier
    {
        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AcceptanceVerificationResult>(exception);

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<FocusedEvidenceRunResult>(exception);
    }

    private sealed class CountingModelProvider(string providerName, string text) : IModelProvider
    {
        public string ProviderName { get; } = providerName;
        public int Calls { get; private set; }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ModelResponse(text, null, "stop"));
        }
    }

    // ── Empty-batch escalation diagnostics ───────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_real_facts_match_per_goal_read_path")]
    public void ConductorDriverRealFactsMatchPerGoalReadPath()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var merged = kernel.CreateGoal("Merged goal");
        var cleaned = kernel.CreateGoal("Cleaned goal");
        var missing = kernel.CreateGoal("Missing journal goal");
        var clarified = kernel.CreateGoal("Clarified goal");

        var worktreePath = GoalWorktrees.WorktreePath(root, merged.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: test");
        GoalOperationJournal.Completed(root, merged, "conductor:land", "landed");
        GoalOperationJournal.Completed(root, merged, "conductor:record", "recorded");
        GoalOperationJournal.Completed(root, cleaned, "conductor:cleanup", "cleaned");
        CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .RaiseAsync(
                CollaborationItemType.Clarification,
                clarified.Id.Value,
                "clarify",
                "body",
                $"spec-clarification:{clarified.Id.Value}:test")
            .GetAwaiter()
            .GetResult();

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        foreach (var goal in new[] { merged, cleaned, missing, clarified })
        {
            Assert.Equal(ReadFactsPerGoal(workspace, goal), driver.GetFacts(goal));
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_real_facts_refresh_after_conductor_record")]
    public async Task ConductorDriverRealFactsRefreshAfterConductorRecord()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Record refresh goal");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: test");
        GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Merged, executed.FromState);
        Assert.True(driver.GetFacts(goal).IsRecorded);
        Assert.Equal(ReadFactsPerGoal(workspace, goal), driver.GetFacts(goal));
        var record = await new DogfoodLogStore(workspace.DogfoodLogStorePath)
            .GetByGoalIdAsync(goal.Id.Value);
        Assert.NotNull(record);
        Assert.Contains("Record refresh goal", record!.RenderedMarkdown);
        Assert.False(File.Exists(Path.Combine(root, "DOGFOOD_LOG.md")));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_acceptance_slots_busy_journals_blocked_outcome")]
    public void ConductorDriverAcceptanceSlotsBusyJournalsBlockedOutcome()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Slots busy conductor goal");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "goal work");
        RunGit(worktree, "add", "feature.txt");
        RunGit(worktree, "commit", "-m", "goal work");
        var busy = new DotnetBuildLeaseAcquisition.SlotsBusy(
            "goal-slots-busy",
            [new DotnetBuildStableSlotWait(0, 12345)]);

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new ThrowingAcceptanceVerifier(new DotnetBuildSlotsBusyException(busy)),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("slots busy", held.Reason, StringComparison.OrdinalIgnoreCase);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        var blockedOutcome = journal.Entries.LastOrDefault(entry => entry.AcceptanceOutcome == "blocked:build-slot");
        Assert.NotNull(blockedOutcome);
        Assert.Equal(GoalOperationStatus.Failed, blockedOutcome.Status);
        Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.BranchHeadSha));
        Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.MainHeadSha));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_surfaces_operator_approval_reasons")]
    public void ConductorDriverEmptyBatchSurfacesOperatorApprovalReasons()
    {
        var plan = new ParallelExecutionPlan(
            [],
            [
                new ParallelExecutionDecision(
                    "task-1",
                    ParallelExecutionDisposition.RequiresOperatorApproval,
                    null,
                    ["high-risk ownership area requires operator approval: Script scripts/Invoke-TestSummary.ps1"])
            ]);

        var reason = ConductorDriver.DescribeEmptyBatch(plan);

        Assert.True(reason.Contains("require operator approval", StringComparison.OrdinalIgnoreCase));
        Assert.True(reason.Contains("scripts/Invoke-TestSummary.ps1", StringComparison.Ordinal));
        Assert.False(reason.Contains("no assigned or ready tasks", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_surfaces_ready_blocked_diagnostics_before_generic_reason")]
    public void ConductorDriverEmptyBatchSurfacesReadyBlockedDiagnosticsBeforeGenericReason()
    {
        var taskId = TaskId.New().Value;
        var plan = new ParallelExecutionPlan([], []);
        var diagnostic = new ReadyBlockedDiagnostic(
            "abc12345",
            1,
            taskId,
            "codex-cli",
            "dirty-worktree",
            ["blocked: worktree has 1 uncommitted change(s) before dispatch"]);

        var reason = ConductorDriver.DescribeEmptyBatch(plan, [diagnostic]);

        Assert.Contains("assigned tasks were excluded", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(taskId, reason, StringComparison.Ordinal);
        Assert.Contains("codex-cli", reason, StringComparison.Ordinal);
        Assert.Contains("dirty-worktree", reason, StringComparison.Ordinal);
        Assert.Contains("worktree has 1 uncommitted change", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("no assigned or ready tasks", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_without_approval_blocks_uses_generic_reason")]
    public void ConductorDriverEmptyBatchWithoutApprovalBlocksUsesGenericReason()
    {
        var plan = new ParallelExecutionPlan([], []);

        var reason = ConductorDriver.DescribeEmptyBatch(plan);

        Assert.True(reason.Contains("no assigned or ready tasks", StringComparison.Ordinal));
    }

    // ── Created state ─────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Created_creates_workspace_and_returns_Executed")]
    public void ConductorDriverCreatedCreatesWorkspaceAndReturnsExecuted()
    {
        var (_, goal) = SimpleGoal();
        var workspaceCreated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            createWorkspace: _ => { workspaceCreated = true; return "/tmp/workspace"; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(workspaceCreated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Created, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    // ── WorkspaceReady state ──────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_at_cap_returns_Held")]
    public void ConductorDriverWorkspaceReadyAtCapReturnsHeld()
    {
        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative; // MaxConcurrentPaidWorkers = 4
        var dispatchCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => policy.MaxConcurrentPaidWorkers, // at cap
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.False(dispatchCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_under_cap_dispatches_and_returns_Executed")]
    public void ConductorDriverWorkspaceReadyUnderCapDispatchesAndReturnsExecuted()
    {
        var (_, goal) = SimpleGoal();
        var dispatchCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(dispatchCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_reserves_gate_slot_when_gate_ready")]
    public void ConductorDriverWorkspaceReadyReservesGateSlotWhenGateReady()
    {
        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative;
        var dispatchCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => DotnetBuildEnvironmentManager.StableSlotCount - 1,
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); },
            hasGateReadyGoal: () => true);

        ConductorAdvanceResult? result = null;
        var output = AsyncLocalConsoleRouter.Capture(() => result = driver.AdvanceOnce(goal, policy));

        Assert.False(dispatchCalled);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result!.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        Assert.Contains("gate-ready goal reserving a stable slot", held.Reason, StringComparison.Ordinal);
        Assert.Contains("ADMISSION", output, StringComparison.Ordinal);
        Assert.Contains("reason=reserved-gate-slot", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_repairs_terminal_goal_with_assigned_task_before_acceptance")]
    public void ConductorDriverRepairsTerminalGoalWithAssignedTaskBeforeAcceptance()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Retry desync");
        var snapshot = kernel.ExportSnapshot();
        var badGoalSnapshot = snapshot.Goals.Single() with
        {
            Status = GoalStatus.Completed,
            Tasks = snapshot.Goals.Single().Tasks
                .Select(task => task with { Status = WorkTaskStatus.Assigned, LastVerification = null })
                .ToArray()
        };
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [badGoalSnapshot] });
        goal = kernel.Goals.Single();
        var dispatched = false;
        var acceptanceCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                dispatched = true;
                return DispatchStartOutcome.Started();
            },
            runAcceptance: _ =>
            {
                acceptanceCalled = true;
                return true;
            },
            normalizeLifecycleState: (g, reason) => kernel.NormalizeGoalLifecycleState(g.Id, reason));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(dispatched);
        Assert.False(acceptanceCalled);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Conductor auto-repaired", StringComparison.Ordinal));
    }

    // ── Dispatched state ──────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Dispatched_starts_recorded_dispatch")]
    public void ConductorDriverDispatchedStartsRecordedDispatch()
    {
        var (kernel, goal) = SimpleGoal();
        DispatchTask(kernel, goal, goal.Tasks.Single());
        var startCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            startRecordedDispatches: _ =>
            {
                startCalled = true;
                return DispatchStartOutcome.Started();
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(startCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Dispatched, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Dispatched_start_failure_escalates_with_reason")]
    public void ConductorDriverDispatchedStartFailureEscalatesWithReason()
    {
        var (kernel, goal) = SimpleGoal();
        DispatchTask(kernel, goal, goal.Tasks.Single());
        var callCount = 0;
        var shutdownCalled = false;
        string? escalationReason = null;
        const string startFailReason = "Recorded dispatch start failed: worker command refused to launch";

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            startRecordedDispatches: _ =>
            {
                callCount++;
                return DispatchStartOutcome.SpawnFailed(startFailReason);
            },
            buildServerShutdown: () => { shutdownCalled = true; },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(2, callCount);
        Assert.True(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Dispatched, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
        Assert.Equal(startFailReason, escalationReason);
    }

    // ── Running state ─────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Running_returns_Held")]
    public void ConductorDriverRunningReturnsHeld()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        var process = new TaskProcessRecord(12345, "test.exe", "C:\\tmp",
            "C:\\tmp\\stdout", "C:\\tmp\\stderr", "C:\\tmp\\exit",
            StartedAt: DateTimeOffset.UtcNow, CompletedAt: null, ExitCode: null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held);
        Assert.Equal(GoalLifecycleState.Running, ((ConductorAdvanceOutcome.Held)result.Outcome).State);
    }

    // ── AwaitingVerification state ────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_AwaitingVerification_returns_Held")]
    public void ConductorDriverAwaitingVerificationReturnsHeld()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        // Mark task Completed without verification → MissingVerification gate keeps goal Active,
        // all tasks Completed → GoalLifecycleState.AwaitingVerification.
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");

        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held);
        Assert.Equal(GoalLifecycleState.AwaitingVerification, ((ConductorAdvanceOutcome.Held)result.Outcome).State);
    }

    // ── Verified state ────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_acceptance_fails_escalates")]
    public void ConductorDriverVerifiedAcceptanceFailsEscalates()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => false,
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Verified, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_rebase_conflict_escalates_before_acceptance")]
    public void ConductorDriverVerifiedRebaseConflictEscalatesBeforeAcceptance()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var acceptanceCalled = false;
        string? escalationReason = null;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Conflict, "goal/test", "conflict", ["src/Foo.cs"], "workspace rebase"),
            runAcceptance: _ => { acceptanceCalled = true; return true; },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        // Rebase-before-acceptance: an un-integrable branch escalates without spending an acceptance run,
        // and acceptance never verifies the pre-integration branch.
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.False(acceptanceCalled);
        Assert.Contains("pre-landing rebase conflict", escalationReason!, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs", escalationReason!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_advisory_only_unmet_acceptance_criteria_land_with_task_note")]
    public void ConductorDriverVerifiedAdvisoryOnlyUnmetAcceptanceCriteriaLandWithTaskNote()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["stale required retry feedback"]);
        var landCalled = false;
        var retryCalled = false;
        var advisory = new AcceptanceCheckResult(
            "test tamper guard",
            false,
            0,
            "1 test degradation signal(s)",
            ResultSummary: "1 test degradation signal(s)",
            Advisory: true);

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [advisory]),
            retryTask: (_, _, _) =>
            {
                retryCalled = true;
                throw new InvalidOperationException("Advisory acceptance criteria must not retry tasks.");
            },
            recordTaskNote: (goalId, taskId, message) => kernel.RecordTaskNote(goalId, taskId, message),
            clearCriterionRetryFeedback: kernel.ClearCriterionRetryFeedback,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.False(retryCalled);
        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Empty(task.CriterionRetryFeedback);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Advisory acceptance criteria observed during landing (non-gating)", StringComparison.Ordinal) &&
            evt.Message.Contains("test tamper guard: 1 test degradation signal(s)", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_required_unmet_acceptance_criterion_retries_task_with_feedback")]
    public void ConductorDriverVerifiedRequiredUnmetAcceptanceCriterionRetriesTaskWithFeedback()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var landCalled = false;
        var retryCalled = false;
        string? retryMessage = null;
        var unmet = new AcceptanceCheckResult(
            "command-exit dotnet test --filter RetryEvidence",
            false,
            1,
            string.Join(Environment.NewLine,
            [
                "src/Foo.cs(12,34): error CS1002: ; expected",
                "[xUnit.net 00:00:01.23]     Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]",
            ]),
            ResultSummary: "focused conductor tests failed");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

        Assert.True(retryCalled);
        Assert.False(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Contains("failed check: command-exit dotnet test --filter RetryEvidence (exit code 1)", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs(12,34): error CS1002: ; expected", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]", retryMessage!, StringComparison.Ordinal);
        Assert.True(task.CriterionRetryFeedback.Any(item => item.Contains("src/Foo.cs(12,34): error CS1002: ; expected", StringComparison.Ordinal)));
        Assert.True(task.CriterionRetryFeedback.Any(item => item.Contains("Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]", StringComparison.Ordinal)));
        Assert.Contains("## Unmet acceptance criteria from the prior attempt - fix these:", brief.Content, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs(12,34): error CS1002: ; expected", brief.Content, StringComparison.Ordinal);
        Assert.Contains("Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]", brief.Content, StringComparison.Ordinal);
        Assert.Contains("focused conductor tests failed", brief.Content, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_unmet_acceptance_retry_feedback_caps_concrete_evidence")]
    public void ConductorDriverVerifiedUnmetAcceptanceRetryFeedbackCapsConcreteEvidence()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        string? retryMessage = null;
        var output = string.Join(Environment.NewLine,
            Enumerable.Range(1, 35).Select(index => $"src/Foo{index}.cs({index},1): error CS1002: ; expected"));
        var unmet = new AcceptanceCheckResult(
            "command-exit dotnet test --filter ManyFailures",
            false,
            1,
            output,
            ResultSummary: "many compiler errors");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("src/Foo29.cs(29,1): error CS1002: ; expected", retryMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Foo30.cs(30,1): error CS1002: ; expected", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("... truncated 6 acceptance evidence line(s)", retryMessage!, StringComparison.Ordinal);
        Assert.True(task.CriterionRetryFeedback.Any(item => item.Contains("... truncated 6 acceptance evidence line(s)", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_unmet_acceptance_retry_feedback_preserves_no_evidence_fallback")]
    public void ConductorDriverVerifiedUnmetAcceptanceRetryFeedbackPreservesNoEvidenceFallback()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        string? retryMessage = null;
        var unmet = new AcceptanceCheckResult(
            "grep-present docs/usage.md contains Ready",
            false,
            1,
            "Pattern 'Ready' was not found.",
            ResultSummary: "docs/usage.md is missing Ready");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("docs/usage.md is missing Ready", retryMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("Concrete acceptance failure evidence", retryMessage!, StringComparison.Ordinal);
        Assert.Equal(["grep-present docs/usage.md contains Ready: docs/usage.md is missing Ready"], task.CriterionRetryFeedback);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_unmet_acceptance_criterion_escalates_after_retry_budget")]
    public void ConductorDriverVerifiedUnmetAcceptanceCriterionEscalatesAfterRetryBudget()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["previous unmet criterion"]);
        var landCalled = false;
        string? escalationReason = null;
        var unmet = new AcceptanceCheckResult(
            "file-exists docs/usage.md",
            false,
            1,
            "file missing",
            ResultSummary: "docs/usage.md missing");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (_, _, _) => throw new InvalidOperationException("Retry should not be called after budget is spent."),
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.False(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Contains("Acceptance criteria unmet after 1 retries", escalationReason!, StringComparison.Ordinal);
        Assert.Contains("docs/usage.md missing", escalationReason!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_all_acceptance_criteria_met_lands")]
    public void ConductorDriverVerifiedAllAcceptanceCriteriaMetLands()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["stale retry feedback"]);
        var landCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            clearCriterionRetryFeedback: kernel.ClearCriterionRetryFeedback,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(0, task.CriterionRetryFeedback.Count);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_runs_semantic_acceptance_after_acceptance_and_landing")]
    public void ConductorDriverVerifiedRunsSemanticAcceptanceAfterAcceptanceAndLanding()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var order = new List<string>();

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ =>
            {
                order.Add("acceptance");
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            runAdvisorySemanticAcceptance: (_, _) => order.Add("semantic"),
            land: g =>
            {
                order.Add("land");
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Xunit.Assert.Equal(new[] { "acceptance", "land", "semantic" }, order);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_successful_landing_runs_semantic_then_post_landing_close_after_main_advances")]
    public void ConductorDriverVerifiedSuccessfulLandingRunsSemanticThenPostLandingCloseAfterMainAdvances()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var order = new List<string>();

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: (_, _) => order.Add("semantic"),
            land: g =>
            {
                order.Add("land");
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            afterSuccessfulLanding: (_, result) =>
            {
                Assert.True(result.MainAdvanced);
                order.Add("close");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Xunit.Assert.Equal(new[] { "land", "semantic", "close" }, order);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_policy_escalation_skips_semantic_receipt")]
    public void ConductorDriverVerifiedPolicyEscalationSkipsSemanticReceipt()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var semanticCalled = false;
        var landCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: (_, _) => { semanticCalled = true; },
            classifyRisk: _ => ChangeRiskTier.Broad,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.False(semanticCalled);
        Assert.False(landCalled);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_landing_writes_semantic_receipt_and_closes_backlog_item")]
    public async Task ConductorDriverVerifiedLandingWritesSemanticReceiptAndClosesBacklogItem()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "src", "A.cs"), "class A {}\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");
        RunGit(root, "checkout", "-b", "goal/test");
        File.WriteAllText(Path.Combine(root, "src", "A.cs"), "class A { string Done() => \"done\"; }\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "goal change");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog(
        [
            new ModelFunctionBinding(ModelFunctionPurposes.AcceptanceJudge, ModelLane.CheapApi,
                new ModelProfile("Fake", "judge", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var provider = new CountingModelProvider("Fake",
            """{"criteria_met": true, "confidence": "high", "reasons": ["implemented"], "unmet_criteria": []}""");
        var providers = new InMemoryModelProviderRegistry([provider]);
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlogStore.AddAsync("Landing target");
        var (kernel, goal) = SimpleGoal("Implement required behavior");
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
        PassVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: (g, summary) => GoalLandingPostActions.RunAdvisorySemanticAcceptance(
                g,
                workspace,
                providers,
                WorkerProfileCatalog.Default(),
                root,
                null),
            land: g => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"),
            afterSuccessfulLanding: (g, result) =>
            {
                Assert.True(result.MainAdvanced);
                GoalLandingPostActions.AutoCloseSourceBacklogItem(g, workspace.BacklogStorePath);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.True(provider.Calls > 0);
        var receipt = File.ReadLines(workspace.SemanticAcceptanceLogPath).Single();
        Assert.True(receipt.Contains(goal.Id.Value, StringComparison.Ordinal));
        var fetched = await backlogStore.GetByExactIdAsync(item.Id);
        Assert.NotNull(fetched);
        Assert.Equal(BacklogItemStatus.Done, fetched!.Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_does_not_close_backlog_when_landing_does_not_advance_main")]
    public void ConductorDriverVerifiedDoesNotCloseBacklogWhenLandingDoesNotAdvanceMain()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var closeCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            land: g => new LandingResult(g.Id.Value, g.Id.Value[..8],
                new LandingDecision.Promote(), "integration", false, "No main advance"),
            afterSuccessfulLanding: (_, _) => { closeCalled = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.False(closeCalled);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_zero_criterion_retry_budget_escalates_without_retry")]
    public void ConductorDriverVerifiedZeroCriterionRetryBudgetEscalatesWithoutRetry()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var retryCalled = false;
        string? escalationReason = null;
        var policy = ConductorAutonomyPolicy.Conservative with { MaxCriterionRetries = 0 };
        var unmet = new AcceptanceCheckResult(
            "command-exit dotnet test",
            false,
            1,
            "failed",
            ResultSummary: "focused command failed");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message);
            },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.False(retryCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Contains("Acceptance criteria unmet after 0 retries", escalationReason!, StringComparison.Ordinal);
        Assert.Contains("focused command failed", escalationReason!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_policy_risk_gate_escalates_Security_risk")]
    public void ConductorDriverVerifiedPolicyRiskGateEscalatesSecurityRisk()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;

        // Conservative: AutoPromoteRiskThreshold = DocsOnly → Security risk → Escalate
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Security,
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_landing_engine_escalation_escalates")]
    public void ConductorDriverVerifiedLandingEngineEscalationEscalates()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g => new LandingResult(g.Id.Value, g.Id.Value[..8],
                new LandingDecision.Escalate("Conflict detected"), "integration", false, "Conflict"),
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_clean_DocsOnly_risk_lands")]
    public void ConductorDriverVerifiedCleanDocsOnlyRiskLands()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;

        // Conservative: threshold DocsOnly, DocsOnly risk → Auto
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Verified, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    // ── Merged state ──────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Merged_records_and_returns_Executed")]
    public void ConductorDriverMergedRecordsAndReturnsExecuted()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var recorded = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true),
            record: _ => { recorded = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(recorded);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Merged, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    // ── Recorded state ────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Recorded_cleans_up_and_returns_Executed")]
    public void ConductorDriverRecordedCleansUpAndReturnsExecuted()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var cleanedUp = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true),
            cleanup: _ =>
            {
                cleanedUp = true;
                return new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(cleanedUp);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Recorded, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Recorded_incomplete_cleanup_completes_with_deferred_diagnostics")]
    public void ConductorDriverRecordedIncompleteCleanupCompletesWithDeferredDiagnostics()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var cleanupCalls = 0;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true),
            cleanup: _ =>
            {
                cleanupCalls++;
                return new GoalWorktreeRemoveResult(
                    "Removed workspace, but leftover directory cleanup is incomplete.",
                    @"C:\repo\.orchestrator-worktrees\abc12345",
                    [new WorktreeLockHolder(1234, "dotnet", "dotnet test")],
                    "conduct abc12345 --loop",
                    CleanupBackoff: new GoalWorktreeCleanupBackoff(
                        "remove:cleanup-budget-exhausted",
                        DateTimeOffset.Parse("2026-07-02T05:01:00Z"),
                        TimeSpan.FromMinutes(1)));
            },
            completeGoal: g => kernel.CompleteGoal(g.Id, "test completed after deferred cleanup."));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Recorded, executed.FromState);
        Assert.Equal(1, cleanupCalls);
        Assert.True(executed.Description.Contains("leftover", StringComparison.Ordinal), executed.Description);
        Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Recorded_cleanup_failure_completes_without_throwing")]
    public void ConductorDriverRecordedCleanupFailureCompletesWithoutThrowing()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true),
            cleanup: _ => throw new InvalidOperationException("git worktree remove refused dirty workspace"),
            completeGoal: g => kernel.CompleteGoal(g.Id, "test completed after deferred cleanup."));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Recorded, executed.FromState);
        Assert.Contains("Workspace cleanup deferred after removal failure", executed.Description, StringComparison.Ordinal);
        Assert.Contains("git worktree remove refused dirty workspace", executed.Description, StringComparison.Ordinal);
        Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
    }

    // ── CleanedUp state ───────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_CleanedUp_returns_Done")]
    public void ConductorDriverCleanedUpReturnsDone()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Done);
        Assert.Equal(GoalLifecycleState.CleanedUp, ((ConductorAdvanceOutcome.Done)result.Outcome).State);
    }

    // ── Error states ──────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Failed_task_escalates_regardless_of_policy")]
    public void ConductorDriverFailedTaskEscalatesRegardlessOfPolicy()
    {
        var (kernel, goal) = SimpleGoal();
        FailVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_needs_work_auto_retries_developer_with_findings")]
    public void ConductorDriverReviewerNeedsWorkAutoRetriesDeveloperWithFindings()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var blocker = "Developer omitted retry receipt injection in TaskBriefs.";
        FailReviewerNeedsWork(kernel, goal, reviewer, blocker);
        var dispatched = false;
        var escalated = false;
        string? retryMessage = null;
        RetryRoundKind? retryRoundKind = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retryMessage = msg;
                retryRoundKind = roundKind;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(dispatched);
        Assert.False(escalated);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Contains("auto-review-retry round 1", retryMessage);
        Assert.Contains(blocker, retryMessage);
        Assert.Contains("C:\\tmp\\reviewer.out.log", retryMessage);
        Assert.Equal(RetryRoundKind.Mechanical, retryRoundKind);
        Assert.Equal(RetryRoundKind.Mechanical, developer.PendingRetryRoundKind);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_evidence_request_runs_focused_evidence_and_retries_reviewer_only")]
    public void ConductorDriverReviewerEvidenceRequestRunsFocusedEvidenceAndRetriesReviewerOnly()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var request = "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests";
        FailReviewerNeedsWork(kernel, goal, reviewer, "missing focused conductor evidence", request);
        var focusedRuns = 0;
        var retriedTaskIds = new List<TaskId>();
        string? retryMessage = null;
        RetryRoundKind? retryRoundKind = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            runFocusedEvidence: (_, actualRequest) =>
            {
                focusedRuns++;
                Assert.Equal(request, actualRequest);
                return new FocusedEvidenceRunResult(
                    actualRequest,
                    Accepted: true,
                    Passed: true,
                    Summary: "focused evidence passed: 1 check; receipts: C:\\tmp\\trx",
                    Checks:
                    [
                        new AcceptanceCheckResult(
                            "reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~ConductorDriverTests",
                            true,
                            0,
                            null,
                            ArtifactsPath: "C:\\tmp\\trx")
                    ]);
            },
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retriedTaskIds.Add(tid);
                retryMessage = msg;
                retryRoundKind = roundKind;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            recordReviewerEvidenceRequestReceived: (gid, tid, msg) => kernel.RecordReviewerEvidenceRequestReceived(gid, tid, msg),
            recordReviewerEvidenceRunRecorded: (gid, tid, msg) => kernel.RecordReviewerEvidenceRunRecorded(gid, tid, msg));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal([reviewer.Id], retriedTaskIds);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Contains("reviewer evidence-on-demand", retryMessage);
        Assert.Contains("C:\\tmp\\trx", retryMessage);
        Assert.Equal(RetryRoundKind.Mechanical, retryRoundKind);
        Assert.Equal(RetryRoundKind.Mechanical, reviewer.PendingRetryRoundKind);
        Assert.Contains(goal.Timeline, evt => evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.ReviewerEvidenceRequestReceived);
        Assert.Contains(goal.Timeline, evt => evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.ReviewerEvidenceRunRecorded);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_second_reviewer_evidence_request_same_round_escalates")]
    public void ConductorDriverSecondReviewerEvidenceRequestSameRoundEscalates()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior reviewer evidence request");
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "still missing focused conductor evidence",
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests");
        var focusedRuns = 0;
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "should not run", []);
            },
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            recordReviewerEvidenceRequestReceived: (gid, tid, msg) => kernel.RecordReviewerEvidenceRequestReceived(gid, tid, msg),
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.False(retried);
        Assert.Contains("repeated in the same review round", escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_unbounded_reviewer_evidence_request_escalates")]
    public void ConductorDriverUnboundedReviewerEvidenceRequestEscalates()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "missing full test evidence", "Infrastructure.Tests: all");
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: false,
                Passed: false,
                Summary: "unbounded evidence request rejected",
                Checks: []),
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            recordReviewerEvidenceRequestReceived: (gid, tid, msg) => kernel.RecordReviewerEvidenceRequestReceived(gid, tid, msg),
            recordReviewerEvidenceRunRecorded: (gid, tid, msg) => kernel.RecordReviewerEvidenceRunRecorded(gid, tid, msg),
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.Contains("evidence request rejected", escalation);
        Assert.Contains("unbounded evidence request rejected", escalation);
        Assert.Contains(goal.Timeline, evt => evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.ReviewerEvidenceRunRecorded);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_failing_reviewer_evidence_run_escalates_with_receipts")]
    public void ConductorDriverFailingReviewerEvidenceRunEscalatesWithReceipts()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "missing focused conductor evidence",
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests");
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "focused evidence failed: reviewer focused evidence exit 1; receipts: C:\\tmp\\failed-trx",
                Checks:
                [
                    new AcceptanceCheckResult(
                        "reviewer focused evidence",
                        false,
                        1,
                        "Failed! - Failed: 1",
                        ArtifactsPath: "C:\\tmp\\failed-trx")
                ]),
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            recordReviewerEvidenceRequestReceived: (gid, tid, msg) => kernel.RecordReviewerEvidenceRequestReceived(gid, tid, msg),
            recordReviewerEvidenceRunRecorded: (gid, tid, msg) => kernel.RecordReviewerEvidenceRunRecorded(gid, tid, msg),
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.Contains("focused evidence failed", escalation);
        Assert.Contains("C:\\tmp\\failed-trx", escalation);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.ReviewerEvidenceRunRecorded &&
            evt.Message.Contains("C:\\tmp\\failed-trx", StringComparison.Ordinal));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_needs_work_round_7_stops_and_escalates")]
    public void ConductorDriverReviewerNeedsWorkRound7StopsAndEscalates()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        for (var i = 1; i <= 6; i++)
        {
            kernel.RetryTask(goal.Id, developer.Id, $"auto-review-retry round {i}: prior reviewer finding");
            PassVerification(kernel, goal, developer);
            PassVerification(kernel, goal, tester);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "Developer still misses the review blocker.");
        var retried = false;
        var dispatched = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.False(dispatched);
        Assert.Contains("auto-review-retry stopped at review round 7/7", escalation);
        Assert.Contains("operator decision required", escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_operator_evidence_blocker_escalates_without_retry")]
    public void ConductorDriverReviewerOperatorEvidenceBlockerEscalatesWithoutRetry()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "Operator receipt required for measurement mandate before acceptance.");
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.Contains("operator-owned evidence", escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_provider_failure_does_not_auto_review_retry")]
    public void ConductorDriverReviewerProviderFailureDoesNotAutoReviewRetry()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        DispatchTask(kernel, goal, reviewer, "review");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            "ERROR: provider authentication failed before WORKER_RESULT",
            "",
            DateTimeOffset.UtcNow,
            ProviderFailureKind: ProviderFailureKind.RateLimit));
        var retried = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Failed_nonzero_empty_output_flake_auto_recovers_instead_of_escalating")]
    public void ConductorDriverFailedNonzeroEmptyOutputFlakeAutoRecoversInsteadOfEscalating()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        // Worker exited non-zero but produced zero bytes of stdout: this is a CLI startup/API flake,
        // not a worker verdict, so the conductor should re-admit it instead of escalating.
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "", "", DateTimeOffset.UtcNow));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.EmptyOutputRetryCount);

        var retried = false;
        var escalated = false;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; retryMessage = msg; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.False(escalated);
        Assert.Contains("zero-byte stdout with exit 1", retryMessage!);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_sandbox_preflight_failure_blocks_without_empty_output_retry_or_dispatch")]
    public void ConductorDriverSandboxPreflightFailureBlocksWithoutEmptyOutputRetryOrDispatch()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "CreateProcessAsUser failed during Low Integrity preflight",
                DateTimeOffset.UtcNow));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(0, task.EmptyOutputRetryCount);

        var retried = false;
        var dispatched = false;
        string? escalationMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalationMessage = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.False(dispatched);
        Assert.Equal(0, task.EmptyOutputRetryCount);
        Assert.Contains("sandbox-preflight-failure", escalationMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_stale_dispatch_recovery_retries_without_empty_output_budget")]
    public void ConductorDriverStaleDispatchRecoveryRetriesWithoutEmptyOutputBudget()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "Dispatch recovery policy action='mark-stale' evidence='heartbeat-absent' reason='no live process, exit-absent, stale retry budget remaining=1'.",
                DateTimeOffset.UtcNow));
        Assert.Equal(0, task.EmptyOutputRetryCount);

        var retried = false;
        var dispatched = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.True(dispatched);
        Assert.Equal(0, task.EmptyOutputRetryCount);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_stale_dispatch_budget_exhausted_escalates_without_retry")]
    public void ConductorDriverStaleDispatchBudgetExhaustedEscalatesWithoutRetry()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "Dispatch recovery policy action='budget-exhausted' evidence='heartbeat-absent' reason='no live process, exit-absent, heartbeat absent' blocker='stale-dispatch retry budget exhausted'.",
                DateTimeOffset.UtcNow));
        Assert.Equal(0, task.EmptyOutputRetryCount);

        var retried = false;
        var dispatched = false;
        string? escalationMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalationMessage = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.False(dispatched);
        Assert.Equal(0, task.EmptyOutputRetryCount);
        Assert.Contains("stale-dispatch retry budget exhausted", escalationMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_nonempty_stdout_failure_resets_empty_output_count_and_escalates")]
    public void ConductorDriverNonemptyStdoutFailureResetsEmptyOutputCountAndEscalates()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "", "", DateTimeOffset.UtcNow));
        kernel.RetryTask(goal.Id, task.Id, "retry transient empty output");
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "x", "", DateTimeOffset.UtcNow));

        Assert.Equal(0, task.EmptyOutputRetryCount);
        var retried = false;
        var escalated = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_output_budget_exhaustion_auto_recovers_before_operator_escalation")]
    public void ConductorDriverEmptyOutputBudgetExhaustionAutoRecoversBeforeOperatorEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        for (var i = 0; i < 2; i++)
        {
            DispatchTask(kernel, goal, task);
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
                new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "", "", DateTimeOffset.UtcNow));
            if (i == 0)
            {
                kernel.RetryTask(goal.Id, task.Id, "previous empty output retry");
            }
        }

        var policy = ConductorAutonomyPolicy.Permissive with
        {
            MaxEmptyOutputDispatchRetries = 2,
            MaxEmptyOutputAutoRecoverCycles = 1
        };
        var retryMessage = string.Empty;
        var escalated = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retryMessage = msg; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.False(escalated);
        Assert.Contains("Auto-recover+re-admit", retryMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);

        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "", "", DateTimeOffset.UtcNow));

        result = driver.AdvanceOnce(goal, policy);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_output_backoff_uses_configured_delay")]
    public void ConductorDriverEmptyOutputBackoffUsesConfiguredDelay()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        for (var i = 0; i < 2; i++)
        {
            DispatchTask(kernel, goal, task);
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
                new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "", "", DateTimeOffset.UtcNow));
            if (i == 0)
            {
                kernel.RetryTask(goal.Id, task.Id, "previous empty output retry");
            }
        }

        var delays = new List<TimeSpan>();
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => kernel.RetryTask(gid, tid, msg),
            emptyOutputBackoffDelay: delays.Add);
        var policy = ConductorAutonomyPolicy.Permissive with
        {
            EmptyOutputRetryInitialDelaySeconds = 1,
            EmptyOutputRetryBackoffMultiplier = 2,
            EmptyOutputRetryMaxDelaySeconds = 3
        };

        driver.AdvanceOnce(goal, policy);

        Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(2), delays[0]);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Blocked_facts_escalates")]
    public void ConductorDriverBlockedFactsEscalates()
    {
        var (_, goal) = SimpleGoal();

        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(IsBlocked: true));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Blocked, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
    }

    // ── Manual policy ─────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Manual_policy_escalates_at_Created")]
    public void ConductorDriverManualPolicyEscalatesAtCreated()
    {
        var (_, goal) = SimpleGoal();
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Manual);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    // ── Policy-over-engine promotion gate ────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Conservative_policy_blocks_Behavior_risk_before_calling_land")]
    public void ConductorDriverConservativePolicyBlocksBehaviorRiskBeforeCallingLand()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;
        var landCalled = false;

        // Conservative: threshold DocsOnly → Behavior risk → Escalate (even if engine would Promote)
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Behavior,
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(escalated);
        Assert.False(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    // ── Dispatch-start retry on transient spawn failure ───────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_spawn_fail_then_recorded_start_success_advances_without_escalation")]
    public void ConductorDriverWorkspaceReadySpawnFailThenRecordedStartSuccessAdvancesWithoutEscalation()
    {
        var (_, goal) = SimpleGoal();
        var dispatchStartCalls = 0;
        var recordedStartCalls = 0;
        var shutdownCalled = false;
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                dispatchStartCalls++;
                return DispatchStartOutcome.SpawnFailed("Dispatched 1 task(s) but no processes started (spawn failed)");
            },
            startRecordedDispatches: _ =>
            {
                recordedStartCalls++;
                return DispatchStartOutcome.Started();
            },
            buildServerShutdown: () => { shutdownCalled = true; },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, dispatchStartCalls);
        Assert.Equal(1, recordedStartCalls);
        Assert.True(shutdownCalled);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_recoverable_sandbox_prep_action_is_remediated_and_start_retried")]
    public void ConductorDriverRecoverableSandboxPrepActionIsRemediatedAndStartRetried()
    {
        var (_, goal) = SimpleGoal();
        var action = new WorkerSandboxPrepRecoverableAction(
            Worktree: @"C:\repo\.orchestrator-worktrees\abc12345",
            SandboxRoot: @"C:\repo\.orchestrator-worktrees\abc12345\.mcg-sandbox",
            FailedRoot: @"C:\repo\.orchestrator-worktrees\abc12345",
            Reason: "Failed to apply inheritable Low integrity label.",
            RequiresRecursiveRemediation: true);
        var dispatchStartCalls = 0;
        var recordedStartCalls = 0;
        var recoveryCalls = 0;
        var retryTaskCalls = 0;
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                dispatchStartCalls++;
                return DispatchStartOutcome.RecoverableSandboxPrep(action);
            },
            startRecordedDispatches: _ =>
            {
                recordedStartCalls++;
                return DispatchStartOutcome.Started();
            },
            retryTask: (goalId, taskId, note) =>
            {
                retryTaskCalls++;
                return goal.Tasks.Single();
            },
            recoverSandboxPrep: received =>
            {
                Assert.True(ReferenceEquals(action, received));
                recoveryCalls++;
                return true;
            },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, dispatchStartCalls);
        Assert.Equal(1, recordedStartCalls);
        Assert.Equal(1, recoveryCalls);
        Assert.Equal(0, retryTaskCalls);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_recorded_start_mixed_started_and_recovery_keeps_recovery_action")]
    public void ConductorDriverRecordedStartMixedStartedAndRecoveryKeepsRecoveryAction()
    {
        var (_, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var action = NewSandboxRecoveryAction();
        var plan = new ProcessBatchPlan(
            goal.Id,
            goal.Objective,
            goal.Status,
            ProcessBatchActionKind.StartDispatches,
            ReadyCount: 1,
            SkippedCount: 0,
            Items: []);
        var result = new ProcessBatchExecutionResult(plan, [task], [action]);

        var outcome = ConductorDriver.ClassifyRecordedDispatchStartForConductor(result);

        Assert.Equal(DispatchStartOutcomeCategory.RecoverableSandboxPrep, outcome.Category);
        Assert.True(ReferenceEquals(action, outcome.SandboxPrepRecoveryAction));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_subscription_start_mixed_started_and_recovery_keeps_recovery_action")]
    public void ConductorDriverSubscriptionStartMixedStartedAndRecoveryKeepsRecoveryAction()
    {
        var (_, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var action = NewSandboxRecoveryAction();
        var plan = new ProcessBatchPlan(
            goal.Id,
            goal.Objective,
            goal.Status,
            ProcessBatchActionKind.StartDispatches,
            ReadyCount: 1,
            SkippedCount: 0,
            Items: []);
        var processResult = new ProcessBatchExecutionResult(plan, [task], [action]);
        var result = new SubscriptionStartResult(
            [new WorkerProfileDispatchResult(task, @"C:\repo\.orchestrator\prompts\task.md")],
            processResult,
            new ParallelExecutionPlan([], []),
            []);

        var outcome = ConductorDriver.ClassifySubscriptionStartForConductor(result);

        Assert.Equal(DispatchStartOutcomeCategory.RecoverableSandboxPrep, outcome.Category);
        Assert.True(ReferenceEquals(action, outcome.SandboxPrepRecoveryAction));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_spawn_fail_twice_escalates_after_exactly_one_retry")]
    public void ConductorDriverWorkspaceReadySpawnFailTwiceEscalatesAfterExactlyOneRetry()
    {
        var (_, goal) = SimpleGoal();
        var callCount = 0;
        var shutdownCalled = false;
        string? escalationReason = null;
        const string spawnFailReason = "Dispatched 1 task(s) but no processes started (spawn failed)";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                callCount++;
                return DispatchStartOutcome.SpawnFailed(spawnFailReason);
            },
            buildServerShutdown: () => { shutdownCalled = true; },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(2, callCount);
        Assert.True(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(spawnFailReason, escalationReason);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_empty_batch_with_assigned_tasks_holds_not_escalates")]
    public void ConductorDriverWorkspaceReadyEmptyBatchWithAssignedTasksHoldsNotEscalates()
    {
        var (_, goal) = SimpleGoal();
        var callCount = 0;
        var shutdownCalled = false;
        const string emptyBatchReason = "No tasks in ready batch; goal may have no assigned or ready tasks";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                callCount++;
                return DispatchStartOutcome.EmptyBatch(emptyBatchReason);
            },
            buildServerShutdown: () => { shutdownCalled = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, callCount);
        Assert.False(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held); // assigned tasks exist → Held, not Escalated
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_started_first_try_advances_without_retry")]
    public void ConductorDriverWorkspaceReadyStartedFirstTryAdvancesWithoutRetry()
    {
        var (_, goal) = SimpleGoal();
        var callCount = 0;
        var shutdownCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                callCount++;
                return DispatchStartOutcome.Started();
            },
            buildServerShutdown: () => { shutdownCalled = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, callCount);
        Assert.False(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Permissive_policy_allows_Broad_risk_land")]
    public void ConductorDriverPermissivePolicyAllowsBroadRiskLand()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;

        // Permissive: threshold Broad → all risk tiers → Auto
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Broad,
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    // ── Result metadata ───────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_result_carries_goalId_goalPrefix_and_policyName")]
    public void ConductorDriverResultCarriesGoalIdGoalPrefixAndPolicyName()
    {
        var (_, goal) = SimpleGoal();
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(goal.Id.Value, result.GoalId);
        Assert.Equal(goal.Id.Value[..8], result.GoalPrefix);
        Assert.Equal("Conservative", result.PolicyName);
    }

    // ── Pre-landing rebase (Defect 1 fix) ────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_disjoint_advance_rebase_lands_without_escalation")]
    public void ConductorDriverVerifiedDisjointAdvanceRebaseLandsWithoutEscalation()
    {
        // Goal branch behind an advanced-but-disjoint main: rebase succeeds, landing proceeds.
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Broad,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Rebased, "goal/test", "Rebased onto main", [], null),
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(landCalled);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_rebase_conflict_escalates_with_conflict_reason")]
    public void ConductorDriverVerifiedRebaseConflictEscalatesWithConflictReason()
    {
        // Genuine overlapping-hunk conflict: rebase returns Conflict → escalate, land is never called.
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;
        string? escalationReason = null;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Broad,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Conflict, "goal/test",
                "Rebase found conflicts", ["src/Foo.cs"], null),
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Verified, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
        Assert.True(escalationReason is not null);
        Assert.Contains("conflict", escalationReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/Foo.cs", escalationReason!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Dispatch-start reason clarity (Defect 2 fix) ─────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_empty_batch_with_assigned_tasks_held_reason_includes_batch_reason")]
    public void ConductorDriverWorkspaceReadyEmptyBatchWithAssignedTasksHeldReasonIncludesBatchReason()
    {
        var (_, goal) = SimpleGoal();
        string? escalationReason = null;
        const string emptyBatchReason = "No tasks in ready batch; goal may have no assigned or ready tasks";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ => DispatchStartOutcome.EmptyBatch(emptyBatchReason),
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held); // assigned tasks exist → Held, not Escalated
        Assert.Null(escalationReason); // writeEscalation not called for Held
        var heldReason = ((ConductorAdvanceOutcome.Held)result.Outcome).Reason;
        Assert.True(heldReason.Contains(goal.Id.Value, StringComparison.Ordinal));
        Assert.True(heldReason.Contains(goal.Tasks.Single().Id.Value, StringComparison.Ordinal));
        Assert.True(heldReason.Contains(emptyBatchReason, StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_reason_names_each_assigned_task_blocker")]
    public void ConductorDriverEmptyBatchReasonNamesEachAssignedTaskBlocker()
    {
        var developerId = TaskId.New().Value;
        var testerId = TaskId.New().Value;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-blocked-batch",
                    "Explain blocked batch",
                    GoalStatus.Active,
                    [
                        new TaskSnapshot(developerId, "Developer still running.", AgentRole.Developer, WorkTaskStatus.Running, null, null, null, [], null, null),
                        new TaskSnapshot(testerId, "Tester waits.", AgentRole.Tester, WorkTaskStatus.Assigned, null, null, null, [], null, null)
                    ],
                    [])
            ],
            []));
        var goal = kernel.Goals.Single();

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => DispatchStartOutcome.EmptyBatch("No tasks in ready batch; goal may have no assigned or ready tasks"),
            evaluateReadiness: _ => new DispatchReadinessReady());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.True(held.Reason.Contains(goal.Id.Value, StringComparison.Ordinal));
        Assert.True(held.Reason.Contains(testerId, StringComparison.Ordinal));
        Assert.True(held.Reason.Contains(developerId, StringComparison.Ordinal));
        Assert.True(held.Reason.Contains("predecessor", StringComparison.OrdinalIgnoreCase));
        Assert.True(held.Reason.Contains("Running, not Completed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_spawn_failure_escalation_names_spawn_reason")]
    public void ConductorDriverWorkspaceReadySpawnFailureEscalationNamesSpawnReason()
    {
        var (_, goal) = SimpleGoal();
        string? escalationReason = null;
        const string spawnFailReason = "Dispatched 1 task(s) but no processes started (spawn failed)";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ => DispatchStartOutcome.SpawnFailed(spawnFailReason),
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(spawnFailReason, escalationReason);
        Assert.Contains("spawn", escalationReason!, StringComparison.OrdinalIgnoreCase);
    }
}
