using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static LandingExecutorTests;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsRefusedCarryRelaunch : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsRefusedCarryRelaunch(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void RefusedCarryHoldsAcrossTwoTicksWithoutRelaunchingPassedCandidate()
    {
        var repository = CreateGitRepository();
        var attemptRoot = CreateTempDirectory("mcg-refused-carry-attempts");
        try
        {
            var baseSha = ReadGit(repository, "rev-parse", "main");
            ReadGit(repository, "checkout", "-b", "candidate-old");
            AppendCommit(repository, "src/goal.txt", "reviewed goal line");
            var boundSha = ReadGit(repository, "rev-parse", "HEAD");
            ReadGit(repository, "checkout", "main");
            ReadGit(repository, "checkout", "-b", "candidate-new", baseSha);
            AppendCommit(repository, "src/goal.txt", "changed goal line");
            var candidateSha = ReadGit(repository, "rev-parse", "HEAD");
            var mainSha = ReadGit(repository, "rev-parse", "main");

            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Update src/goal.txt");
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                goal.Objective, ["Full acceptance passes"], VerificationClass.TestVerifiable, [], []));
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: boundSha);

            var acceptanceRuns = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) =>
                {
                    acceptanceRuns++;
                    return new AcceptanceVerificationSummary(true, [], BranchHeadSha: candidateSha);
                },
                resolveAcceptanceHeads: _ => (candidateSha, mainSha),
                getLandingFileScopes: _ => ["src/goal.txt"],
                executionDirectory: repository,
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot, repository, runInline: true));

            var first = new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            Assert.Equal(1, acceptanceRuns);
            Assert.Equal(1, first.Held);
            Assert.Equal(GoalStatus.Verified, goal.Status);

            for (var tick = 0; tick < 2; tick++)
            {
                BatchTickSummary? observed = null;
                var summary = new ConductorBatchLoop().Run(
                    kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
                    maxIterations: 1, onTick: item => observed = item);
                Assert.Equal(1, summary.Held);
                Assert.Equal(1, acceptanceRuns);
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.Contains(observed!.ProgressLines!, line =>
                    line.Contains("reason=range-diff-not-identical", StringComparison.Ordinal) &&
                    line.Contains(boundSha, StringComparison.Ordinal) &&
                    line.Contains("criterion-evidence-map --goal", StringComparison.Ordinal));
            }

            Assert.Single(Directory.GetFiles(Path.Combine(attemptRoot, goal.Id.Value), "*.attempt.json"));

            ReadGit(repository, "checkout", "main");
            AppendCommit(repository, "src/main.txt", "new main commit");
            mainSha = ReadGit(repository, "rev-parse", "main");
            ReadGit(repository, "checkout", "candidate-new");
            new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            Assert.Equal(2, acceptanceRuns);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
            TryDeleteDirectory(repository);
        }
    }
}
