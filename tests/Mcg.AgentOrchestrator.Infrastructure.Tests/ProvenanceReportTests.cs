using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProvenanceReportTests
{
    private static readonly IReadOnlyList<AgentDefinition> DefaultAgents =
        AgentCatalog.Default().Agents;

    private const string WorkDir = "C:\\work";
    private const string DispatchCommand = "worker-cli run";

    // ── Core library tests ──────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Provenance_completed_goal_with_receipts_is_classified_Backed")]
    public void ProvenanceCompletedGoalWithReceiptsIsClassifiedBacked()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Ship feature X", [MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);
        RecordCompletedWithReceipt(kernel, goal, goal.Tasks[0]);

        var snapshot = kernel.BuildProvenanceReport(string.Empty);

        Xunit.Assert.Equal(1, snapshot.CompletedGoalCount);
        Xunit.Assert.Equal(1, snapshot.BackedGoalCount);
        Xunit.Assert.Equal(0, snapshot.UnbackedGoalCount);
        var record = Xunit.Assert.Single(snapshot.Goals);
        Xunit.Assert.Equal(ProvenanceStatus.Backed, record.ProvenanceStatus);
        Xunit.Assert.Equal(0, record.UnbackedTaskCount);
    }

    [Xunit.Fact(DisplayName = "Provenance_completed_goal_whose_task_lacks_receipt_is_Unbacked")]
    public void ProvenanceCompletedGoalWhoseTaskLacksReceiptIsUnbacked()
    {
        // Build via snapshot to produce a Completed goal where the task has no verification receipt.
        // This represents a state that could arise from corruption or a future weakening of the domain invariant.
        var kernel = BuildKernelWithUnbackedCompletedGoal(out _);

        var snapshot = kernel.BuildProvenanceReport(string.Empty);

        Xunit.Assert.Equal(1, snapshot.CompletedGoalCount);
        Xunit.Assert.Equal(0, snapshot.BackedGoalCount);
        Xunit.Assert.Equal(1, snapshot.UnbackedGoalCount);
        var record = Xunit.Assert.Single(snapshot.Goals);
        Xunit.Assert.Equal(ProvenanceStatus.Unbacked, record.ProvenanceStatus);
        Xunit.Assert.Equal(1, record.UnbackedTaskCount);
    }

    [Xunit.Fact(DisplayName = "Provenance_dogfood_reference_to_nonexistent_goal_is_flagged")]
    public void ProvenanceDogfoodReferenceToNonexistentGoalIsFlagged()
    {
        var kernel = new AgentOrchestratorKernel();
        // No goals in state — any dogfood reference resolves as absent
        var dogfoodText = "## 2026-06-14 - Shipped goal aa11bb22 to production";

        var snapshot = kernel.BuildProvenanceReport(dogfoodText);

        Xunit.Assert.Empty(snapshot.Goals);
        var reference = Xunit.Assert.Single(snapshot.UnbackedDogfoodReferences);
        Xunit.Assert.Equal("aa11bb22", reference.GoalIdPrefix);
        Xunit.Assert.True(reference.IsAbsent);
    }

    [Xunit.Fact(DisplayName = "Provenance_dogfood_reference_to_unbacked_goal_is_flagged")]
    public void ProvenanceDogfoodReferenceToUnbackedGoalIsFlagged()
    {
        var kernel = BuildKernelWithUnbackedCompletedGoal(out var goalId);
        var prefix = goalId.Value[..8];
        var dogfoodText = $"## 2026-06-14 - Landed goal {prefix} successfully";

        var snapshot = kernel.BuildProvenanceReport(dogfoodText);

        Xunit.Assert.Equal(1, snapshot.UnbackedGoalCount);
        var reference = Xunit.Assert.Single(snapshot.UnbackedDogfoodReferences);
        Xunit.Assert.Equal(prefix, reference.GoalIdPrefix);
        Xunit.Assert.False(reference.IsAbsent);
    }

    [Xunit.Fact(DisplayName = "Provenance_dogfood_reference_to_backed_goal_is_not_flagged")]
    public void ProvenanceDogfoodReferenceToBackedGoalIsNotFlagged()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Ship feature W", [MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);
        RecordCompletedWithReceipt(kernel, goal, goal.Tasks[0]);

        var prefix = goal.Id.Value[..8];
        var dogfoodText = $"## 2026-06-14 - Landed goal {prefix} successfully";

        var snapshot = kernel.BuildProvenanceReport(dogfoodText);

        Xunit.Assert.Equal(1, snapshot.BackedGoalCount);
        Xunit.Assert.Empty(snapshot.UnbackedDogfoodReferences);
    }

    [Xunit.Fact(DisplayName = "Provenance_empty_state_returns_zero_metrics_without_exception")]
    public void ProvenanceEmptyStateReturnsZeroMetricsWithoutException()
    {
        var kernel = new AgentOrchestratorKernel();

        var snapshot = kernel.BuildProvenanceReport(string.Empty);

        Xunit.Assert.Equal(0, snapshot.CompletedGoalCount);
        Xunit.Assert.Equal(0, snapshot.BackedGoalCount);
        Xunit.Assert.Equal(0, snapshot.UnbackedGoalCount);
        Xunit.Assert.Empty(snapshot.Goals);
        Xunit.Assert.Empty(snapshot.UnbackedDogfoodReferences);
    }

    [Xunit.Fact(DisplayName = "Provenance_non_completed_goals_are_excluded_from_report")]
    public void ProvenanceNonCompletedGoalsAreExcludedFromReport()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Active work", [MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);
        // Goal remains Active (not Completed)

        var snapshot = kernel.BuildProvenanceReport(string.Empty);

        Xunit.Assert.Equal(0, snapshot.CompletedGoalCount);
        Xunit.Assert.Empty(snapshot.Goals);
    }

    [Xunit.Fact(DisplayName = "Provenance_verified_goals_are_excluded_from_completed_report")]
    public void ProvenanceVerifiedGoalsAreExcludedFromCompletedReport()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Verified but unmerged work", [MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);
        RecordVerifiedWithReceipt(kernel, goal, goal.Tasks[0]);

        var snapshot = kernel.BuildProvenanceReport($"Landed goal {goal.Id.Value[..8]}");

        Assert.Equal(GoalStatus.Verified, goal.Status);
        Xunit.Assert.Equal(0, snapshot.CompletedGoalCount);
        Xunit.Assert.Empty(snapshot.Goals);
        var reference = Xunit.Assert.Single(snapshot.UnbackedDogfoodReferences);
        Assert.True(reference.IsAbsent);
    }

    // ── Acceptance gate tests ───────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Acceptance_gate_blocks_unbacked_completed_goal_with_provenance_blocker")]
    public void AcceptanceGateBlocksUnbackedCompletedGoalWithProvenanceBlocker()
    {
        var kernel = BuildKernelWithUnbackedCompletedGoal(out _);
        var goal = kernel.Goals.Single();

        var bundle = GoalAcceptanceEvidenceBundleBuilder.Build(
            kernel,
            goal,
            worktreePath: null,
            verification: null,
            verificationSkipped: true,
            buildStorageRoot: null, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);

        Assert.False(bundle.Passed);
        Assert.Equal(DotnetBuildEnvironmentManager.GoalRoot(goal.Id), bundle.BuildEnvironment.RootPath);
        var provenanceBlocker = bundle.Blockers.FirstOrDefault(b =>
            b.Kind.Equals("provenance-check-failed", StringComparison.Ordinal));
        Xunit.Assert.NotNull(provenanceBlocker);
        Assert.True(provenanceBlocker!.Message.Contains("no verification receipt", StringComparison.OrdinalIgnoreCase));
    }

    // ── Commit SHA cross-check tests ────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Provenance_dogfood_commit_sha_absent_from_git_history_is_flagged")]
    public void ProvenanceDogfoodCommitShaAbsentFromGitHistoryIsFlagged()
    {
        var kernel = new AgentOrchestratorKernel();
        var dogfoodText = "Landed as `abc1234` in production";

        var snapshot = kernel.BuildProvenanceReport(dogfoodText, sha => false);

        Xunit.Assert.Single(snapshot.UnbackedCommitShas);
        Assert.Equal("abc1234", snapshot.UnbackedCommitShas[0]);
    }

    [Xunit.Fact(DisplayName = "Provenance_dogfood_commit_sha_present_in_git_history_is_not_flagged")]
    public void ProvenanceDogfoodCommitShaPresentInGitHistoryIsNotFlagged()
    {
        var kernel = new AgentOrchestratorKernel();
        var dogfoodText = "Landed as `abc1234` in production";

        var snapshot = kernel.BuildProvenanceReport(dogfoodText, sha => true);

        Xunit.Assert.Empty(snapshot.UnbackedCommitShas);
    }

    [Xunit.Fact(DisplayName = "Provenance_dogfood_commit_sha_via_commit_keyword_is_flagged_when_absent")]
    public void ProvenanceDogfoodCommitShaViaCommitKeywordIsFlaggedWhenAbsent()
    {
        var kernel = new AgentOrchestratorKernel();
        var dogfoodText = "Fixed bug in commit abc1234ef and committed def5678a separately.";

        var snapshot = kernel.BuildProvenanceReport(dogfoodText, sha => false);

        Assert.True(snapshot.UnbackedCommitShas.Count == 2);
        Assert.True(snapshot.UnbackedCommitShas.Contains("abc1234ef", StringComparer.OrdinalIgnoreCase));
        Assert.True(snapshot.UnbackedCommitShas.Contains("def5678a", StringComparer.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "Provenance_commit_sha_check_not_run_when_no_sha_exists_func")]
    public void ProvenanceCommitShaCheckNotRunWhenNoShaExistsFunc()
    {
        var kernel = new AgentOrchestratorKernel();
        var dogfoodText = "Landed as `abc1234` in production";

        var snapshot = kernel.BuildProvenanceReport(dogfoodText);

        Xunit.Assert.Empty(snapshot.UnbackedCommitShas);
    }

    // ── CLI command tests ───────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Provenance_CLI_throws_when_unbacked_completed_goal_exists")]
    public void ProvenanceCliThrowsWhenUnbackedCompletedGoalExists()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = BuildKernelWithUnbackedCompletedGoal(out _);
        IReadOnlyList<AgentDefinition> agents = DefaultAgents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        InvalidOperationException? caught = null;
        var captured = CaptureConsole(() =>
        {
            try
            {
                CliCommandDispatcher.ExecuteCommand(
                    ["provenance"],
                    kernel,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;
            }
        });

        Xunit.Assert.NotNull(caught);
        Xunit.Assert.Contains("Provenance check failed", caught!.Message);
        Xunit.Assert.Contains("unbacked", caught.Message);
        Xunit.Assert.Contains("UNBACKED", captured);
    }

    [Xunit.Fact(DisplayName = "Provenance_CLI_returns_without_throwing_when_all_goals_backed")]
    public void ProvenanceCliReturnsWithoutThrowingWhenAllGoalsBacked()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Backed goal", [MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);
        RecordCompletedWithReceipt(kernel, goal, goal.Tasks[0]);

        IReadOnlyList<AgentDefinition> agents = DefaultAgents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        bool changed = false;
        var captured = CaptureConsole(() =>
        {
            changed = CliCommandDispatcher.ExecuteCommand(
                ["provenance"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });

        Xunit.Assert.False(changed);
        Xunit.Assert.Contains("BACKED", captured);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static TaskSpec MakeTask() =>
        new(TaskId.New(), "Developer implementation task", AgentRole.Developer);

    private static void RecordCompletedWithReceipt(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task)
    {
        var dispatch = new TaskDispatchRecord(
            "worker-cli", DispatchCommand, WorkDir, DateTimeOffset.UtcNow,
            ProviderName: "Anthropic", ModelName: "claude-sonnet-4-6");
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

        var receipt = new TaskVerificationRecord(
            DispatchCommand, WorkDir, 0,
            "Task completed successfully.",
            string.Empty,
            DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, receipt);
        if (goal.Status == GoalStatus.Verified)
        {
            kernel.CompleteGoal(goal.Id, "Completed after durable integration and cleanup evidence.");
        }
    }

    private static void RecordVerifiedWithReceipt(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task)
    {
        var dispatch = new TaskDispatchRecord(
            "worker-cli", DispatchCommand, WorkDir, DateTimeOffset.UtcNow,
            ProviderName: "Anthropic", ModelName: "claude-sonnet-4-6");
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

        var receipt = new TaskVerificationRecord(
            DispatchCommand, WorkDir, 0,
            "Task completed successfully.",
            string.Empty,
            DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, receipt);
    }

    // Creates a kernel with a Completed goal whose single task has no verification receipt.
    // This scenario requires a snapshot bypass since the domain normally enforces that a
    // Completed goal's tasks have passing verifications.
    private static AgentOrchestratorKernel BuildKernelWithUnbackedCompletedGoal(out GoalId goalId)
    {
        var id = GoalId.New();
        goalId = id;
        var taskId = TaskId.New();

        var snapshot = new OrchestratorSnapshot(
            Goals:
            [
                new GoalSnapshot(
                    Id: id.Value,
                    Objective: "Unbacked completed goal",
                    Status: GoalStatus.Completed,
                    Tasks:
                    [
                        new TaskSnapshot(
                            Id: taskId.Value,
                            Description: "Developer task with no receipt",
                            RequiredRole: AgentRole.Developer,
                            Status: WorkTaskStatus.Completed,
                            AssignedAgentId: null,
                            LastExecution: null,
                            LastVerification: null,
                            VerificationHistory: [],
                            LastDispatch: null,
                            LastProcess: null)
                    ],
                    Timeline:
                    [
                        new ProgressEventSnapshot(id.Value, null, ProgressKind.GoalCreated, "Goal created.", DateTimeOffset.UtcNow.AddMinutes(-5)),
                        new ProgressEventSnapshot(id.Value, taskId.Value, ProgressKind.TaskCompleted, "Task done (no receipt).", DateTimeOffset.UtcNow)
                    ])
            ],
            HumanInputRequests: []);

        return AgentOrchestratorKernel.FromSnapshot(snapshot);
    }
}
