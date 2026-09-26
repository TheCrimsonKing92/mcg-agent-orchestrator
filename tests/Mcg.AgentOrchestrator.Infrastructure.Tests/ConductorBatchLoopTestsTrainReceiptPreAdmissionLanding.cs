using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsTrainReceiptPreAdmissionLanding : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void PassedTrainReceipt_AdmissionFull_LandsBeforeAdmissionWithoutGate()
    {
        var fixture = CreatePassedTrain();
        try
        {
            var (repo, kernel, goals, driver, verifier, store, receipt) = fixture;
            var occupantGoals = CreateReadyGoals(kernel, repo, "Occupant", 2);
            var occupantSelection = ProjectSelection(driver, occupantGoals[0], occupantGoals[1]);
            var pending = new BackgroundGateStartHarness();
            pending.Capture(driver);
            var occupant = driver.RunAcceptanceCohort(occupantSelection, occupantGoals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Contains("outcome=inflight", occupant.Detail, StringComparison.Ordinal);
            Assert.Equal(1, driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);

            var landings = new List<ConductorLandingReceipt>();
            driver.SuccessfulLandingSink = landings.Add;
            var policy = ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 1 };
            var tick = RunOneTick(repo, kernel, driver, policy);
            var progress = string.Join(" | ", tick.ProgressLines ?? []);

            Assert.Contains($"outcome=passed attempts=1 landings=3 receipt={receipt.ReceiptId}", progress);
            Assert.All(goals, goal => Assert.True(goal.Status == GoalStatus.Completed,
                $"Expected {goal.Id.Value} to land; status={goal.Status}; progress={progress}"));
            Assert.Equal(3, landings.Count);
            Assert.Equal(1, verifier.RunCount);
            Assert.Equal(1, pending.StartCount);
            Assert.Equal(1, pending.PendingCount);
            Assert.Equal(1, driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);
            Assert.Single(store.ReadPassedReceiptsForGoal(goals[0].Id));
        }
        finally
        {
            DeleteDirectory(fixture.Repo);
        }
    }

    [Fact]
    public void PassedTrainReceipt_MainMoved_NotLandedMembersReenterSelection()
    {
        var fixture = CreatePassedTrain();
        try
        {
            File.WriteAllText(Path.Combine(fixture.Repo, "unrelated-main.txt"), "later main");
            RunGit(fixture.Repo, "add", "unrelated-main.txt");
            RunGit(fixture.Repo, "commit", "-m", "Advance main after train gate");
            AssertStaleReceiptReentersSelection(fixture, "moved=main");
        }
        finally
        {
            DeleteDirectory(fixture.Repo);
        }
    }

    [Fact]
    public void PassedTrainReceipt_MemberHeadChanged_NotLandedMembersReenterSelection()
    {
        var fixture = CreatePassedTrain();
        try
        {
            var changed = fixture.Goals[1];
            var candidate = CreateWorktreeCandidate(fixture.Repo, changed.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ChangedMember.cs", "changed");
            fixture.Kernel.MapCriterionEvidenceOwner(changed.Id, 0, 1,
                CriterionEvidenceOwner.Acceptance, "test",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
            AssertStaleReceiptReentersSelection(fixture, $"moved=member:{changed.Id.Value[..8]}");
        }
        finally
        {
            DeleteDirectory(fixture.Repo);
        }
    }

    private static void AssertStaleReceiptReentersSelection(
        (string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals, ConductorDriver Driver,
            SequenceAcceptanceVerifier Verifier, MergeTrainAcceptanceStore Store, MergeTrainReceipt Receipt) fixture,
        string moved)
    {
        var pending = new BackgroundGateStartHarness();
        pending.Capture(fixture.Driver);
        var logPath = Path.Combine(fixture.Repo, "stale-conduct.jsonl");
        var tick = RunOneTick(fixture.Repo, fixture.Kernel, fixture.Driver,
            ConductorAutonomyPolicy.Permissive, logPath);
        var progress = string.Join(" | ", tick.ProgressLines ?? []);
        Assert.DoesNotContain("outcome=passed", progress, StringComparison.Ordinal);
        Assert.All(fixture.Goals, goal => Assert.NotEqual(GoalStatus.Completed, goal.Status));
        Assert.Contains(File.ReadAllLines(logPath), line =>
            line.Contains("TRAIN_RECEIPT_STALE", StringComparison.Ordinal) &&
            line.Contains($"receipt={fixture.Receipt.ReceiptId}", StringComparison.Ordinal) &&
            line.Contains(moved, StringComparison.Ordinal));
        Assert.Equal(fixture.Receipt.ReceiptId,
            Assert.Single(fixture.Store.ReadPassedReceiptsForGoal(fixture.Goals[0].Id)).ReceiptId);
        Assert.Contains("ACCEPTANCE_TRAIN", progress, StringComparison.Ordinal);
        Assert.Contains("outcome=inflight", progress, StringComparison.Ordinal);
        Assert.Equal(1, pending.StartCount);
        Assert.Equal(1, fixture.Verifier.RunCount);
    }

    private static BatchTickSummary RunOneTick(string repo, AgentOrchestratorKernel kernel,
        ConductorDriver driver, ConductorAutonomyPolicy policy, string? logPath = null)
    {
        BatchTickSummary? observed = null;
        _ = new ConductorBatchLoop(conductEventLogWriter: logPath is null ? null :
            new ConductEventLogWriter(logPath)).Run(kernel, driver, policy,
            Path.Combine(repo, "stop-does-not-exist"), maxIterations: 1,
            onTick: summary => observed = summary);
        return Assert.IsType<BatchTickSummary>(observed);
    }

    private static (string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals, ConductorDriver Driver,
        SequenceAcceptanceVerifier Verifier, MergeTrainAcceptanceStore Store, MergeTrainReceipt Receipt)
        CreatePassedTrain()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        AddAcceptanceManifest(repo);
        var kernel = new AgentOrchestratorKernel();
        var goals = CreateReadyGoals(kernel, repo, "Passed train", 3);
        var workspace = OrchestratorWorkspace.ForDirectory(repo);
        var verifier = new SequenceAcceptanceVerifier(
            [new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("passed train", true, 0, null)],
                TestResultPaths: [WritePassingTrx(repo, "passed-train-green.trx")])]);
        var cleanup = CreateIsolatedCleanupContext(repo);
        var driver = new ConductorDriver(kernel, workspace, verifier,
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
            runAcceptanceAttemptsInCurrentProcess: true, cleanupHooks: cleanup.Hooks);
        var starts = new BackgroundGateStartHarness();
        starts.Inline(driver);
        var selection = ProjectTrainSelection(driver, goals);
        var started = driver.RunMergeTrain(selection, goals,
            ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
        Assert.Contains("outcome=inflight", started.Detail, StringComparison.Ordinal);
        var store = new MergeTrainAcceptanceStore(
            Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
        var receipt = Assert.Single(store.ReadPassedReceiptsForGoal(goals[0].Id));
        Assert.Equal(1, verifier.RunCount);
        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
        return (repo, kernel, goals, driver, verifier, store, receipt);
    }

    private static Goal[] CreateReadyGoals(AgentOrchestratorKernel kernel, string repo,
        string label, int count)
    {
        var paths = new[]
        {
            "tests/Mcg.AgentOrchestrator.Core.Tests/ReceiptFirst.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ReceiptSecond.cs",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/ReceiptThird.cs",
            "src/Mcg.AgentOrchestrator.App/Orchestration/ReceiptFourth.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/ReceiptFifth.cs"
        };
        var goals = Enumerable.Range(0, count)
            .Select(index => CreateCompletedGoal(kernel, $"{label} {index}", repo)).ToArray();
        for (var index = 0; index < count; index++)
        {
            var goal = goals[index];
            var candidate = CreateWorktreeCandidate(repo, goal.Id, paths[index], $"{label} {index}");
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
            {
                AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
            });
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1,
                CriterionEvidenceOwner.Acceptance, "test",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
        }
        return goals;
    }
}
