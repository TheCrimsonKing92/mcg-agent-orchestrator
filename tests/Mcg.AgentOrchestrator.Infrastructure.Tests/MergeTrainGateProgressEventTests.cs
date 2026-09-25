using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class MergeTrainGateProgressEventTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void MergeTrainGate_ReportsProgressForEveryMemberWhileAttemptRuns()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = new[]
            {
                CreateCompletedGoal(kernel, "First train progress member", repo),
                CreateCompletedGoal(kernel, "Second train progress member", repo),
                CreateCompletedGoal(kernel, "Third train progress member", repo)
            };
            var paths = new[]
            {
                "tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs",
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/TrainThird.cs"
            };
            for (var index = 0; index < goals.Length; index++)
            {
                var candidate = CreateWorktreeCandidate(repo, goals[index].Id, paths[index], $"member {index}");
                kernel.RecordGoalRefinement(goals[index].Id, new RefinedSpec(goals[index].Objective,
                    ["The train acceptance gate passes"], VerificationClass.TestVerifiable, [], []));
                kernel.MapCriterionEvidenceOwner(goals[index].Id, 0, 1,
                    CriterionEvidenceOwner.Acceptance, "test",
                    CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
            }
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new GateProgressReportingVerifier(workspace.ConductEventsLogPath,
            [
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("train gate", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "train-progress-green.trx")])
            ]);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectTrainSelection(driver, goals);

            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive);

            var receipt = Assert.IsType<MergeTrainReceipt>(result.Receipt);
            var duringAttempt = Assert.Single(verifier.EventsAfterProgress);
            var trainTag = $"train={receipt.Identity.Value[..(MergeTrainIdentity.Version.Length + 9)]}";
            var orderedMembers = string.Join(',', receipt.Identity.Members.Select(member => member.GoalId.Value[..8]));
            foreach (var member in receipt.Identity.Members)
            {
                var goalId = member.GoalId.Value[..8];
                Assert.Contains(duringAttempt, record => record.EventKind == "gate-progress" &&
                    record.GoalId == goalId &&
                    record.Detail.Contains(trainTag, StringComparison.Ordinal) &&
                    record.Detail.Contains($"member={goalId}", StringComparison.Ordinal) &&
                    record.Detail.Contains($"members={orderedMembers}", StringComparison.Ordinal));
            }
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
