using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsTrainStartWidthOccupancy : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void TrainStartedAtWidthOne_HoldsFourthGoalWithoutSoloAttempt() =>
        AssertTrainOccupiesWidth(goalCount: 4);

    [Fact]
    public void TrainStartedAtWidthOne_EntersNoCohortForRemainingPair() =>
        AssertTrainOccupiesWidth(goalCount: 5);

    private static void AssertTrainOccupiesWidth(int goalCount)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var previousTrainDisabled = Environment.GetEnvironmentVariable("MCG_MERGE_TRAIN_DISABLED");
        try
        {
            Environment.SetEnvironmentVariable("MCG_MERGE_TRAIN_DISABLED", "0");
            ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = CreateReadyGoals(kernel, repo, goalCount);
            Assert.All(goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            var verifier = new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("train width", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "train-width-green.trx")]));
            var cleanup = CreateIsolatedCleanupContext(repo);
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                runAcceptanceAttemptsInCurrentProcess: true, cleanupHooks: cleanup.Hooks);
            Assert.True(driver.MergeTrainsEnabled);
            Assert.True(driver.AcceptanceCohortsEnabled);
            var starts = new BackgroundGateStartHarness();
            // Capture keeps the train live throughout the tick without running a background task.
            starts.Capture(driver);
            var logPath = Path.Combine(repo, "train-width-conduct.jsonl");
            BatchTickSummary? observed = null;
            _ = new ConductorBatchLoop(conductEventLogWriter: new ConductEventLogWriter(logPath))
                .Run(kernel, driver, ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 1 },
                    Path.Combine(repo, "stop-does-not-exist"), maxIterations: 1,
                    onTick: summary => observed = summary);
            var tick = Assert.IsType<BatchTickSummary>(observed);
            Assert.Equal(1, tick.Tick);
            var progress = tick.ProgressLines ?? [];
            Assert.Contains(progress, line =>
                line.Contains("ACCEPTANCE_TRAIN tick=1 members=", StringComparison.Ordinal) &&
                line.Contains("outcome=inflight", StringComparison.Ordinal));

            var trainMembers = driver.GetActiveCohortGateMemberGoalIds();
            Assert.Equal(3, trainMembers.Count);
            Assert.All(trainMembers, member => Assert.Contains(goals, goal => goal.Id.Value == member));
            var remaining = goals.Where(goal => !trainMembers.Contains(goal.Id.Value)).ToArray();
            Assert.Equal(goalCount - 3, remaining.Length);
            Assert.All(remaining, goal =>
            {
                Assert.DoesNotContain(progress, line =>
                    line.Contains($"ACCEPTANCE goal={goal.Id.Value[..8]}", StringComparison.Ordinal));
                Assert.Equal(GoalStatus.Verified, goal.Status);
            });
            Assert.Empty(driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts(
                remaining.Select(goal => goal.Id.Value).ToArray()));
            Assert.DoesNotContain(File.ReadAllLines(logPath), line =>
                line.Contains("ACCEPTANCE_COHORT_ENTRY tick=1 ", StringComparison.Ordinal));
            Assert.DoesNotContain(progress, line =>
                line.Contains("ACCEPTANCE_COHORT tick=1 members=", StringComparison.Ordinal));
            Assert.Contains(progress, line =>
                line.Contains("ADMISSION tick=1 result=deferred reason=parallel-acceptance-slot-cap cap=1 ",
                    StringComparison.Ordinal) &&
                line.Contains($"deferred={remaining.Length}", StringComparison.Ordinal));
            Assert.Equal(1, starts.StartCount);
            Assert.Equal(1, starts.PendingCount);
            Assert.Equal(0, verifier.RunCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_MERGE_TRAIN_DISABLED", previousTrainDisabled);
            ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();
            DeleteDirectory(repo);
        }
    }

    private static Goal[] CreateReadyGoals(AgentOrchestratorKernel kernel, string repo, int count)
    {
        var paths = new[]
        {
            "tests/Mcg.AgentOrchestrator.Core.Tests/WidthFirst.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WidthSecond.cs",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/WidthThird.cs",
            "src/Mcg.AgentOrchestrator.App/Orchestration/WidthFourth.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/WidthFifth.cs"
        };
        var goals = Enumerable.Range(0, count)
            .Select(index => CreateCompletedGoal(kernel, $"Train width {index}", repo)).ToArray();
        for (var index = 0; index < count; index++)
        {
            var goal = goals[index];
            var candidate = CreateWorktreeCandidate(repo, goal.Id, paths[index], $"width {index}");
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
