using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliPersistentStateRunnerAcceptanceFailureRecoveryTests : CliCommandTestBase
{
    [Xunit.Fact]
    public void AcceptanceFailed_StalePairWithCancelledTask_PersistsRecovery()
    {
        var root = CreateShortAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Persist stale acceptance recovery", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-09-03T12:00:00Z")));
        var oldMain = RunGitOutput(root, "rev-parse", "HEAD").Trim();
        var worktree = CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var oldBranch = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
        Xunit.Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
        Xunit.Assert.True(kernel.ReconcileGoalAcceptanceFailed(
            goal.Id,
            ["old acceptance failure"],
            "Acceptance failed against the old candidate pair.",
            oldBranch,
            oldMain));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "Operator deliberately descoped task.");
        GoalOperationJournal.AcceptanceFailed(
            root,
            goal,
            "acceptance",
            oldBranch,
            oldMain,
            "old acceptance failure");
        File.WriteAllText(Path.Combine(root, "main-change.txt"), "main moved");
        RunGitOutput(root, "add", "main-change.txt");
        RunGitOutput(root, "commit", "-m", "Move main");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var verifier = new ProbeAcceptanceVerifier(() => { });
        var stableSlotSelections = 0;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["acceptance", "--keep-workspace", "--no-record"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: verifier,
            stableSlotSelector: (_, _) =>
            {
                Interlocked.Increment(ref stableSlotSelections);
                return CreateFakeStableSlotLease(root);
            }));

        Xunit.Assert.Contains("superseded failure is historical", output);
        Xunit.Assert.Equal(1, stableSlotSelections);
        Xunit.Assert.Equal(1, verifier.RunCount);
        var storedGoal = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
        Xunit.Assert.Equal(GoalStatus.Completed, storedGoal.Status);
        Xunit.Assert.Null(storedGoal.LatestAcceptanceFailure);
        Xunit.Assert.Contains(storedGoal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("recorded branch=", StringComparison.Ordinal) &&
            evt.Message.Contains("current branch=", StringComparison.Ordinal));
    }

    private static DotnetBuildEnvironmentLease CreateFakeStableSlotLease(string root)
    {
        var leaseRoot = Path.Combine(root, ".fake-build-slot");
        Directory.CreateDirectory(leaseRoot);
        var lockPath = Path.Combine(leaseRoot, $"{Guid.NewGuid():N}.lock");
        var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var environment = new DotnetBuildEnvironment(
            "fake-acceptance-recovery-slot",
            leaseRoot,
            Path.Combine(leaseRoot, "artifacts"),
            lockPath,
            [],
            "build-0",
            BuildPermitIndex: 0);
        return new DotnetBuildEnvironmentLease(environment, stream);
    }
}
