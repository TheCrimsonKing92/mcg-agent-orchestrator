using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

public sealed class BackgroundMergeTrainGateTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void PlannerConflict_EscalatesWhileAnotherTrainOccupiesAcceptanceWidth()
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
            var excluded = CreateCompletedGoal(kernel, "Verified conflict outside the active train", repo);
            _ = CreateWorktreeCandidate(repo, excluded.Id, conflictPath, "goal change\n");
            File.WriteAllText(fullPath, "main change\n");
            RunGit(repo, "add", conflictPath);
            RunGit(repo, "commit", "-m", "Main conflicting change");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(
                new AcceptanceVerificationResult(true, false, 0, null));
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var starts = new BackgroundGateStartHarness();
            starts.Capture(driver);
            var policy = ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 1 };
            var train = driver.RunMergeTrain(ProjectTrainSelection(driver, goals), goals, policy, runGateInBackground: true);
            Assert.Contains("outcome=inflight", train.Detail, StringComparison.Ordinal);
            Assert.Equal(1, starts.PendingCount);
            Assert.Equal(1, driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);
            Assert.DoesNotContain(excluded.Id.Value, driver.GetActiveCohortGateMemberGoalIds());
            var projection = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(driver.ProjectGateReadyCandidate(excluded, policy));
            Assert.Equal(GateReadyCandidateExclusionReason.MergeConflict, projection.Reason);
            Assert.Equal(new[] { conflictPath }, projection.ConflictPaths);

            var summary = new ConductorBatchLoop().Run(kernel, driver, policy,
                Path.Combine(repo, "stop-does-not-exist"), maxIterations: 1);

            using var store = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json")));
            var escalation = Assert.Single(store.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("goalId").GetString() == excluded.Id.Value);
            var reason = escalation.GetProperty("reason").GetString()!;
            Assert.StartsWith("pre-landing rebase conflict", reason);
            Assert.Contains(conflictPath, reason, StringComparison.Ordinal);
            Assert.Equal(1, summary.Escalated);
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(1, starts.PendingCount);
            Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(excluded, driver.GetFacts(excluded)));
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void FaultedBackgroundTrain_DoesNotEnterCohortFaultDrain()
    {
        var (repo, kernel, goals) = CreateReadyTrain();
        var cleanup = CreateIsolatedCleanupContext(repo);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("train fault", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "train-fault-green.trx")]),
                (run, token) =>
                {
                    if (run == 1)
                    {
                        cancellation.Cancel();
                        token.ThrowIfCancellationRequested();
                    }
                });
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var starts = new BackgroundGateStartHarness();
            starts.Capture(driver);
            var train = ProjectTrainSelection(driver, goals);
            _ = driver.RunMergeTrain(train, goals, ConductorAutonomyPolicy.Permissive,
                cancellation.Token, runGateInBackground: true);
            Assert.Equal(1, starts.StartCount);
            Assert.Equal(0, verifier.RunCount);
            starts.RunPending();
            Assert.Equal(1, verifier.RunCount);
            Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());

            var pair = ConductorAcceptanceCohortSelector.Select(goals.Take(2).Select(goal =>
                new ConductorSpeculativeAcceptanceCandidate(goal.Id,
                    driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive))).ToArray()).Selection;
            Assert.NotNull(pair);
            var cohort = driver.RunAcceptanceCohortForTick(pair, goals.Take(2).ToArray(),
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Null(cohort.Fault);
            Assert.DoesNotContain("gate infrastructure failure", cohort.Run.Detail, StringComparison.Ordinal);
            Assert.Equal(2, starts.StartCount);
            starts.RunPending();
            Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
            var trainFault = driver.RunMergeTrain(train, goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Contains("OperationCanceledException", trainFault.Detail, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

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
            var starts = new BackgroundGateStartHarness();
            starts.Inline(firstDriver);
            var selection = ProjectTrainSelection(firstDriver, goals);
            var started = firstDriver.RunMergeTrain(selection, goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Contains("outcome=inflight", started.Detail, StringComparison.Ordinal);
            var store = new MergeTrainAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            Assert.Equal(1, starts.StartCount);
            Assert.Single(store.ReadPassedReceiptsForGoal(goals[0].Id));
            Assert.Empty(firstDriver.GetActiveCohortGateMemberGoalIds());

            var restartedVerifier = new SequenceAcceptanceVerifier([]);
            var restartedDriver = new ConductorDriver(kernel, workspace, restartedVerifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var landed = restartedDriver.RunMergeTrain(
                ProjectTrainSelection(restartedDriver, goals), goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Equal(3, landed.MemberResults.Count);
            Assert.All(goals, goal => Assert.True(goal.Status == GoalStatus.Completed,
                $"Expected persisted train receipt to land {goal.Id.Value}; status={goal.Status}; detail={landed.Detail}"));
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
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("train stale main", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "train-stale-main-green.trx")]),
                (run, _) =>
                {
                    if (run == 1)
                    {
                        File.WriteAllText(Path.Combine(repo, "unrelated-main.txt"), "another landing");
                        RunGit(repo, "add", "unrelated-main.txt");
                        RunGit(repo, "commit", "-m", "Advance main independently");
                    }
                });
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: cleanup.Hooks);
            var starts = new BackgroundGateStartHarness();
            starts.Capture(driver);
            var firstSelection = ProjectTrainSelection(driver, goals);
            var firstMain = firstSelection.Members[0].MainRevision;
            _ = driver.RunMergeTrain(firstSelection, goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Equal(1, starts.StartCount);
            starts.RunPending();
            var store = new MergeTrainAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            Assert.Single(store.ReadPassedReceiptsForGoal(goals[0].Id));
            Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
            var reselection = ProjectTrainSelection(driver, goals);
            Assert.NotEqual(firstMain, reselection.Members[0].MainRevision);
            var retried = driver.RunMergeTrain(reselection, goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Contains("outcome=inflight", retried.Detail, StringComparison.Ordinal);
            Assert.Equal(2, starts.StartCount);
            starts.RunPending();
            Assert.Equal(2, verifier.RunCount);
            Assert.Equal(2, store.ReadPassedReceiptsForGoal(goals[0].Id).Count);
            Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
            Assert.All(goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void TrainNeedingFreshGate_TickReturnsWhileGateBlocked_LaterTickLandsFromReceipt()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
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
            var verifier = new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("background train", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "background-train-green.trx")]));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                runAcceptanceAttemptsInCurrentProcess: true, cleanupHooks: cleanup.Hooks);
            var starts = new BackgroundGateStartHarness();
            starts.Capture(driver);
            var stopPath = Path.Combine(repo, "stop-does-not-exist");
            BatchTickSummary? heldTick = null;
            _ = new ConductorBatchLoop().Run(kernel, driver,
                ConductorAutonomyPolicy.Permissive, stopPath, maxIterations: 1,
                onTick: summary => heldTick = summary);
            Assert.Equal(1, starts.StartCount);
            Assert.Equal(1, starts.PendingCount);
            Assert.Equal(0, verifier.RunCount);
            var heldProgress = string.Join(" | ", heldTick?.ProgressLines ?? []);
            Assert.Contains("ACCEPTANCE_TRAIN", heldProgress, StringComparison.Ordinal);
            Assert.Contains("outcome=inflight", heldProgress, StringComparison.Ordinal);
            foreach (var goal in new[] { first, second, third })
            {
                Assert.Contains(goal.Id.Value[..8], heldProgress, StringComparison.Ordinal);
                Assert.NotEqual(GoalStatus.Completed, goal.Status);
            }
            Assert.Subset(
                driver.GetActiveCohortGateMemberGoalIds().ToHashSet(StringComparer.Ordinal),
                new[] { first.Id.Value, second.Id.Value, third.Id.Value }.ToHashSet(StringComparer.Ordinal));
            foreach (var goal in new[] { first, second, third })
            {
                Assert.True(driver.TryGetCohortGateHold(goal.Id, out _));
            }

            _ = new ConductorBatchLoop().Run(kernel, driver,
                ConductorAutonomyPolicy.Permissive, stopPath, maxIterations: 1);
            Assert.Equal(1, starts.StartCount);
            Assert.Equal(1, starts.PendingCount);
            Assert.Equal(0, verifier.RunCount);
            starts.RunPending();
            Assert.Equal(1, verifier.RunCount);
            var store = new MergeTrainAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            Assert.Single(store.ReadPassedReceiptsForGoal(first.Id));
            Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
            BatchTickSummary? observedTick = null;
            _ = new ConductorBatchLoop().Run(kernel, driver,
                ConductorAutonomyPolicy.Permissive, stopPath, maxIterations: 1,
                onTick: summary => observedTick = summary);

            Assert.Equal(1, verifier.RunCount);
            var progress = string.Join(" | ", observedTick?.ProgressLines ?? []);
            Assert.True(first.Status == GoalStatus.Completed,
                $"Expected first train member to land; status={first.Status}; progress={progress}");
            Assert.True(second.Status == GoalStatus.Completed,
                $"Expected second train member to land; status={second.Status}; progress={progress}");
            Assert.True(third.Status == GoalStatus.Completed,
                $"Expected third train member to land; status={third.Status}; progress={progress}");
        }
        finally
        {
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
