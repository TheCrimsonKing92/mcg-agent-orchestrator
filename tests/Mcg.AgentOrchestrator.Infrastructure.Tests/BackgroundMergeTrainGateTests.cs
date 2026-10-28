using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

public sealed class BackgroundMergeTrainGateTests(CohortCandidateTemplate template)
    : AcceptanceCohortWorkflowTests, IClassFixture<CohortCandidateTemplate>
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

    [Fact]
    public void CandidateCopy_HiddenGitFile_RepairsPointersAndPreservesAttributes()
    {
        const string key = "hidden-git-file";
        byte[]? originalPointer = null;
        var copy = template.Copy(key, 1, (repo, ids) =>
        {
            CreateWorktreeCandidate(repo, ids[0], "hidden-candidate.txt", "candidate");
            var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(repo, ids[0]));
            var gitFile = Path.Combine(worktree, ".git");
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(gitFile, File.GetAttributes(gitFile) | FileAttributes.Hidden);
                Assert.True(File.GetAttributes(gitFile).HasFlag(FileAttributes.Hidden));
            }
            originalPointer = File.ReadAllBytes(gitFile);
        });
        try
        {
            var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(copy.Repo, copy.GoalIds[0]));
            var gitFile = Path.Combine(worktree, ".git");
            var administration = Assert.Single(Directory.GetDirectories(Path.Combine(copy.Repo, ".git", "worktrees")));
            Assert.Equal("gitdir: " + administration.Replace('\\', '/') + "\n", File.ReadAllText(gitFile));
            Assert.Equal(gitFile.Replace('\\', '/') + "\n", File.ReadAllText(Path.Combine(administration, "gitdir")));
            if (OperatingSystem.IsWindows())
                Assert.True(File.GetAttributes(gitFile).HasFlag(FileAttributes.Hidden));
            Assert.Equal(copy.Revisions[0], RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
            Assert.Empty(RunGitOutput(worktree, "status", "--porcelain").Trim());
            var sourceWorktree = Assert.IsType<string>(GoalWorktrees.TryResolve(template.TemplatePath(key), copy.GoalIds[0]));
            Assert.NotNull(originalPointer);
            Assert.Equal(originalPointer, File.ReadAllBytes(Path.Combine(sourceWorktree, ".git")));
        }
        finally
        {
            DeleteDirectory(copy.Repo);
        }
    }

    [Fact]
    public void CandidateCopy_RealConstruction_PreservesTopologyAndTrainOutcome()
    {
        var fresh = CreateReducedAcceptanceCohortRepository();
        var (copy, copyKernel, copyGoals) = CreateReadyTrain();
        var (sibling, _, siblingGoals) = CreateReadyTrain();
        try
        {
            // This arm deliberately uses the original builders, independently of the recipe.
            AddAcceptanceManifest(fresh);
            var freshKernel = new AgentOrchestratorKernel();
            var freshGoals = new[]
            {
                CreateCompletedGoal(freshKernel, "First train member", fresh),
                CreateCompletedGoal(freshKernel, "Second train member", fresh),
                CreateCompletedGoal(freshKernel, "Third train member", fresh)
            };
            string[] paths = ["tests/Mcg.AgentOrchestrator.Core.Tests/RestartFirst.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RestartSecond.cs",
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/RestartThird.cs"];
            string[] contents = ["first", "second", "third"];
            var mainTree = RunGitOutput(fresh, "rev-parse", "main^{tree}").Trim();
            Assert.Equal(mainTree, RunGitOutput(copy, "rev-parse", "main^{tree}").Trim());
            for (var index = 0; index < freshGoals.Length; index++)
            {
                var goal = freshGoals[index];
                var candidate = CreateWorktreeCandidate(fresh, goal.Id, paths[index], contents[index]);
                freshKernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                    { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                freshKernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
                var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(copy, copyGoals[index].Id));
                Assert.StartsWith(copy + Path.DirectorySeparatorChar, worktree, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(RunGitOutput(worktree, "status", "--porcelain").Trim());
                Assert.Equal(contents[index], File.ReadAllText(Path.Combine(worktree, paths[index])));
                Assert.Equal(RunGitOutput(fresh, "rev-parse", candidate + "^{tree}").Trim(),
                    RunGitOutput(worktree, "rev-parse", "HEAD^{tree}").Trim());
                Assert.Equal(mainTree, RunGitOutput(worktree, "rev-parse", "HEAD^1^{tree}").Trim());
                Assert.Equal(mainTree, RunGitOutput(fresh, "rev-parse", candidate + "^1^{tree}").Trim());
                Assert.Equal(paths[index], RunGitOutput(worktree, "diff", "--name-only", "main...HEAD").Trim());
                Assert.Equal(RunGitOutput(fresh, "diff", "--name-only", "main..." + candidate).Trim(),
                    RunGitOutput(worktree, "diff", "--name-only", "main...HEAD").Trim());
            }
            var topology = RunGitOutput(copy, "worktree", "list", "--porcelain");
            Assert.DoesNotContain("prunable", topology, StringComparison.Ordinal);
            var worktrees = topology.Split('\n').Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
                .Select(line => Path.GetFullPath(line[9..].Trim())).ToArray();
            Assert.Equal(4, worktrees.Length);
            Assert.Contains(Path.GetFullPath(copy), worktrees, StringComparer.OrdinalIgnoreCase);
            Assert.All(worktrees, path =>
            {
                Assert.True(Path.GetFullPath(path).Equals(Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(copy + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                Assert.Empty(RunGitOutput(path, "status", "--porcelain").Trim());
            });

            var prepared = template.TemplatePath("ready-train");
            var preparedHead = RunGitOutput(prepared, "rev-parse", "main").Trim();
            var preparedState = File.ReadAllBytes(OrchestratorWorkspace.ForDirectory(prepared).SqliteStatePath);
            var freshOutcome = Run(fresh, freshKernel, freshGoals);
            var copyOutcome = Run(copy, copyKernel, copyGoals);
            Assert.Equal(freshOutcome, copyOutcome);
            Assert.Equal(MergeTrainGateOutcome.Passed, copyOutcome.Outcome);
            Assert.Equal(0, copyOutcome.Ejections);
            Assert.Equal(1, copyOutcome.Runs);
            Assert.All(copyGoals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            Assert.Equal(preparedHead, RunGitOutput(prepared, "rev-parse", "main").Trim());
            Assert.Equal(preparedHead, RunGitOutput(sibling, "rev-parse", "main").Trim());
            Assert.Equal(preparedState, File.ReadAllBytes(OrchestratorWorkspace.ForDirectory(prepared).SqliteStatePath));
            for (var index = 0; index < copyGoals.Length; index++)
            {
                var original = RunGitOutput(prepared, "rev-parse", GoalWorktrees.BranchName(copyGoals[index].Id)).Trim();
                var copiedTree = Assert.IsType<string>(GoalWorktrees.TryResolve(sibling, siblingGoals[index].Id));
                File.WriteAllText(Path.Combine(copiedTree, "independent.txt"), "copy only");
                RunGit(copiedTree, "add", "independent.txt");
                RunGit(copiedTree, "commit", "-m", "Mutate copy only");
                Assert.Equal(original, RunGitOutput(prepared, "rev-parse", GoalWorktrees.BranchName(copyGoals[index].Id)).Trim());
                Assert.NotEqual(original, RunGitOutput(sibling, "rev-parse", GoalWorktrees.BranchName(siblingGoals[index].Id)).Trim());
            }
            File.WriteAllText(Path.Combine(copy, "independent.txt"), "main copy only");
            RunGit(copy, "add", "independent.txt");
            RunGit(copy, "commit", "-m", "Mutate copied main");
            Assert.Equal(preparedHead, RunGitOutput(prepared, "rev-parse", "main").Trim());
            Assert.Equal(preparedHead, RunGitOutput(sibling, "rev-parse", "main").Trim());
        }
        finally
        {
            DeleteDirectory(fresh);
            DeleteDirectory(copy);
            DeleteDirectory(sibling);
        }

        static (MergeTrainGateOutcome Outcome, int Ejections, int Runs, string Members) Run(
            string repo, AgentOrchestratorKernel kernel, Goal[] goals)
        {
            var verifier = new SequenceAcceptanceVerifier([new(true, false, 0, null,
                TestResultPaths: [WritePassingTrx(repo, "contract-green.trx")])]);
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: CreateIsolatedCleanupContext(repo).Hooks);
            var result = driver.RunMergeTrain(ProjectTrainSelection(driver, goals), goals, ConductorAutonomyPolicy.Permissive);
            Assert.Equal(3, result.MemberResults.Count);
            Assert.All(goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            return (Assert.IsType<MergeTrainReceipt>(result.Receipt).Outcome, result.Ejections.Count, verifier.RunCount,
                string.Join(",", goals.Select(goal => result.MemberResults[goal.Id.Value].Outcome.GetType().FullName)));
        }
    }

    private (string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals) CreateReadyTrain()
    {
        var copy = template.Copy("ready-train", 3, (preparedRepo, ids) =>
        {
            AddAcceptanceManifest(preparedRepo);
            CreateWorktreeCandidate(preparedRepo, ids[0],
                "tests/Mcg.AgentOrchestrator.Core.Tests/RestartFirst.cs", "first");
            CreateWorktreeCandidate(preparedRepo, ids[1],
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RestartSecond.cs", "second");
            CreateWorktreeCandidate(preparedRepo, ids[2],
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/RestartThird.cs", "third");
        });
        var repo = copy.Repo;
        var kernel = new AgentOrchestratorKernel();
        var goals = new[]
        {
            copy.CreateCompletedGoal(kernel, 0, "First train member"),
            copy.CreateCompletedGoal(kernel, 1, "Second train member"),
            copy.CreateCompletedGoal(kernel, 2, "Third train member")
        };
        var candidates = copy.Revisions;
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
