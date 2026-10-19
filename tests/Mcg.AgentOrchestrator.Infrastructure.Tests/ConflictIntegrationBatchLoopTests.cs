using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: inherited cohort fixture owns all repositories, stores and cleanup hooks.
public sealed class ConflictIntegrationBatchLoopTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void PlannerConflict_BeginsIntegrationInsteadOfEscalating()
    {
        var (repo, kernel, goals) = CreateReadyTrain();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            const string conflictPath = "src/Mcg.AgentOrchestrator.Core/PlannerConflict.cs";
            var fullPath = Path.Combine(repo, conflictPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "baseline\n");
            RunGit(repo, "add", conflictPath);
            RunGit(repo, "commit", "-m", "Conflict baseline");
            var excluded = kernel.CreateGoal("Verified conflict outside the active train",
                [new(TaskId.New(), "Implement", AgentRole.Developer), new(TaskId.New(), "Verify", AgentRole.Tester)]);
            kernel.ActivateGoal(excluded.Id, AgentCatalog.Default().Agents);
            foreach (var task in excluded.Tasks) ConductorDriverTests.PassVerification(kernel, excluded, task);
            _ = CreateWorktreeCandidate(repo, excluded.Id, conflictPath, "goal change\n");
            File.WriteAllText(fullPath, "main change\n");
            RunGit(repo, "add", conflictPath);
            RunGit(repo, "commit", "-m", "Main conflicting change");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(new AcceptanceVerificationResult(true, false, 0, null));
            var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var starts = new BackgroundGateStartHarness();
            starts.Capture(driver);
            var policy = ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 1 };
            var train = driver.RunMergeTrain(ProjectTrainSelection(driver, goals), goals, policy, runGateInBackground: true);
            Assert.Contains("outcome=inflight", train.Detail);
            Assert.Equal(1, starts.PendingCount);
            Assert.DoesNotContain(excluded.Id.Value, driver.GetActiveCohortGateMemberGoalIds());
            var projection = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(driver.ProjectGateReadyCandidate(excluded, policy));
            Assert.Equal(GateReadyCandidateExclusionReason.MergeConflict, projection.Reason);
            Assert.Equal(new[] { conflictPath }, projection.ConflictPaths);

            var summary = new ConductorBatchLoop().Run(kernel, driver, policy, Path.Combine(repo, "stop-does-not-exist"), maxIterations: 1);

            var escalationPath = Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json");
            if (File.Exists(escalationPath))
            {
                using var store = JsonDocument.Parse(File.ReadAllText(escalationPath));
                Assert.DoesNotContain(store.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("goalId").GetString() == excluded.Id.Value);
            }
            Assert.Equal(0, summary.Escalated);
            Assert.Single(GoalOperationJournal.Read(repo, excluded.Id).LatestByOperation, item => item.Operation == ConductorConflictIntegration.OperationName);
            var developer = excluded.Tasks.First();
            Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
            Assert.Equal(RetryCause.MainDriftConflict, developer.PendingRetryCause);
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(1, starts.PendingCount);
        }
        finally { DeleteDirectory(repo); }
    }

    private static (string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals) CreateReadyTrain()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        AddAcceptanceManifest(repo);
        var kernel = new AgentOrchestratorKernel();
        var goals = new[]
        {
            CreateCompletedGoal(kernel, "First train member", repo),
            CreateCompletedGoal(kernel, "Second train member", repo),
            CreateCompletedGoal(kernel, "Third train member", repo)
        };
        var candidates = new[]
        {
            CreateWorktreeCandidate(repo, goals[0].Id, "tests/Mcg.AgentOrchestrator.Core.Tests/RestartFirst.cs", "first"),
            CreateWorktreeCandidate(repo, goals[1].Id, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RestartSecond.cs", "second"),
            CreateWorktreeCandidate(repo, goals[2].Id, "tests/Mcg.AgentOrchestrator.Dashboard.Tests/RestartThird.cs", "third")
        };
        foreach (var (goal, candidate) in goals.Zip(candidates))
        {
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
            { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "test",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
        }
        return (repo, kernel, goals);
    }
}
