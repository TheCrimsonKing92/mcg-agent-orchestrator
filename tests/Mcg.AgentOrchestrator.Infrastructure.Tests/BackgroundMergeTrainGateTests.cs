using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BackgroundMergeTrainGateTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void BackgroundTrainGate_RestartedDriverLandsFromPersistedReceipt()
    {
        var (repo, kernel, goals) = CreateReadyTrain();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new SequenceAcceptanceVerifier(
                [new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("train restart", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "train-restart-green.trx")])]);
            var firstDriver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var selection = ProjectTrainSelection(firstDriver, goals);
            var started = firstDriver.RunMergeTrain(selection, goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Contains("outcome=inflight", started.Detail, StringComparison.Ordinal);
            var store = new MergeTrainAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            Assert.True(SpinWait.SpinUntil(() =>
                store.ReadPassedReceiptsForGoal(goals[0].Id).Count == 1 &&
                firstDriver.GetActiveCohortGateMemberGoalIds().Count == 0,
                TimeSpan.FromSeconds(15)), "The first driver did not persist its train receipt.");

            var restartedVerifier = new SequenceAcceptanceVerifier([]);
            var restartedDriver = new ConductorDriver(kernel, workspace, restartedVerifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var landed = restartedDriver.RunMergeTrain(
                ProjectTrainSelection(restartedDriver, goals), goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Equal(3, landed.MemberResults.Count);
            Assert.All(goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            Assert.Equal(0, restartedVerifier.RunCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void BackgroundTrainGate_MainAdvanceWhileInFlight_ReselectsAgainstNewMain()
    {
        var (repo, kernel, goals) = CreateReadyTrain();
        var cleanup = CreateIsolatedCleanupContext(repo);
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new BlockingAcceptanceVerifier(gateStarted, gateRelease,
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("train stale main", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "train-stale-main-green.trx")]));
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var firstSelection = ProjectTrainSelection(driver, goals);
            var firstMain = firstSelection.Members[0].MainRevision;
            _ = driver.RunMergeTrain(firstSelection, goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.True(gateStarted.Wait(TimeSpan.FromSeconds(10)), "The initial train gate did not start.");
            File.WriteAllText(Path.Combine(repo, "unrelated-main.txt"), "another landing");
            RunGit(repo, "add", "unrelated-main.txt");
            RunGit(repo, "commit", "-m", "Advance main independently");
            gateRelease.Set();
            var store = new MergeTrainAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            Assert.True(SpinWait.SpinUntil(() =>
                store.ReadPassedReceiptsForGoal(goals[0].Id).Count == 1 &&
                driver.GetActiveCohortGateMemberGoalIds().Count == 0,
                TimeSpan.FromSeconds(15)), "The old-main train receipt was not persisted.");
            var reselection = ProjectTrainSelection(driver, goals);
            Assert.NotEqual(firstMain, reselection.Members[0].MainRevision);
            var retried = driver.RunMergeTrain(reselection, goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Contains("outcome=inflight", retried.Detail, StringComparison.Ordinal);
            Assert.True(SpinWait.SpinUntil(() => verifier.RunCount == 2,
                TimeSpan.FromSeconds(15)), "The train was not gated against the new main.");
            Assert.True(SpinWait.SpinUntil(() =>
                store.ReadPassedReceiptsForGoal(goals[0].Id).Count == 2 &&
                driver.GetActiveCohortGateMemberGoalIds().Count == 0,
                TimeSpan.FromSeconds(15)), "The new-main train gate did not finish.");
            Assert.All(goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
        }
        finally
        {
            gateRelease.Set();
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void TrainNeedingFreshGate_TickReturnsWhileGateBlocked_LaterTickLandsFromReceipt()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        Task? tick = null;
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "First background train member", repo);
            var second = CreateCompletedGoal(kernel, "Second background train member", repo);
            var third = CreateCompletedGoal(kernel, "Third background train member", repo);
            var candidates = new[]
            {
                CreateWorktreeCandidate(repo, first.Id,
                    "tests/Mcg.AgentOrchestrator.Core.Tests/BackgroundFirst.cs", "first"),
                CreateWorktreeCandidate(repo, second.Id,
                    "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/BackgroundSecond.cs", "second"),
                CreateWorktreeCandidate(repo, third.Id,
                    "tests/Mcg.AgentOrchestrator.Dashboard.Tests/BackgroundThird.cs", "third")
            };
            foreach (var (goal, candidate) in new[] { first, second, third }.Zip(candidates))
            {
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1,
                    CriterionEvidenceOwner.Acceptance, "test",
                    CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
            }
            var verifier = new BlockingAcceptanceVerifier(gateStarted, gateRelease,
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("background train", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "background-train-green.trx")]));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var stopPath = Path.Combine(repo, "stop-does-not-exist");
            tick = Task.Run(() => new ConductorBatchLoop().Run(kernel, driver,
                ConductorAutonomyPolicy.Permissive, stopPath, maxIterations: 1));
            Assert.True(tick.Wait(TimeSpan.FromSeconds(30)),
                "The train tick waited for its blocked acceptance gate.");
            Assert.True(gateStarted.Wait(TimeSpan.FromSeconds(10)), "The train gate did not start.");
            Assert.Equal(1, verifier.RunCount);
            Assert.Subset(
                driver.GetActiveCohortGateMemberGoalIds().ToHashSet(StringComparer.Ordinal),
                new[] { first.Id.Value, second.Id.Value, third.Id.Value }.ToHashSet(StringComparer.Ordinal));
            foreach (var goal in new[] { first, second, third })
            {
                Assert.True(driver.TryGetCohortGateHold(goal.Id, out _));
            }

            _ = new ConductorBatchLoop().Run(kernel, driver,
                ConductorAutonomyPolicy.Permissive, stopPath, maxIterations: 1);
            Assert.Equal(1, verifier.RunCount);
            gateRelease.Set();
            var store = new MergeTrainAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            Assert.True(SpinWait.SpinUntil(() =>
                store.ReadPassedReceiptsForGoal(first.Id).Count == 1 &&
                driver.GetActiveCohortGateMemberGoalIds().Count == 0,
                TimeSpan.FromSeconds(15)), "The background train receipt was not persisted.");
            _ = new ConductorBatchLoop().Run(kernel, driver,
                ConductorAutonomyPolicy.Permissive, stopPath, maxIterations: 1);

            Assert.Equal(1, verifier.RunCount);
            Assert.Equal(GoalStatus.Completed, first.Status);
            Assert.Equal(GoalStatus.Completed, second.Status);
            Assert.Equal(GoalStatus.Completed, third.Status);
        }
        finally
        {
            gateRelease.Set();
            _ = tick?.Wait(TimeSpan.FromSeconds(30));
            DeleteDirectory(repo);
        }
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
            CreateWorktreeCandidate(repo, goals[0].Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/RestartFirst.cs", "first"),
            CreateWorktreeCandidate(repo, goals[1].Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RestartSecond.cs", "second"),
            CreateWorktreeCandidate(repo, goals[2].Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/RestartThird.cs", "third")
        };
        foreach (var (goal, candidate) in goals.Zip(candidates))
        {
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
            {
                AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
            });
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1,
                CriterionEvidenceOwner.Acceptance, "test",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
        }
        return (repo, kernel, goals);
    }
}
