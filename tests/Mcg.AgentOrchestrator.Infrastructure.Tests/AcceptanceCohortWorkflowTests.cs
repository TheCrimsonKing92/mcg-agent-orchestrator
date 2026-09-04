using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]
public sealed class AcceptanceCohortWorkflowTests : GoalWorktreeTestBase
{
    [Fact]
    public void DeveloperDispatchIntegration_CleanDivergence_MergesMainAndLeavesGoalWorktreeClean()
    {
        var repo = CreateAcceptanceCohortRepository();
        var goal = new Goal(
            new GoalId("11111111111111111111111111111111"),
            "Integrate before Developer dispatch",
            [new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
        try
        {
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktree, "goal.txt"), "goal change");
            RunGit(worktree, "add", "goal.txt");
            RunGit(worktree, "commit", "-m", "Goal change");
            File.WriteAllText(Path.Combine(repo, "main.txt"), "main change");
            RunGit(repo, "add", "main.txt");
            RunGit(repo, "commit", "-m", "Main change");

            var result = ConductorDriver.IntegrateMainBeforeDeveloperDispatch(repo, goal);

            Assert.Equal(DeveloperBranchIntegrationStatus.Integrated, result.Status);
            Assert.Empty(result.ConflictPaths);
            Assert.Equal(string.Empty, RunGitOutput(worktree, "status", "--short").Trim());
            RunGitOutput(
                repo,
                "merge-base",
                "--is-ancestor",
                "main",
                GoalWorktrees.BranchName(goal.Id));
            Assert.Equal(
                $"Integrate main into {GoalWorktrees.BranchName(goal.Id)} before Developer dispatch",
                RunGitOutput(worktree, "log", "-1", "--pretty=%s").Trim());
            Assert.Equal(2, RunGitOutput(worktree, "show", "-s", "--pretty=%P", "HEAD")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void DeveloperDispatchIntegration_ConflictingDivergence_NamesPathsAndRestoresCleanBranch()
    {
        var repo = CreateAcceptanceCohortRepository();
        var goal = new Goal(
            new GoalId("22222222222222222222222222222222"),
            "Escalate conflicting integration",
            [new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
        try
        {
            File.WriteAllText(Path.Combine(repo, "shared.txt"), "base");
            RunGit(repo, "add", "shared.txt");
            RunGit(repo, "commit", "-m", "Shared base");
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktree, "shared.txt"), "goal change");
            RunGit(worktree, "add", "shared.txt");
            RunGit(worktree, "commit", "-m", "Goal change");
            var branchHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            File.WriteAllText(Path.Combine(repo, "shared.txt"), "main change");
            RunGit(repo, "add", "shared.txt");
            RunGit(repo, "commit", "-m", "Main change");

            var result = ConductorDriver.IntegrateMainBeforeDeveloperDispatch(repo, goal);

            Assert.Equal(DeveloperBranchIntegrationStatus.Conflict, result.Status);
            Assert.Equal(["shared.txt"], result.ConflictPaths);
            Assert.Contains("conductor or operator", result.Message, StringComparison.Ordinal);
            Assert.Contains("do not instruct a worker to rebase", result.Message, StringComparison.Ordinal);
            Assert.Equal(branchHead, RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
            Assert.Equal(string.Empty, RunGitOutput(worktree, "status", "--short").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void DeveloperDispatchIntegration_ConflictEscalatesBeforePaidWorkerStart()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Block conflicting Developer dispatch",
            [new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var dispatchStarted = false;
        string? escalation = null;
        var driver = new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => "unused",
            dispatchAndStart: _ =>
            {
                dispatchStarted = true;
                return DispatchStartOutcome.Started();
            },
            startRecordedDispatches: null,
            buildServerShutdown: null,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordTaskNote: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                GoalWorktrees.BranchName(goal.Id),
                "current",
                [],
                null),
            land: (candidate, _) => new LandingResult(
                candidate.Id.Value,
                candidate.Id.Value[..8],
                new LandingDecision.Promote(),
                LandingExecutor.IntegrationBranchName,
                MainAdvanced: true,
                "landed"),
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("clean", null, [], null),
            writeEscalation: (_, _, reason) => escalation = reason,
            classifyChangeRisk: _ => null,
            integrateMainBeforeDeveloperDispatch: _ => new DeveloperBranchIntegrationResult(
                DeveloperBranchIntegrationStatus.Conflict,
                "Developer dispatch blocked: conflict in src/Semantic.cs; conductor or operator must act; do not instruct a worker to rebase.",
                ["src/Semantic.cs"]));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, escalated.State);
        Assert.False(dispatchStarted);
        Assert.Contains("src/Semantic.cs", escalation, StringComparison.Ordinal);
        Assert.Contains("conductor or operator", escalation, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposableWorkspace_MaterializesOrderedCombinedTree_WithoutMutatingGoalBranches()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "config"));
            File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
            RunGit(repo, "add", "config/acceptance-manifest.json");
            RunGit(repo, "commit", "-m", "Add manifest");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/First.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };

            string workspacePath;
            string combinedTree;
            using (var workspace = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings))
            {
                workspacePath = workspace.Path;
                combinedTree = workspace.TreeRevision;
                Assert.True(File.Exists(Path.Combine(workspace.Path, "src", "First.cs")));
                Assert.True(File.Exists(Path.Combine(workspace.Path, "tests", "Second.cs")));
                Assert.StartsWith(
                    "effective-manifest-sha256-",
                    GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                        workspace.Path,
                        bindings.SelectMany(member => member.LandingPaths).ToArray()),
                    StringComparison.Ordinal);
                Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
                Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
                Assert.Equal(combinedTree, RunGitOutput(workspace.Path, "rev-parse", "HEAD^{tree}").Trim());
            }

            Assert.False(Directory.Exists(workspacePath));
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
            Assert.Equal(string.Empty, RunGitOutput(repo, "status", "--short").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void CohortWorkspace_MediumGrove_AllowsLowScratchAndProtectsGit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repo = CreateAcceptanceCohortRepository();
        var labeler = new ModeledIntegrityLabeler();
        var workspaceGrove = Path.Combine(repo, GoalWorktrees.DirectoryName);
        labeler.Seed(
            workspaceGrove,
            new IntegrityLabelState(Exists: true, Low: false, Inheritable: true, Medium: true));
        Assert.True(labeler.WouldDenyLowWrite(
            Path.Combine(workspaceGrove, "c-unprepared", ".scratch", "mcg-wt", "before-preparation")));
        using var labelerScope = AcceptanceWorkspaceIntegrityPreparer.PushIntegrityLabelerForTests(labeler);
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/First.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "tests/Second.cs", "second");

            using var workspace = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                repo,
                main,
                [
                    Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                    Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
                ]);

            var scratchWrite = Path.Combine(workspace.Path, ".scratch", "mcg-wt", Guid.NewGuid().ToString("N"));
            var gitFile = Path.Combine(workspace.Path, ".git");
            var commonDirRaw = RunGitOutput(workspace.Path, "rev-parse", "--git-common-dir").Trim();
            var commonDir = Path.IsPathRooted(commonDirRaw)
                ? Path.GetFullPath(commonDirRaw)
                : Path.GetFullPath(Path.Combine(workspace.Path, commonDirRaw));

            Assert.False(labeler.WouldDenyLowWrite(scratchWrite));
            Assert.True(labeler.WouldDenyLowWrite(gitFile));
            Assert.True(labeler.WouldDenyLowWrite(commonDir));
            var workspaceLowIndex = labeler.FindSetIndex(
                workspace.Path,
                WorkerSandboxPreparer.LowInheritableLevel,
                recursive: true);
            var gitMediumIndex = labeler.FindSetIndex(gitFile, "M", recursive: false);
            var commonDirMediumIndex = labeler.FindSetIndex(commonDir, "(OI)(CI)M", recursive: false);
            Assert.True(workspaceLowIndex >= 0);
            Assert.True(gitMediumIndex > workspaceLowIndex);
            Assert.True(commonDirMediumIndex > workspaceLowIndex);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void CohortWorkspace_AlreadyLowGrove_SkipsRelabeling()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repo = CreateAcceptanceCohortRepository();
        var labeler = new ModeledIntegrityLabeler();
        var workspaceGrove = Path.Combine(repo, GoalWorktrees.DirectoryName);
        labeler.Seed(
            workspaceGrove,
            new IntegrityLabelState(Exists: true, Low: true, Inheritable: true));
        using var labelerScope = AcceptanceWorkspaceIntegrityPreparer.PushIntegrityLabelerForTests(labeler);
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/First.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "tests/Second.cs", "second");

            using var workspace = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                repo,
                main,
                [
                    Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                    Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
                ]);

            Assert.False(labeler.WouldDenyLowWrite(
                Path.Combine(workspace.Path, ".scratch", "mcg-wt", Guid.NewGuid().ToString("N"))));
            Assert.Empty(labeler.SetCalls);
            Assert.Contains(
                labeler.Operations,
                operation => operation.Kind == "query" &&
                    string.Equals(operation.Path, workspaceGrove, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void CohortWorkspace_PreparationFailure_IsTypedAndCleansUp()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repo = CreateAcceptanceCohortRepository();
        var labeler = new ModeledIntegrityLabeler(failLowSet: true);
        labeler.Seed(
            Path.Combine(repo, GoalWorktrees.DirectoryName),
            new IntegrityLabelState(Exists: true, Low: false, Inheritable: true, Medium: true));
        using var labelerScope = AcceptanceWorkspaceIntegrityPreparer.PushIntegrityLabelerForTests(labeler);
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/First.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "tests/Second.cs", "second");

            var failure = Assert.Throws<AcceptanceCohortMaterializationException>(() =>
                GoalWorktrees.CreateAcceptanceCohortWorkspace(
                    repo,
                    main,
                    [
                        Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                        Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
                    ]));

            Assert.Equal(AcceptanceCohortMaterializationFailureKind.WorkspaceFailure, failure.Kind);
            Assert.Contains("Low-integrity gate writes", failure.Message, StringComparison.Ordinal);
            AssertNoCohortWorkspaces(repo);
            Assert.Single(labeler.SetCalls);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void DisposableWorkspaces_UnderNestedGitWorktree_UseShortTokensAndCleanUp()
    {
        var repo = CreateAcceptanceCohortRepository();
        var outerToken = Guid.NewGuid().ToString("N")[..8];
        var outerWorktree = Path.Combine(
            repo,
            GoalWorktrees.DirectoryName,
            $"w-{outerToken}");
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/First.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "tests/Second.cs", "second");
            RunGit(repo, "worktree", "add", "--detach", outerWorktree, main);
            Assert.True(File.Exists(Path.Combine(outerWorktree, ".git")));

            string cohortWorkspace;
            using (var workspace = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                outerWorktree,
                main,
                [
                    Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                    Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
                ]))
            {
                cohortWorkspace = workspace.Path;
                Assert.Matches("^c-[0-9a-f]{12}$", Path.GetFileName(workspace.Path));
                Assert.True(Directory.Exists(workspace.Path));
                Assert.True(File.Exists(Path.Combine(workspace.Path, "src", "First.cs")));
                Assert.True(File.Exists(Path.Combine(workspace.Path, "tests", "Second.cs")));
            }

            Assert.False(Directory.Exists(cohortWorkspace));

            string partitionWorkspace;
            using (var workspace = GoalWorktrees.CreateAcceptancePartitionWorkspace(
                outerWorktree,
                main,
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first")))
            {
                partitionWorkspace = workspace.Path;
                Assert.Matches("^p-[0-9a-f]{12}$", Path.GetFileName(workspace.Path));
                Assert.True(Directory.Exists(workspace.Path));
                Assert.True(File.Exists(Path.Combine(workspace.Path, "src", "First.cs")));
            }

            Assert.False(Directory.Exists(partitionWorkspace));
            AssertNoCohortWorkspaces(outerWorktree);
        }
        finally
        {
            if (Directory.Exists(outerWorktree))
            {
                RunGit(repo, "worktree", "remove", "--force", outerWorktree);
            }
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void DisposableWorkspace_CleanupFailurePersistsTypedDebt_AndCanBeRetried()
    {
        var repo = CreateAcceptanceCohortRepository();
        var previousRemover = AcceptanceCohortWorkspace.WorkspaceRemover;
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/First.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "tests/Second.cs", "second");
            var workspace = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                repo,
                main,
                [
                    Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                    Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
                ]);
            AcceptanceCohortWorkspace.WorkspaceRemover = (_, _) =>
                throw new InvalidOperationException("simulated cohort cleanup failure");

            var failure = Assert.Throws<InvalidOperationException>(workspace.Dispose);

            Assert.Contains("simulated cohort cleanup failure", failure.Message, StringComparison.Ordinal);
            Assert.Contains(GoalWorktrees.ListCleanupDebt(repo), debt =>
                debt.Path.Equals(Path.GetFullPath(workspace.Path), StringComparison.OrdinalIgnoreCase) &&
                debt.Reason == "cohort:worktree-remove-failed");
            AcceptanceCohortWorkspace.WorkspaceRemover = previousRemover;
            workspace.Dispose();
            Assert.False(Directory.Exists(workspace.Path));
        }
        finally
        {
            AcceptanceCohortWorkspace.WorkspaceRemover = previousRemover;
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void FailedMaterialization_CleanupFailurePersistsDebt()
    {
        var repo = CreateAcceptanceCohortRepository();
        var previousRemover = AcceptanceCohortWorkspace.WorkspaceRemover;
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "seed.txt", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "seed.txt", "second");
            AcceptanceCohortWorkspace.WorkspaceRemover = (_, _) =>
                throw new InvalidOperationException("simulated pre-return cleanup failure");

            var failure = Assert.Throws<AcceptanceCohortMaterializationException>(() =>
                GoalWorktrees.CreateAcceptanceCohortWorkspace(
                    repo,
                    main,
                    [
                        Bind(first.GoalId, first.Revision, "seed.txt", "resource:first"),
                        Bind(second.GoalId, second.Revision, "seed.txt", "resource:second")
                    ]));

            Assert.Equal(AcceptanceCohortMaterializationFailureKind.WorkspaceFailure, failure.Kind);
            var debt = Assert.Single(GoalWorktrees.ListCleanupDebt(repo), item =>
                item.Reason == "cohort:worktree-remove-failed");
            Assert.True(Directory.Exists(debt.Path));
            AcceptanceCohortWorkspace.WorkspaceRemover = previousRemover;
            GoalWorktrees.RemoveAcceptanceCohortWorkspace(repo, debt.Path);
        }
        finally
        {
            AcceptanceCohortWorkspace.WorkspaceRemover = previousRemover;
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ConflictingMaterialization_CleansWorkspace_AndLeavesBothBranchesUnchanged()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "seed.txt", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "seed.txt", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "seed.txt", "resource:first"),
                Bind(second.GoalId, second.Revision, "seed.txt", "resource:second")
            };

            var failure = Assert.Throws<AcceptanceCohortMaterializationException>(() =>
                GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings));
            Assert.Equal(AcceptanceCohortMaterializationFailureKind.MergeConflict, failure.Kind);

            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
            AssertNoCohortWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void MergeTrain_ConflictingMemberIsEjected_AndRemainingTrainContinues()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "seed.txt", "first");
            var conflicting = CreateCandidate(repo, "22222222222222222222222222222222", "seed.txt", "second");
            var third = CreateCandidate(repo, "33333333333333333333333333333333", "src/Third.cs", "third");

            using var workspace = GoalWorktrees.CreateMergeTrainWorkspace(
                repo,
                main,
                [
                    TrainBind(first.GoalId, first.Revision, "seed.txt", "resource:first"),
                    TrainBind(conflicting.GoalId, conflicting.Revision, "seed.txt", "resource:second"),
                    TrainBind(third.GoalId, third.Revision, "src/Third.cs", "resource:third")
                ]);

            Assert.Equal([first.GoalId, third.GoalId], workspace.Members.Select(member => member.GoalId));
            var ejection = Assert.Single(workspace.Ejections);
            Assert.Equal(conflicting.GoalId, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RebaseConflict, ejection.Reason);
            Assert.Equal(["seed.txt"], ejection.ConflictPaths);
            Assert.True(File.Exists(Path.Combine(workspace.Path, "src", "Third.cs")));
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(conflicting.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(conflicting.GoalId)}").Trim());
            Assert.Equal(third.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(third.GoalId)}").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void Store_PersistsReceiptAndTwoCoverageRowsAtomically_AndFairnessSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "cohort.db");
            var first = Bind(new GoalId("11111111111111111111111111111111"), new string('b', 40), "src/A.cs", "resource:a");
            var second = Bind(new GoalId("22222222222222222222222222222222"), new string('c', 40), "tests/B.cs", "resource:b");
            var identity = AcceptanceCohortIdentity.Create(
                [first, second],
                new string('a', 40),
                new string('d', 40),
                "manifest-v1");
            var receipt = new AcceptanceCohortReceipt(
                "receipt-1",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.Parse("2026-08-11T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                123,
                [],
                ValidForLanding: true,
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(root, "cohort.trx")]);
            var store = new CohortAcceptanceStore(databasePath);

            var persistedReceipt = store.SaveGateReceipt(receipt);
            Assert.True(persistedReceipt.HasAuthoritativeLandingEvidence);
            Assert.NotEqual(receipt.GateTestResultPaths.Single(), persistedReceipt.GateTestResultPaths.Single());
            Assert.All(persistedReceipt.GateEvidenceArtifacts, artifact =>
            {
                Assert.True(File.Exists(artifact.Path));
                Assert.Equal(64, artifact.Sha256.Length);
            });
            File.WriteAllText(receipt.GateTestResultPaths.Single(), "later stable-slot output");
            Assert.True(persistedReceipt.HasAuthoritativeLandingEvidence);
            var reloadedReceipt = store.SaveGateReceipt(receipt);
            Assert.Equal(receipt.ReceiptId, reloadedReceipt.ReceiptId);
            Assert.Equal(receipt.Identity, reloadedReceipt.Identity);
            Assert.Equal(first, reloadedReceipt.Identity.Members[0]);
            Assert.Equal(second, reloadedReceipt.Identity.Members[1]);
            Assert.Equal(receipt.Outcome, reloadedReceipt.Outcome);
            Assert.Equal(receipt.FailedChecks, reloadedReceipt.FailedChecks);
            Assert.Equal(receipt.ValidForLanding, reloadedReceipt.ValidForLanding);
            Assert.Equal(0, reloadedReceipt.GateExitCode);
            Assert.Equal(persistedReceipt.GateTestResultPaths.ToArray(), reloadedReceipt.GateTestResultPaths.ToArray());
            Assert.Equal(persistedReceipt.GateEvidenceArtifacts.ToArray(), reloadedReceipt.GateEvidenceArtifacts.ToArray());
            store.PrepareLanding(persistedReceipt, new string('e', 40));
            var coverage = store.FinalizeLanding(identity.Value, receipt.ReceiptId);
            store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
            store.RecordOvertake(first.GoalId);
            store.SuppressPair("pair-1", identity.Value);

            Assert.Equal(2, coverage.Count);
            Assert.All(coverage, member =>
            {
                Assert.True(member.Landed);
                Assert.Equal(receipt.ReceiptId, member.ReceiptId);
            });
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT COUNT(*) FROM cohort_receipts), (SELECT COUNT(*) FROM cohort_members);";
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(1, reader.GetInt32(0));
                Assert.Equal(2, reader.GetInt32(1));
            }

            var reopened = new CohortAcceptanceStore(databasePath);
            Assert.Equal(1, reopened.ReadOvertakeCount(first.GoalId));
            Assert.Contains("pair-1", reopened.ReadSuppressedPairs());
            Assert.Equal(identity.Value, reopened.TryReadReceipt(identity.Value)?.Identity.Value);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Store_InfrastructureCauseRoundTripsLegacyUnknown()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-store-cause-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "cohort.db");
            var members = new[]
            {
                Bind(new GoalId("11111111111111111111111111111111"), new string('b', 40), "src/A.cs", "resource:a"),
                Bind(new GoalId("22222222222222222222222222222222"), new string('c', 40), "tests/B.cs", "resource:b")
            };
            var legacyIdentity = AcceptanceCohortIdentity.Create(
                members,
                new string('a', 40),
                new string('d', 40),
                "manifest-v1");
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using (var create = connection.CreateCommand())
                {
                    create.CommandText = """
                        CREATE TABLE cohort_receipts(
                            cohort_id TEXT PRIMARY KEY,
                            receipt_id TEXT NOT NULL UNIQUE,
                            main_revision TEXT NOT NULL,
                            combined_tree_revision TEXT NOT NULL,
                            manifest_identity TEXT NOT NULL,
                            outcome TEXT NOT NULL,
                            attribution TEXT NOT NULL,
                            valid_for_landing INTEGER NOT NULL,
                            completed_at TEXT NOT NULL,
                            gate_elapsed_ms INTEGER NOT NULL,
                            failed_checks_json TEXT NOT NULL,
                            gate_exit_code INTEGER NULL,
                            gate_test_result_paths_json TEXT NOT NULL DEFAULT '[]',
                            gate_evidence_artifacts_json TEXT NOT NULL DEFAULT '[]');
                        CREATE TABLE cohort_members(
                            cohort_id TEXT NOT NULL,
                            member_ordinal INTEGER NOT NULL,
                            goal_id TEXT NOT NULL,
                            branch_revision TEXT NOT NULL,
                            candidate_revision TEXT NOT NULL,
                            landing_paths_json TEXT NOT NULL,
                            resource_keys_json TEXT NOT NULL,
                            risk_tier TEXT NOT NULL,
                            promotion_disposition TEXT NOT NULL,
                            merge_status TEXT NOT NULL,
                            merge_reason TEXT NOT NULL,
                            landed INTEGER NOT NULL DEFAULT 0,
                            PRIMARY KEY(cohort_id, member_ordinal));
                        """;
                    create.ExecuteNonQuery();
                }
                using (var receipt = connection.CreateCommand())
                {
                    receipt.CommandText = """
                        INSERT INTO cohort_receipts(
                            cohort_id, receipt_id, main_revision, combined_tree_revision, manifest_identity,
                            outcome, attribution, valid_for_landing, completed_at, gate_elapsed_ms,
                            failed_checks_json, gate_exit_code, gate_test_result_paths_json, gate_evidence_artifacts_json)
                        VALUES ($cohort, 'legacy-receipt', $main, $tree, 'manifest-v1', 'InfrastructureFailure',
                            'NotApplicable', 0, $completed, 50, '["legacy infrastructure check"]', 2, '[]', '[]');
                        """;
                    receipt.Parameters.AddWithValue("$cohort", legacyIdentity.Value);
                    receipt.Parameters.AddWithValue("$main", legacyIdentity.ObservedMainRevision);
                    receipt.Parameters.AddWithValue("$tree", legacyIdentity.CombinedTreeRevision);
                    receipt.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
                    receipt.ExecuteNonQuery();
                }
                for (var index = 0; index < members.Length; index++)
                {
                    var member = members[index];
                    using var insertMember = connection.CreateCommand();
                    insertMember.CommandText = """
                        INSERT INTO cohort_members(
                            cohort_id, member_ordinal, goal_id, branch_revision, candidate_revision,
                            landing_paths_json, resource_keys_json, risk_tier, promotion_disposition,
                            merge_status, merge_reason, landed)
                        VALUES ($cohort, $ordinal, $goal, $branch, $candidate, $paths, $resources,
                            $risk, $promotion, $mergeStatus, $mergeReason, 0);
                        """;
                    insertMember.Parameters.AddWithValue("$cohort", legacyIdentity.Value);
                    insertMember.Parameters.AddWithValue("$ordinal", index);
                    insertMember.Parameters.AddWithValue("$goal", member.GoalId.Value);
                    insertMember.Parameters.AddWithValue("$branch", member.BranchRevision);
                    insertMember.Parameters.AddWithValue("$candidate", member.CandidateRevision);
                    insertMember.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(member.LandingPaths));
                    insertMember.Parameters.AddWithValue("$resources", JsonSerializer.Serialize(member.ResourceKeys));
                    insertMember.Parameters.AddWithValue("$risk", member.ChangeRiskTier.ToString());
                    insertMember.Parameters.AddWithValue("$promotion", member.AutoPromotionDisposition.ToString());
                    insertMember.Parameters.AddWithValue("$mergeStatus", member.MergeStatus);
                    insertMember.Parameters.AddWithValue("$mergeReason", member.MergeReason);
                    insertMember.ExecuteNonQuery();
                }
            }

            var store = new CohortAcceptanceStore(databasePath);
            var legacy = store.TryReadReceipt(legacyIdentity.Value);
            Assert.NotNull(legacy);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.LegacyUnknown, legacy.InfrastructureReasonCode);
            Assert.Null(legacy.InfrastructureDetail);

            var currentIdentity = AcceptanceCohortIdentity.Create(
                members,
                legacyIdentity.ObservedMainRevision,
                new string('e', 40),
                legacyIdentity.ManifestIdentity);
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "current-receipt",
                currentIdentity,
                AcceptanceCohortGateOutcome.InfrastructureFailure,
                DateTimeOffset.UtcNow,
                75,
                ["infrastructure tests"],
                GateExitCode: 2,
                GateTestResultPaths: [],
                InfrastructureReasonCode: AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent,
                InfrastructureDetail: "The TRX result set was incomplete."));

            var current = new CohortAcceptanceStore(databasePath).TryReadReceipt(currentIdentity.Value);
            Assert.NotNull(current);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent, current.InfrastructureReasonCode);
            Assert.Equal("The TRX result set was incomplete.", current.InfrastructureDetail);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Store_RejectsPassingReceiptWithoutAuthoritativeEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-store-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var identity = AcceptanceCohortIdentity.Create(
                [
                    Bind(new GoalId("11111111111111111111111111111111"), new string('b', 40), "src/A.cs", "resource:a"),
                    Bind(new GoalId("22222222222222222222222222222222"), new string('c', 40), "tests/B.cs", "resource:b")
                ],
                new string('a', 40),
                new string('d', 40),
                "manifest-v1");
            var store = new CohortAcceptanceStore(Path.Combine(root, "cohort.db"));
            var validTrx = WritePassingTrx(root, "valid.trx");
            var missingFile = Path.GetFullPath(Path.Combine(root, "missing.trx"));
            var malformedTrx = Path.GetFullPath(Path.Combine(root, "malformed.trx"));
            var incoherentTrx = Path.GetFullPath(Path.Combine(root, "incoherent.trx"));
            File.WriteAllText(malformedTrx, "not xml");
            File.WriteAllText(incoherentTrx, """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results><UnitTestResult testId="1" testName="Passes" outcome="Passed" /></Results>
                  <ResultSummary outcome="Completed"><Counters total="2" executed="2" passed="2" failed="0" /></ResultSummary>
                </TestRun>
                """);

            var missingExit = new AcceptanceCohortReceipt(
                "missing-exit", identity, AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow, 1, [], null, [validTrx],
                ValidForLanding: true);
            var missingTrx = missingExit with
            {
                ReceiptId = "missing-trx",
                GateExitCode = 0,
                GateTestResultPaths = []
            };
            var unnormalizedTrx = missingTrx with
            {
                ReceiptId = "unnormalized-trx",
                GateTestResultPaths = ["relative.trx"]
            };
            var absentTrx = missingExit with
            {
                ReceiptId = "absent-trx",
                GateExitCode = 0,
                GateTestResultPaths = [missingFile]
            };
            var malformed = absentTrx with
            {
                ReceiptId = "malformed-trx",
                GateTestResultPaths = [malformedTrx]
            };
            var incoherent = absentTrx with
            {
                ReceiptId = "incoherent-trx",
                GateTestResultPaths = [incoherentTrx]
            };

            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(missingExit));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(missingTrx));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(unnormalizedTrx));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(absentTrx));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(malformed));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(incoherent));
            Assert.Null(store.TryReadReceipt(identity.Value));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ExactTestedCombinedCommit_LandsOnce_AndBothGoalsShareReceiptCoverage()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "config"));
            File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
            RunGit(repo, "add", "config/acceptance-manifest.json");
            RunGit(repo, "commit", "-m", "Add manifest");
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second cohort member", repo);
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/First.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));

            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                main,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-shared",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "receipt-shared.trx")],
                ValidForLanding: true));

            var result = LandingExecutor.ExecuteCohort(
                kernel,
                [firstGoal, secondGoal],
                workspace,
                receipt,
                integration.CommitRevision,
                store,
                ConductorAutonomyPolicy.Conservative);

            Assert.True(result.MainAdvanced);
            Assert.Equal(integration.CommitRevision, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.Equal(integration.TreeRevision, RunGitOutput(repo, "rev-parse", "main^{tree}").Trim());
            Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<AcceptanceCohortCoverage>>(result.Coverage).Count);
            Assert.All(result.Coverage!, coverage =>
            {
                Assert.True(coverage.Landed);
                Assert.Equal(receipt.ReceiptId, coverage.ReceiptId);
            });
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ExactTestedMergeTrainCommit_LandsThreeGoalsWithPerGoalCoverage()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Third train member", repo);
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/TrainFirst.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/TrainSecond.cs", "second");
            var third = CreateCandidate(repo, thirdGoal.Id.Value, "docs/train-third.md", "third");
            var bindings = new[]
            {
                TrainBind(first.GoalId, first.Revision, "src/TrainFirst.cs", "resource:first"),
                TrainBind(second.GoalId, second.Revision, "tests/TrainSecond.cs", "resource:second"),
                TrainBind(third.GoalId, third.Revision, "docs/train-third.md", "resource:third")
            };
            var orchestratorWorkspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new MergeTrainAcceptanceStore(
                Path.Combine(orchestratorWorkspace.OrchestratorDirectory, "merge-train-acceptance.db"));

            using var integration = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings);
            var identity = MergeTrainIdentity.Create(
                integration.Members,
                main,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var receipt = store.SaveGateReceipt(new MergeTrainReceipt(
                "receipt-train-shared",
                identity,
                MergeTrainGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "receipt-train-shared.trx")],
                ValidForLanding: true));

            var result = LandingExecutor.ExecuteMergeTrain(
                kernel,
                [firstGoal, secondGoal, thirdGoal],
                orchestratorWorkspace,
                receipt,
                integration.CommitRevision,
                store,
                ConductorAutonomyPolicy.Conservative);

            Assert.True(result.MainAdvanced);
            Assert.Equal(integration.TreeRevision, RunGitOutput(repo, "rev-parse", "main^{tree}").Trim());
            Assert.Equal(3, Assert.IsAssignableFrom<IReadOnlyList<AcceptanceCohortCoverage>>(result.Coverage).Count);
            Assert.All(result.Coverage!, coverage => Assert.True(coverage.Landed));
            Assert.All([firstGoal, secondGoal, thirdGoal], goal =>
                Assert.Contains(
                    GoalOperationJournal.Read(repo, goal.Id).Entries,
                    operation => operation.Operation == "conductor:land" && operation.Status == GoalOperationStatus.Completed));
            store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PreparedMergeTrainLanding_RecoversUntilPostLandingEffectsComplete()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/RecoverTrainFirst.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "tests/RecoverTrainSecond.cs", "second");
            var third = CreateCandidate(repo, "33333333333333333333333333333333", "src/RecoverTrainThird.cs", "third");
            var bindings = new[]
            {
                TrainBind(first.GoalId, first.Revision, "src/RecoverTrainFirst.cs", "resource:first"),
                TrainBind(second.GoalId, second.Revision, "tests/RecoverTrainSecond.cs", "resource:second"),
                TrainBind(third.GoalId, third.Revision, "src/RecoverTrainThird.cs", "resource:third")
            };
            var store = new MergeTrainAcceptanceStore(
                Path.Combine(repo, ".orchestrator", "merge-train-acceptance.db"));
            using var integration = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings);
            var identity = MergeTrainIdentity.Create(
                integration.Members,
                main,
                integration.TreeRevision,
                "manifest-v1");
            var receipt = store.SaveGateReceipt(new MergeTrainReceipt(
                "recoverable-train-receipt",
                identity,
                MergeTrainGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "recoverable-train.trx")],
                ValidForLanding: true));
            store.PrepareLanding(receipt, integration.CommitRevision, main);

            Assert.Empty(store.RecoverPreparedLandings(repo));
            RunGit(repo, "update-ref", "refs/heads/main", integration.CommitRevision, main);
            var recovery = Assert.Single(store.RecoverPreparedLandings(repo));
            Assert.Equal(identity.Value, recovery.Receipt.Identity.Value);
            Assert.Equal(3, recovery.Coverage.Count);
            Assert.Single(store.RecoverPreparedLandings(repo));

            store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
            Assert.Empty(store.RecoverPreparedLandings(repo));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PostGateMainMovement_LandsNeitherMember_AndCreatesNoCoverage()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "config"));
            File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
            RunGit(repo, "add", "config/acceptance-manifest.json");
            RunGit(repo, "commit", "-m", "Add manifest");
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First invalidated member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second invalidated member", repo);
            var boundMain = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/First.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };
            var orchestratorWorkspace = OrchestratorWorkspace.ForDirectory(repo);
            var databasePath = Path.Combine(orchestratorWorkspace.OrchestratorDirectory, "cohort-acceptance.db");
            var store = new CohortAcceptanceStore(databasePath);
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, boundMain, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                boundMain,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-invalidated",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "receipt-invalidated.trx")],
                ValidForLanding: true));

            File.WriteAllText(Path.Combine(repo, "main-moved.txt"), "new main");
            RunGit(repo, "add", "main-moved.txt");
            RunGit(repo, "commit", "-m", "Move main after gate");

            var result = LandingExecutor.ExecuteCohort(
                kernel,
                [firstGoal, secondGoal],
                orchestratorWorkspace,
                receipt,
                integration.CommitRevision,
                store,
                ConductorAutonomyPolicy.Conservative);

            Assert.False(result.MainAdvanced);
            Assert.Contains("main changed", result.Message, StringComparison.OrdinalIgnoreCase);
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_members WHERE landed=1;";
            Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void MainCasFailure_RestoresIntegrationRef_AndLandsNeitherMember()
    {
        var repo = CreateAcceptanceCohortRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First CAS member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second CAS member", repo);
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/First.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            var integrationBefore = RunGitOutput(
                repo,
                "rev-parse",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim();
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                main,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-cas", identity, AcceptanceCohortGateOutcome.Passed, DateTimeOffset.UtcNow,
                100, [], 0, [WritePassingTrx(repo, "receipt-cas.trx")], ValidForLanding: true));
            var moved = false;
            LandingExecutor.GitRunner = (workingDirectory, args) =>
            {
                var isOldMerge = args.SequenceEqual(["merge", "--ff-only", integration.CommitRevision]);
                var isMainCas = args.Length == 4 && args[0] == "update-ref" && args[1] == "refs/heads/main";
                if (!moved && (isOldMerge || isMainCas))
                {
                    moved = true;
                    var movement = GitCli.Run(workingDirectory, "reset", "--hard", first.Revision);
                    Assert.Equal(0, movement.ExitCode);
                }
                return GitCli.Run(workingDirectory, args);
            };

            var result = LandingExecutor.ExecuteCohort(
                kernel, [firstGoal, secondGoal], workspace, receipt, integration.CommitRevision, store,
                ConductorAutonomyPolicy.Conservative);

            Assert.True(moved);
            Assert.Equal(AcceptanceCohortLandingOutcome.StateInvalidated, result.Outcome);
            Assert.False(result.MainAdvanced);
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.Equal(
                integrationBefore,
                RunGitOutput(repo, "rev-parse", $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim());
            using var connection = new SqliteConnection($"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_members WHERE landed=1;";
            Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void DeterministicRed_PersistsPartitionsBeforeInteractionClassification()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-attribution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "cohort.db");
            var first = Bind(new GoalId("11111111111111111111111111111111"), new string('b', 40), "src/A.cs", "resource:a");
            var second = Bind(new GoalId("22222222222222222222222222222222"), new string('c', 40), "tests/B.cs", "resource:b");
            var identity = AcceptanceCohortIdentity.Create(
                [first, second], new string('a', 40), new string('d', 40), "manifest-v1");
            var store = new CohortAcceptanceStore(databasePath);
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "combined-red",
                identity,
                AcceptanceCohortGateOutcome.Failed,
                DateTimeOffset.UtcNow,
                100,
                ["combined-check"],
                GateExitCode: 1,
                GateTestResultPaths: [Path.GetFullPath("combined-red.trx")]));
            var partitions = new[]
            {
                new AcceptanceCohortPartitionReceipt(
                    "partition-a", first.GoalId, 0, first.CandidateRevision, identity.ObservedMainRevision,
                    new string('e', 40), identity.ManifestIdentity, AcceptanceCohortGateOutcome.Passed, 10, ["a.trx"]),
                new AcceptanceCohortPartitionReceipt(
                    "partition-b", second.GoalId, 1, second.CandidateRevision, identity.ObservedMainRevision,
                    new string('f', 40), identity.ManifestIdentity, AcceptanceCohortGateOutcome.Passed, 11, ["b.trx"])
            };

            var receipt = store.SaveAttribution(
                identity.Value,
                AcceptanceCohortAttributionOutcome.InteractionOnly,
                partitions,
                "pair-interaction",
                innocentGoalId: null);
            var secondIdentity = AcceptanceCohortIdentity.Create(
                [first, second], new string('a', 40), new string('e', 40), "manifest-v1");
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "combined-red-second",
                secondIdentity,
                AcceptanceCohortGateOutcome.Failed,
                DateTimeOffset.UtcNow,
                100,
                ["combined-check"],
                GateExitCode: 1,
                GateTestResultPaths: [Path.GetFullPath("combined-red-second.trx")]));
            _ = store.SaveAttribution(
                secondIdentity.Value,
                AcceptanceCohortAttributionOutcome.InteractionOnly,
                partitions,
                "pair-interaction-second",
                innocentGoalId: null);

            Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, receipt.Attribution);
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_partition_receipts WHERE cohort_id=$cohort AND outcome='Passed';";
            command.Parameters.AddWithValue("$cohort", identity.Value);
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM cohort_partition_receipts;";
            Assert.Equal(4, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ProductionGate_InvalidResultPath_PersistsStructuredInfrastructureFailure()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First invalid-path cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second invalid-path cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new FakeAcceptanceVerifier(
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: false,
                    ExitCode: 1,
                    OutputTail: "invalid result path",
                    Checks: [new AcceptanceCheckResult("combined", false, 1, "invalid result path")],
                    TestResultPaths: ["bad\0path.trx"]),
                exception: null);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var mainBefore = RunGitOutput(repo, "rev-parse", "main").Trim();
            ConductorAcceptanceCohortRunResult? result = null;

            var exception = Record.Exception(() => result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive));

            Assert.Null(exception);
            var receipt = Assert.IsType<AcceptanceCohortReceipt>(result?.Receipt);
            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, receipt.Outcome);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.ResultPathInvalid, receipt.InfrastructureReasonCode);
            Assert.Empty(receipt.GateTestResultPaths);
            Assert.All(result!.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Equal(mainBefore, RunGitOutput(repo, "rev-parse", "main").Trim());
            AssertNoCohortWorkspaces(repo);

            var persisted = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"))
                .TryReadReceipt(receipt.Identity.Value);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.ResultPathInvalid, persisted?.InfrastructureReasonCode);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionGate_SkippedInvalidResultPath_PersistsStructuredInfrastructureFailure() =>
        AssertEarlyInfrastructureResultWithInvalidPathPersistsStructuredFailure(
            skipped: true,
            exitCode: 0,
            outputTail: "skipped with invalid result path");

    [Fact]
    public void ProductionGate_MissingExitCodeInvalidResultPath_PersistsStructuredInfrastructureFailure() =>
        AssertEarlyInfrastructureResultWithInvalidPathPersistsStructuredFailure(
            skipped: false,
            exitCode: null,
            outputTail: "missing exit code with invalid result path");

    private static void AssertEarlyInfrastructureResultWithInvalidPathPersistsStructuredFailure(
        bool skipped,
        int? exitCode,
        string outputTail)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First early invalid-path cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second early invalid-path cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new FakeAcceptanceVerifier(
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: skipped,
                    ExitCode: exitCode,
                    OutputTail: outputTail,
                    Checks: [new AcceptanceCheckResult("combined", false, exitCode, outputTail)],
                    TestResultPaths: ["bad\0path.trx"]),
                exception: null);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var mainBefore = RunGitOutput(repo, "rev-parse", "main").Trim();
            ConductorAcceptanceCohortRunResult? result = null;

            var exception = Record.Exception(() => result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive));

            Assert.Null(exception);
            var receipt = Assert.IsType<AcceptanceCohortReceipt>(result?.Receipt);
            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, receipt.Outcome);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.ResultPathInvalid, receipt.InfrastructureReasonCode);
            Assert.Empty(receipt.GateTestResultPaths);
            Assert.All(result!.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Equal(mainBefore, RunGitOutput(repo, "rev-parse", "main").Trim());
            AssertNoCohortWorkspaces(repo);

            var persisted = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"))
                .TryReadReceipt(receipt.Identity.Value);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.ResultPathInvalid, persisted?.InfrastructureReasonCode);
            Assert.Empty(persisted!.GateTestResultPaths);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionRed_RunsBothPartitions_AndClassifiesInteraction()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            AddSourceSizeAuthority(
                repo,
                ("src/Mcg.AgentOrchestrator.Infrastructure/First.cs", 1),
                ("tests/Second.cs", 1));
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First RED cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second RED cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var failingTrx = WriteFailingTrx(repo, "combined-red.trx");
            var firstPassingTrx = WritePassingTrx(repo, "first-green.trx");
            var secondPassingTrx = WritePassingTrx(repo, "second-green.trx");
            var verifier = new SequenceAcceptanceVerifier(
            [
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: false,
                    ExitCode: 1,
                    OutputTail: "combined failure",
                    Checks: [new AcceptanceCheckResult("combined", false, 1, "combined failure")],
                    TestResultPaths: [failingTrx]),
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("first partition", true, 0, null)],
                    TestResultPaths: [firstPassingTrx]),
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("second partition", true, 0, null)],
                    TestResultPaths: [secondPassingTrx])
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var mainBefore = RunGitOutput(repo, "rev-parse", "main").Trim();

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(3, verifier.RunCount);
            Assert.Equal<GoalId?>([null, firstGoal.Id, secondGoal.Id], verifier.GoalIds);
            Assert.Equal(AcceptanceCohortGateOutcome.Failed, result.Receipt?.Outcome);
            Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, result.Receipt?.Attribution);
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Equal(mainBefore, RunGitOutput(repo, "rev-parse", "main").Trim());
            AssertNoCohortWorkspaces(repo);

            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            Assert.Contains(
                ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
                store.ReadSuppressedPairs());
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_partition_receipts WHERE cohort_id=$cohort;";
            command.Parameters.AddWithValue("$cohort", result.Receipt!.Identity.Value);
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionRed_OneFailedMember_RequeuesGreenPeer()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "Failed cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Innocent cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new SequenceAcceptanceVerifier(
            [
                FailedVerification(repo, "combined-red-one-member.trx", "combined"),
                FailedVerification(repo, "first-member-red.trx", "first partition"),
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("second partition", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "second-member-green.trx")])
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var mainBefore = RunGitOutput(repo, "rev-parse", "main").Trim();

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(3, verifier.RunCount);
            Assert.Equal<GoalId?>([null, firstGoal.Id, secondGoal.Id], verifier.GoalIds);
            Assert.Equal(AcceptanceCohortAttributionOutcome.FirstMemberFailed, result.Receipt?.Attribution);
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Equal(mainBefore, RunGitOutput(repo, "rev-parse", "main").Trim());
            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            Assert.Equal(1, store.ReadOvertakeCount(secondGoal.Id));
            Assert.Equal(0, store.ReadOvertakeCount(firstGoal.Id));
            store.EnsureAttributionSideEffects(
                result.Receipt!.Identity.Value,
                result.Receipt.Attribution,
                ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
                secondGoal.Id);
            Assert.Equal(1, store.ReadOvertakeCount(secondGoal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PreparedLanding_RecoversCoverageOnlyAfterExactCommitIsReachableFromMain()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var firstGoal = new GoalId("11111111111111111111111111111111");
            var secondGoal = new GoalId("22222222222222222222222222222222");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Value, "src/A.cs", "a");
            var second = CreateCandidate(repo, secondGoal.Value, "tests/B.cs", "b");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/A.cs", "resource:a"),
                Bind(second.GoalId, second.Revision, "tests/B.cs", "resource:b")
            };
            var databasePath = Path.Combine(repo, ".orchestrator", "cohort-acceptance.db");
            var store = new CohortAcceptanceStore(databasePath);
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings, main, integration.TreeRevision, "manifest-v1");
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "recoverable-receipt",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "recoverable-receipt.trx")],
                ValidForLanding: true));
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            store.PrepareLanding(receipt, integration.CommitRevision, main);
            RunGit(
                repo,
                "update-ref",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}",
                integration.CommitRevision,
                main);

            Assert.Empty(store.RecoverPreparedLandings(repo));
            Assert.Equal(
                main,
                RunGitOutput(repo, "rev-parse", $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim());
            store.PrepareLanding(receipt, integration.CommitRevision, main);
            RunGit(
                repo,
                "update-ref",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}",
                integration.CommitRevision,
                main);
            RunGit(repo, "merge", "--ff-only", integration.CommitRevision);
            var recovery = Assert.Single(store.RecoverPreparedLandings(repo));
            Assert.Equal(identity.Value, recovery.Receipt.Identity.Value);
            store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
            Assert.Empty(store.RecoverPreparedLandings(repo));

            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_members WHERE cohort_id=$cohort AND landed=1;";
            command.Parameters.AddWithValue("$cohort", identity.Value);
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ConstructorRecovery_DefersEntireCohortUntilReplacementLeaseIsReleased()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First recovered member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second recovered member", repo);
            var firstRevision = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            var secondRevision = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var bindings = new[]
            {
                Bind(firstGoal.Id, firstRevision, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "resource:first"),
                Bind(secondGoal.Id, secondRevision, "tests/Second.cs", "resource:second")
            };
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                main,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "constructor-recovery-receipt",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "constructor-recovery.trx")],
                ValidForLanding: true));
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            store.PrepareLanding(receipt, integration.CommitRevision, main);
            RunGit(
                repo,
                "update-ref",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}",
                integration.CommitRevision,
                main);
            RunGit(repo, "update-ref", "refs/heads/main", integration.CommitRevision, main);

            var replacementProtectedGoal = new[] { firstGoal, secondGoal }
                .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
                .Last();
            using (var replacementLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
                       .TryAcquireAcceptanceLease(
                           replacementProtectedGoal.Id.Value,
                           $"goal-replace:test:{Guid.NewGuid():N}",
                           TimeSpan.FromMinutes(30)))
            {
                Assert.NotNull(replacementLease);
                _ = new ConductorDriver(
                    kernel,
                    workspace,
                    FakeAcceptanceVerifier.Throws(new InvalidOperationException("Blocked recovery must not run a gate.")),
                    AgentCatalog.Default().Agents,
                    WorkerProfileCatalog.Default());

                Assert.All(new[] { firstGoal, secondGoal }, goal =>
                    Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(
                        GoalOperationJournal.Read(repo, goal.Id))));
                var blockedLifecycleText = Directory.Exists(workspace.GoalLifecycleEventsDirectory)
                    ? string.Join(
                        '\n',
                        Directory.EnumerateFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
                            .Select(File.ReadAllText))
                    : string.Empty;
                Assert.DoesNotContain("\"eventType\":\"GoalLanded\"", blockedLifecycleText, StringComparison.Ordinal);
                Assert.Single(store.RecoverPreparedLandings(repo));
            }

            var recoveredDriver = new ConductorDriver(
                kernel,
                workspace,
                FakeAcceptanceVerifier.Throws(new InvalidOperationException("Recovery must not run a gate.")),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default());
            var recoveredReceipts = new List<ConductorLandingReceipt>();
            recoveredDriver.SuccessfulLandingSink = recoveredReceipts.Add;

            Assert.Equal(2, recoveredReceipts.Count);
            Assert.Equal(
                new[] { firstGoal.Id.Value, secondGoal.Id.Value }.Order(StringComparer.Ordinal),
                recoveredReceipts.Select(item => item.GoalId).Order(StringComparer.Ordinal));
            Assert.All(new[] { firstGoal, secondGoal }, goal =>
                Assert.True(GoalOperationJournal.HasCompletedLandingEvidence(
                    GoalOperationJournal.Read(repo, goal.Id))));
            var lifecycleText = string.Join(
                '\n',
                Directory.EnumerateFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
                    .Select(File.ReadAllText));
            Assert.Equal(2, lifecycleText.Split("\"eventType\":\"GoalLanded\"", StringSplitOptions.None).Length - 1);
            Assert.Empty(store.RecoverPreparedLandings(repo));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionMergeTrain_OneGateAttemptLandsThreeMembers()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var previousIsolatedRoot = Environment.GetEnvironmentVariable(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                Path.Combine(repo, ".dotnet-test-root"));
            AddAcceptanceManifest(repo);
            AddSourceSizeAuthority(repo, ("tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs", 1));
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First production train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second production train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Third production train member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs",
                "first");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs",
                "second");
            _ = CreateWorktreeCandidate(
                repo,
                thirdGoal.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/TrainThird.cs",
                "third");
            var verifier = new SequenceAcceptanceVerifier(
            [
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("merge train", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "train-green.trx")])
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectTrainSelection(driver, firstGoal, secondGoal, thirdGoal);
            var landings = new List<ConductorLandingReceipt>();
            driver.SuccessfulLandingSink = landings.Add;

            var result = driver.RunMergeTrain(
                selection,
                [firstGoal, secondGoal, thirdGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Equal(1, verifier.RunCount);
            Assert.Equal(3, result.MemberResults.Count);
            Assert.Equal(3, landings.Count);
            Assert.Equal(MergeTrainGateOutcome.Passed, result.Receipt?.Outcome);
            Assert.Contains("attempts=1 landings=3", result.Detail, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Core.Tests",
                "TrainFirst.cs")));
            Assert.True(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Infrastructure.Tests",
                "TrainSecond.cs")));
            Assert.True(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Dashboard.Tests",
                "TrainThird.cs")));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                previousIsolatedRoot);
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionMergeTrain_RedNewestDropsThenShorterTrainLands()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var previousIsolatedRoot = Environment.GetEnvironmentVariable(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                Path.Combine(repo, ".dotnet-test-root"));
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First RED train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second RED train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Newest RED train member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/RedTrainFirst.cs",
                "first");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RedTrainSecond.cs",
                "second");
            _ = CreateWorktreeCandidate(
                repo,
                thirdGoal.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/RedTrainNewest.cs",
                "newest");
            var verifier = new SequenceAcceptanceVerifier(
            [
                FailedVerification(repo, "train-three-red.trx", "three-member train"),
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("shorter train", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "train-two-green.trx")]),
                FailedVerification(repo, "train-newest-solo-red.trx", "newest solo")
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectTrainSelection(driver, firstGoal, secondGoal, thirdGoal);

            var result = driver.RunMergeTrain(
                selection,
                [firstGoal, secondGoal, thirdGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Equal(2, result.MemberResults.Count);
            Assert.DoesNotContain(thirdGoal.Id.Value, result.MemberResults.Keys);
            var dropped = Assert.Single(result.Ejections, item => item.Reason == MergeTrainEjectionReason.RedNewestMember);
            Assert.Equal(thirdGoal.Id, dropped.GoalId);
            Assert.True(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Core.Tests",
                "RedTrainFirst.cs")));
            Assert.True(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Infrastructure.Tests",
                "RedTrainSecond.cs")));
            Assert.False(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Dashboard.Tests",
                "RedTrainNewest.cs")));
            var soloProjection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                driver.ProjectGateReadyCandidate(thirdGoal, ConductorAutonomyPolicy.Permissive));
            Assert.Equal(
                ["tests/Mcg.AgentOrchestrator.Dashboard.Tests/RedTrainNewest.cs"],
                soloProjection.Projection.LandingPaths);
            var soloResult = driver.RunParallelLandingAcceptance(
                ConductorParallelAcceptanceCandidate.Create(
                    thirdGoal,
                    slotIndex: 0,
                    soloProjection.Projection.LandingPaths),
                ConductorAutonomyPolicy.Permissive,
                stableSlotLease: null,
                CancellationToken.None);
            Assert.NotNull(soloResult.Acceptance);
            Assert.False(soloResult.Acceptance!.Passed);
            Assert.Equal(3, verifier.RunCount);
            Assert.Equal<GoalId?>([firstGoal.Id, firstGoal.Id, thirdGoal.Id], verifier.GoalIds);
            Assert.Equal(3, verifier.ChangedFiles[0].Count);
            Assert.Equal(2, verifier.ChangedFiles[1].Count);
            Assert.Equal(
                ["tests/Mcg.AgentOrchestrator.Dashboard.Tests/RedTrainNewest.cs"],
                verifier.ChangedFiles[2]);
            Assert.False(File.Exists(Path.Combine(
                repo,
                "tests",
                "Mcg.AgentOrchestrator.Dashboard.Tests",
                "RedTrainNewest.cs")));
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM merge_train_receipts;";
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                previousIsolatedRoot);
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionMergeTrain_StaleMaterializationReturnsTypedFallback()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First stale train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second stale train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Third stale train member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/StaleTrainFirst.cs",
                "first");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/StaleTrainSecond.cs",
                "second");
            _ = CreateWorktreeCandidate(
                repo,
                thirdGoal.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/StaleTrainThird.cs",
                "third");
            var verifier = new FakeAcceptanceVerifier(
                result: null,
                exception: new InvalidOperationException("A stale train must not invoke the gate."));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectTrainSelection(driver, firstGoal, secondGoal, thirdGoal);
            File.WriteAllText(Path.Combine(repo, "move-main.txt"), "new main");
            RunGit(repo, "add", "move-main.txt");
            RunGit(repo, "commit", "-m", "Move main before train materialization");

            var result = driver.RunMergeTrain(
                selection,
                [firstGoal, secondGoal, thirdGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Null(result.Receipt);
            Assert.Empty(result.MemberResults);
            Assert.Equal(3, result.Ejections.Count);
            Assert.All(result.Ejections, item => Assert.Equal(MergeTrainEjectionReason.StaleBinding, item.Reason));
            Assert.Contains("materialization fallback: StaleBinding", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionMergeTrain_ManifestFailureReturnsTypedFallback()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First manifest-failure train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second manifest-failure train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Third manifest-failure train member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/ManifestTrainFirst.cs",
                "first");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ManifestTrainSecond.cs",
                "second");
            _ = CreateWorktreeCandidate(
                repo,
                thirdGoal.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/ManifestTrainThird.cs",
                "third");
            var verifier = new ManifestThrowingAcceptanceVerifier(
                new InvalidOperationException("acceptance manifest changed during train materialization"));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectTrainSelection(driver, firstGoal, secondGoal, thirdGoal);

            var result = driver.RunMergeTrain(
                selection,
                [firstGoal, secondGoal, thirdGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Null(result.Receipt);
            Assert.Empty(result.MemberResults);
            Assert.Empty(result.Ejections);
            Assert.Contains("manifest fallback", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void CohortProgressEvents_AreStructuredForBothMembersWithoutUnknownGoal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = new GoalId("11111111111111111111111111111111");
            var second = new GoalId("22222222222222222222222222222222");
            var bindings = new[]
            {
                Bind(first, new string('a', 40), "src/First.cs", "production:first"),
                Bind(second, new string('b', 40), "src/Second.cs", "production:second")
            };
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                new string('c', 40),
                new string('d', 40),
                "manifest-v1");
            var logPath = Path.Combine(root, "conduct-events.jsonl");
            var writer = new ConductEventLogWriter(logPath);
            var now = DateTimeOffset.UtcNow;

            ConductorDriver.AppendCohortGateProgressEvents(
                writer,
                identity,
                bindings,
                new AcceptanceGateProgress(
                    GoalId: null,
                    Phase: "lane",
                    CurrentTarget: "focused-cohort",
                    SlotIndex: 0,
                    ProcessId: Environment.ProcessId,
                    ChildProcessId: 1234,
                    StartedAt: now.AddSeconds(-1),
                    LastObservedAt: now,
                    LastProgressAt: now,
                    Elapsed: TimeSpan.FromSeconds(1),
                    OutputBytes: 42,
                    HeartbeatPath: Path.Combine(root, "heartbeat.json")));

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();
            Assert.Equal(2, records.Length);
            Assert.Contains(records, record => record.GoalId == first.Value[..8]);
            Assert.Contains(records, record => record.GoalId == second.Value[..8]);
            Assert.All(records, record =>
            {
                Assert.Equal("gate-progress", record.EventKind);
                Assert.Contains($"cohort={identity.Value[..18]}", record.Detail, StringComparison.Ordinal);
                Assert.Contains($"members={first.Value[..8]},{second.Value[..8]}", record.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("goal=unknown", record.Detail, StringComparison.Ordinal);
            });
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void BackgroundCohortGate_MainAdvanceDoesNotLaunchDuplicateForSameMembers()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-member-pair-{Guid.NewGuid():N}.trx");
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        var previousIsolatedRoot = Environment.GetEnvironmentVariable(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                Path.Combine(repo, ".dotnet-test-root"));
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First stable member-pair member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second stable member-pair member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new BlockingAcceptanceVerifier(
                gateStarted,
                gateRelease,
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("shared-member-pair-gate", true, 0, null)],
                    TestResultPaths: [trx]));
            var driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var originalPairFingerprint = ConductorAcceptanceCohortSelector.PairFingerprint(
                selection.Members[0],
                selection.Members[1]);

            var first = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);

            Assert.Contains("outcome=inflight", first.Detail, StringComparison.Ordinal);
            Assert.True(gateStarted.Wait(TimeSpan.FromSeconds(10)), "The controlled cohort verifier did not start.");
            Assert.Equal(1, verifier.RunCount);
            Assert.Single(Directory.EnumerateDirectories(
                Path.Combine(repo, GoalWorktrees.DirectoryName),
                "c-*"));

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main advanced while cohort gate remained held");
            RunGit(repo, "add", "main-advanced.txt");
            RunGit(repo, "commit", "-m", "Advance main during cohort gate");
            var revisedSelection = ProjectSelection(driver, firstGoal, secondGoal);
            Assert.NotEqual(selection.Members[0].MainRevision, revisedSelection.Members[0].MainRevision);
            Assert.NotEqual(
                originalPairFingerprint,
                ConductorAcceptanceCohortSelector.PairFingerprint(
                    revisedSelection.Members[0],
                    revisedSelection.Members[1]));

            var held = driver.RunAcceptanceCohort(
                revisedSelection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);

            Assert.Contains("outcome=inflight", held.Detail, StringComparison.Ordinal);
            Assert.Contains($"fingerprint={originalPairFingerprint}", held.Detail, StringComparison.Ordinal);
            Assert.Equal(1, verifier.RunCount);
            Assert.Single(Directory.EnumerateDirectories(
                Path.Combine(repo, GoalWorktrees.DirectoryName),
                "c-*"));
        }
        finally
        {
            gateRelease.Set();
            _ = SpinWait.SpinUntil(
                () => !Directory.Exists(repo) ||
                      !Directory.EnumerateDirectories(
                          Path.Combine(repo, GoalWorktrees.DirectoryName),
                          "c-*").Any(),
                TimeSpan.FromSeconds(10));
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                previousIsolatedRoot);
            if (File.Exists(trx)) File.Delete(trx);
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionBatch_LongCohortGateDoesNotBlockTicksOrOperatorIntents_AndReconcilesLater()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-production-{Guid.NewGuid():N}.trx");
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        var previousIsolatedRoot = Environment.GetEnvironmentVariable(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                Path.Combine(repo, ".dotnet-test-root"));
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First production cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second production cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new BlockingAcceptanceVerifier(
                gateStarted,
                gateRelease,
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("shared-production-gate", true, 0, null)],
                    TestResultPaths: [trx]));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default());
            var fairnessStore = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            fairnessStore.RecordOvertake(firstGoal.Id);
            fairnessStore.RecordOvertake(secondGoal.Id);
            var stopPath = Path.Combine(repo, "stop-does-not-exist");
            var conductLogPath = Path.Combine(workspace.OrchestratorDirectory, "logs", "cohort-nonblocking.jsonl");
            var intentStore = new SqliteOperatorIntentStore(
                Path.Combine(workspace.OrchestratorDirectory, "operator-intents.db"),
                Path.Combine(workspace.OrchestratorDirectory, "logs"));
            const string intentId = "cohort-running-progress";
            var ticks = new List<BatchTickSummary>();

            var heldSummary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(intentStore),
                conductEventLogWriter: new ConductEventLogWriter(conductLogPath)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 3,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ => false,
                onTick: tick =>
                {
                    ticks.Add(tick);
                    if (ticks.Count != 1)
                    {
                        return;
                    }

                    Assert.True(gateStarted.Wait(TimeSpan.FromSeconds(10)), "The controlled cohort verifier did not start.");
                    intentStore.EnqueueAsync(new OperatorIntentRecord(
                        intentId,
                        "cohort-running-progress-key",
                        OperatorIntentVerbs.Progress,
                        firstGoal.Id.Value,
                        firstGoal.Tasks.Single().Id.Value,
                        JsonSerializer.Serialize(
                            new ProgressOperatorIntentPayload(WorkTaskStatus.Completed, "operator intent applied while cohort gate remained held"),
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        [],
                        "operator",
                        "test",
                        "local-process",
                        DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
                },
                persistGoalTick: (_, _) => { });

            Assert.Equal(3, ticks.Count);
            Assert.Equal(1, verifier.RunCount);
            Assert.True(verifier.StableSlotLeaseObserved);
            Assert.True(Directory.Exists(verifier.WorktreePath));
            Assert.Equal(OperatorIntentStatus.Applied, intentStore.GetAsync(intentId).GetAwaiter().GetResult()!.Status);
            Assert.Equal(GoalStatus.Verified, firstGoal.Status);
            Assert.Equal(GoalStatus.Verified, secondGoal.Status);
            Assert.Empty(driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts(
                [firstGoal.Id.Value, secondGoal.Id.Value]));
            Assert.Equal(0, heldSummary.Advanced);
            var conductEvents = File.ReadAllLines(conductLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();
            var gateProgressEvents = File.ReadAllLines(workspace.ConductEventsLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "gate-progress")
                .ToArray();
            Assert.Equal(2, gateProgressEvents.Length);
            Assert.Contains(gateProgressEvents, record => record.GoalId == firstGoal.Id.Value[..8]);
            Assert.Contains(gateProgressEvents, record => record.GoalId == secondGoal.Id.Value[..8]);
            Assert.All(gateProgressEvents, record =>
            {
                Assert.Contains($"members={firstGoal.Id.Value[..8]},{secondGoal.Id.Value[..8]}", record.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("goal=unknown", record.Detail, StringComparison.Ordinal);
            });
            var cohortEvents = conductEvents
                .Where(record => record.EventKind == "acceptance-cohort")
                .ToArray();
            Assert.StartsWith("ACCEPTANCE_COHORT_ENTRY", cohortEvents[0].Detail, StringComparison.Ordinal);
            Assert.Contains(cohortEvents, record => record.Detail.StartsWith("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal));
            Assert.Contains(cohortEvents, record => record.Detail.StartsWith("ACCEPTANCE_COHORT_EXIT", StringComparison.Ordinal));
            var inFlightTicks = cohortEvents
                .Where(record => record.Detail.StartsWith("ACCEPTANCE_COHORT_INFLIGHT", StringComparison.Ordinal))
                .Select(record => record.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Single(token => token.StartsWith("tick=", StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.True(inFlightTicks.Length >= 2, "Expected in-flight cohort records from at least two completed ticks.");

            gateRelease.Set();
            Assert.True(SpinWait.SpinUntil(ReceiptPersisted, TimeSpan.FromSeconds(10)), "The background cohort receipt was not persisted.");
            var landedSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 1);

            Assert.Equal(0, fairnessStore.ReadOvertakeCount(firstGoal.Id));
            Assert.Equal(0, fairnessStore.ReadOvertakeCount(secondGoal.Id));
            Assert.Equal(2, landedSummary.Advanced);
            Assert.True(File.Exists(Path.Combine(
                repo,
                "src",
                "Mcg.AgentOrchestrator.Infrastructure",
                "First.cs")));
            Assert.True(File.Exists(Path.Combine(repo, "tests", "Second.cs")));
            AssertNoCohortWorkspaces(repo);
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*), MIN(gate_exit_code), MAX(json_array_length(gate_test_result_paths_json))
                FROM cohort_receipts;
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));

            bool ReceiptPersisted()
            {
                using var receiptConnection = new SqliteConnection(
                    $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")}");
                receiptConnection.Open();
                using var receiptCommand = receiptConnection.CreateCommand();
                receiptCommand.CommandText = "SELECT COUNT(*) FROM cohort_receipts;";
                return Convert.ToInt32(receiptCommand.ExecuteScalar()) == 1;
            }
        }
        finally
        {
            gateRelease.Set();
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                previousIsolatedRoot);
            if (File.Exists(trx)) File.Delete(trx);
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionBatch_TwoLiveCohortRootsFillSharedAcceptanceCapacity()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-shared-capacity-{Guid.NewGuid():N}.trx");
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        var previousIsolatedRoot = Environment.GetEnvironmentVariable(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        ConductorDriver? driver = null;
        try
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                Path.Combine(repo, ".dotnet-test-root"));
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 5)
                .Select(index => CreateCompletedGoal(kernel, $"Shared capacity member {index}", repo))
                .ToArray();
            var paths = new[]
            {
                "src/Mcg.AgentOrchestrator.Infrastructure/CapacityFirst.cs",
                "tests/CapacitySecond.cs",
                "src/Mcg.AgentOrchestrator.Core/CapacityThird.cs",
                "tests/CapacityFourth.cs",
                "src/Mcg.AgentOrchestrator.App/CapacityWaiter.cs"
            };
            for (var index = 0; index < goals.Length; index++)
            {
                _ = CreateWorktreeCandidate(repo, goals[index].Id, paths[index], $"capacity-{index}");
            }

            var verifier = new BlockingAcceptanceVerifier(
                gateStarted,
                gateRelease,
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("shared-capacity-gate", true, 0, null)],
                    TestResultPaths: [trx]));
            driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default());
            var first = ProjectSelection(driver, goals[0], goals[1]);
            var second = ProjectSelection(driver, goals[2], goals[3]);

            _ = driver.RunAcceptanceCohort(
                first,
                [goals[0], goals[1]],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);
            _ = driver.RunAcceptanceCohort(
                second,
                [goals[2], goals[3]],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);
            Assert.True(
                SpinWait.SpinUntil(() => verifier.RunCount == 2, TimeSpan.FromSeconds(10)),
                "Both controlled cohort roots did not enter the verifier.");

            var capacity = driver.GetActiveAcceptanceCohortCapacity();
            Assert.Equal(2, capacity.ActiveRootCount);
            BatchTickSummary? tick = null;
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                Path.Combine(repo, "stop-does-not-exist"),
                maxIterations: 1,
                onTick: current => tick = current);

            Assert.Empty(driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts([goals[4].Id.Value]));
            Assert.Equal(GoalStatus.Verified, goals[4].Status);
            Assert.Contains(tick!.ProgressLines!, line =>
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal));
        }
        finally
        {
            gateRelease.Set();
            if (driver is not null)
            {
                _ = SpinWait.SpinUntil(
                    () => driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount == 0,
                    TimeSpan.FromSeconds(10));
            }
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                previousIsolatedRoot);
            if (File.Exists(trx)) File.Delete(trx);
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionBatch_OrdinaryParallelAcceptanceStillStartsPromptlyAndReconcilesLater()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var attemptRoot = Path.Combine(repo, ".orchestrator", "ordinary-negative-control");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Ordinary acceptance negative control", repo);
            ConductorParallelAcceptanceOwnedProcessLaunch? launch = null;
            const int ownerProcessId = 8123;
            var landed = false;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: pending =>
                {
                    launch = pending;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(ownerProcessId);
                },
                acquireStableSlotLease: (_, _) => null);
            var driver = new ConductorDriver(
                getFacts: _ => landed
                    ? new GoalLifecycleFacts(
                        WorkspaceExists: true,
                        IsMerged: true,
                        IsRecorded: true,
                        IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningPaidWorkerCount: () => 0,
                createWorkspace: _ => repo,
                dispatchAndStart: _ => DispatchStartOutcome.Started(),
                startRecordedDispatches: null,
                buildServerShutdown: null,
                runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                runAdvisorySemanticAcceptance: null,
                retryTask: null,
                recordTaskNote: null,
                recordCriterionRetryFeedback: null,
                clearCriterionRetryFeedback: null,
                rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                    GoalWorktrees.BranchName(goal.Id),
                    "current",
                    [],
                    null),
                land: (candidate, _) =>
                {
                    landed = true;
                    return new LandingResult(
                        candidate.Id.Value,
                        candidate.Id.Value[..8],
                        new LandingDecision.Promote(),
                        LandingExecutor.IntegrationBranchName,
                        MainAdvanced: true,
                        "landed");
                },
                afterSuccessfulLanding: null,
                record: _ => { },
                cleanup: _ => new GoalWorktreeRemoveResult("clean", null, [], null),
                writeEscalation: (_, _, _) => { },
                classifyChangeRisk: _ => ChangeRiskTier.Behavior,
                getLandingFileScopes: _ => ["src/Ordinary.cs"],
                runAcceptanceVerificationWithSlot: (_, _) =>
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                parallelAcceptanceAttemptCoordinator: coordinator);
            var stopPath = Path.Combine(repo, "stop-does-not-exist");
            BatchTickSummary? startTick = null;

            var started = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 1,
                onTick: tick => startTick = tick);

            Assert.Equal(0, started.Advanced);
            Assert.Equal(1, started.Held);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.NotNull(launch);
            Assert.False(driver.TryGetCohortGateHold(goal.Id, out _));
            Assert.Contains(startTick!.ProgressLines!, line =>
                line.Contains("ACCEPTANCE", StringComparison.Ordinal) &&
                line.Contains("result=started", StringComparison.Ordinal));
            launch.ExecuteInCurrentProcess(ownerProcessId);

            BatchTickSummary? reconcileTick = null;
            var completed = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 1,
                onTick: tick => reconcileTick = tick);

            Assert.Equal(1, completed.Advanced);
            Assert.True(landed);
            Assert.Contains(reconcileTick!.ProgressLines!, line =>
                line.Contains("ACCEPTANCE", StringComparison.Ordinal) &&
                line.Contains("result=passed", StringComparison.Ordinal));
            Assert.Empty(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void CohortStableSlotLease_RefusesToExceedTrustedHostGateCap()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-slot-cap-{Guid.NewGuid():N}");
        var previousIsolatedRoot = Environment.GetEnvironmentVariable(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                root);
            using var first = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero)).Lease;
            using var second = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(1, TimeSpan.Zero)).Lease;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                runInline: true);

            Assert.Throws<DotnetBuildSlotsBusyException>(() =>
                coordinator.AcquireCohortStableSlotLease("cohort-v2-slot-cap", timeout: TimeSpan.Zero));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                previousIsolatedRoot);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void StaleMaterialization_PersistsOutcomeAndUsesOrdinaryFallback()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First materialization member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second materialization member", repo);
            var firstRevision = CreateWorktreeCandidate(repo, firstGoal.Id, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            var secondRevision = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new FakeAcceptanceVerifier(
                result: null,
                exception: new InvalidOperationException("The gate must not run after materialization fails."));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            store.RecordOvertake(firstGoal.Id);
            var gateAdmissions = 0;

            File.WriteAllText(Path.Combine(repo, "main-moved.txt"), "new main");
            RunGit(repo, "add", "main-moved.txt");
            RunGit(repo, "commit", "-m", "Move main before cohort materialization");

            var result = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive,
                onGateAdmitted: () =>
                {
                    gateAdmissions++;
                    driver.RecordCohortAdmissionFairness([firstGoal, secondGoal], selection);
                });

            Assert.Null(result.Receipt);
            Assert.Empty(result.MemberResults);
            Assert.Contains("outcome=materialization-failure", result.Detail, StringComparison.Ordinal);
            Assert.Contains("fallback=ordinary", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(0, gateAdmissions);
            Assert.Equal(1, store.ReadOvertakeCount(firstGoal.Id));
            AssertNoCohortWorkspaces(repo);
            Assert.Equal(firstRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(firstGoal.Id)}").Trim());
            Assert.Equal(secondRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(secondGoal.Id)}").Trim());

            using var connection = new SqliteConnection($"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT outcome FROM cohort_materialization_failures;";
            Assert.Equal("StaleBinding", Assert.IsType<string>(command.ExecuteScalar()));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void RetryableLandingHold_PreservesPassingReceipt_AndLaterRelandsIdenticalState()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First retryable landing member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second retryable landing member", repo);
            var firstRevision = CreateWorktreeCandidate(repo, firstGoal.Id, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            var secondRevision = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new FakeAcceptanceVerifier(
                result: null,
                exception: new InvalidOperationException("An identical-state landing retry must reuse its exact gate receipt."));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var bindings = selection.BindMembers();
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            AcceptanceCohortIdentity identity;
            using (var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                       repo,
                       selection.Members[0].MainRevision,
                       bindings))
            {
                identity = AcceptanceCohortIdentity.Create(
                    bindings,
                    selection.Members[0].MainRevision,
                    integration.TreeRevision,
                    GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                        integration.Path,
                        bindings.SelectMany(member => member.LandingPaths).ToArray()));
            }
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "retryable-hold-receipt",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "retryable-hold-receipt.trx")],
                ValidForLanding: true));

            driver.LandingMutationBlocker = () => "operator-controlled landing pause";
            var held = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Passed, held.Receipt?.Outcome);
            Assert.True(held.Receipt?.ValidForLanding);
            Assert.All(held.MemberResults.Values, result => Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome));
            Assert.Equal(identity.ObservedMainRevision, RunGitOutput(repo, "rev-parse", "main").Trim());

            driver.LandingMutationBlocker = null;
            var landed = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Passed, landed.Receipt?.Outcome);
            Assert.True(landed.Receipt?.ValidForLanding);
            Assert.All(landed.MemberResults.Values, result => Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome));
            Assert.Equal(identity.CombinedTreeRevision, RunGitOutput(repo, "rev-parse", "main^{tree}").Trim());
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(firstRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(firstGoal.Id)}").Trim());
            Assert.Equal(secondRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(secondGoal.Id)}").Trim());
            AssertNoCohortWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void CachedPassWithoutEvidence_InvalidatesBeforeLanding()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First cached receipt member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second cached receipt member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = FakeAcceptanceVerifier.Throws(
                new InvalidOperationException("An invalid cached pass must not be reused or rerun in place."));
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var bindings = selection.BindMembers();
            AcceptanceCohortIdentity identity;
            using (var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                       repo, selection.Members[0].MainRevision, bindings))
            {
                identity = AcceptanceCohortIdentity.Create(
                    bindings,
                    selection.Members[0].MainRevision,
                    integration.TreeRevision,
                    GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                        integration.Path,
                        bindings.SelectMany(member => member.LandingPaths).ToArray()));
            }
            var databasePath = Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db");
            var store = new CohortAcceptanceStore(databasePath);
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "legacy-cached-pass",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "legacy-cached-pass.trx")],
                ValidForLanding: true));
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE cohort_receipts SET gate_exit_code=NULL, gate_test_result_paths_json='[]';";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Passed, result.Receipt?.Outcome);
            Assert.Equal(AcceptanceCohortInvalidationReason.EvidenceUnavailable, result.Receipt?.Invalidation?.Reason);
            Assert.False(result.Receipt?.HasAuthoritativeLandingEvidence);
            Assert.Equal(2, result.MemberResults.Count);
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Contains("lacks successful exit", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(identity.ObservedMainRevision, RunGitOutput(repo, "rev-parse", "main").Trim());
            using var verifyConnection = new SqliteConnection($"Data Source={databasePath}");
            verifyConnection.Open();
            using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = """
                SELECT outcome, valid_for_landing,
                       (SELECT COUNT(*) FROM cohort_invalidations WHERE cohort_id=$cohort)
                FROM cohort_receipts WHERE cohort_id=$cohort;
                """;
            verifyCommand.Parameters.AddWithValue("$cohort", identity.Value);
            using var verifyReader = verifyCommand.ExecuteReader();
            Assert.True(verifyReader.Read());
            Assert.Equal("Passed", verifyReader.GetString(0));
            Assert.Equal(1, verifyReader.GetInt32(1));
            Assert.Equal(1, verifyReader.GetInt32(2));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PostGateBranchMove_HoldsMembersForFreshProjection()
    {
        var repo = CreateAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-branch-movement-{Guid.NewGuid():N}.trx");
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First moving member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second moving member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var firstWorktree = Assert.IsType<string>(GoalWorktrees.TryResolve(repo, firstGoal.Id));
            var verifier = new FakeAcceptanceVerifier(
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    TestResultPaths: [trx]),
                onRun: () =>
                {
                    File.WriteAllText(Path.Combine(firstWorktree, "post-gate.txt"), "branch moved");
                    RunGit(firstWorktree, "add", "post-gate.txt");
                    RunGit(firstWorktree, "commit", "-m", "Move branch during cohort gate");
                });
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Passed, result.Receipt?.Outcome);
            Assert.Equal(AcceptanceCohortInvalidationReason.GoalBranchChanged, result.Receipt?.Invalidation?.Reason);
            Assert.Equal(2, result.MemberResults.Count);
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Contains("fresh Ready projection", result.Detail, StringComparison.Ordinal);
            Assert.Equal(1, verifier.RunCount);
            AssertNoCohortWorkspaces(repo);
        }
        finally
        {
            if (File.Exists(trx)) File.Delete(trx);
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PersistedInfrastructureFailure_UsesBoundedHoldThenOrdinaryFallback()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First infrastructure member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second infrastructure member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = FakeAcceptanceVerifier.Throws(
                new InvalidOperationException("A persisted infrastructure receipt must not rerun the cohort gate."));
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var bindings = selection.BindMembers();
            AcceptanceCohortIdentity identity;
            using (var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                       repo, selection.Members[0].MainRevision, bindings))
            {
                identity = AcceptanceCohortIdentity.Create(
                    bindings,
                    selection.Members[0].MainRevision,
                    integration.TreeRevision,
                    GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                        integration.Path,
                        bindings.SelectMany(member => member.LandingPaths).ToArray()));
            }
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "infrastructure-receipt",
                identity,
                AcceptanceCohortGateOutcome.InfrastructureFailure,
                DateTimeOffset.UtcNow,
                100,
                ["infrastructure:runner-loss"],
                GateExitCode: null,
                GateTestResultPaths: [],
                InfrastructureReasonCode: AcceptanceCohortInfrastructureReasonCodes.ExitCodeMissing,
                InfrastructureDetail: "The persisted gate did not report an exit code."));

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, result.Receipt?.Outcome);
            Assert.Equal(AcceptanceCohortInvalidationReason.InfrastructureRetryExhausted, result.Receipt?.Invalidation?.Reason);
            Assert.Empty(result.MemberResults);
            Assert.Contains("fallback=ordinary", result.Detail, StringComparison.Ordinal);
            Assert.Contains(
                ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
                store.ReadSuppressedPairs());
            Assert.Equal(0, verifier.RunCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionCohort_RatchetBreach_SkipsGateAndPreservesPair()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            AddSourceSizeAuthority(
                repo,
                ("src/Mcg.AgentOrchestrator.Infrastructure/First.cs", 2),
                ("tests/Second.cs", 4));
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First ratchet member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second ratchet member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "one\ntwo\nthree");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Second.cs",
                "one\ntwo\nthree\nfour\nfive\nsix");
            var verifier = new SequenceAcceptanceVerifier(
            [
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: false,
                    ExitCode: 1,
                    OutputTail: "source size breach",
                    Checks: [new AcceptanceCheckResult("source size ratchet preflight", false, 1, "breach")])
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var gateAdmitted = false;

            var result = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive,
                onGateAdmitted: () => gateAdmitted = true);

            Assert.False(gateAdmitted, "The cohort stable-slot lease must not be acquired for a ratchet breach.");
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(AcceptanceCohortGateOutcome.Failed, result.Receipt?.Outcome);
            Assert.Equal(AcceptanceCohortAttributionOutcome.NotApplicable, result.Receipt?.Attribution);
            Assert.Empty(result.MemberResults);
            Assert.Contains("fallback=ordinary", result.Detail, StringComparison.Ordinal);
            var failedChecks = string.Join(Environment.NewLine, result.Receipt!.FailedChecks);
            Assert.Contains("First.cs has 3 lines", failedChecks, StringComparison.Ordinal);
            Assert.Contains("recorded ceiling of 2", failedChecks, StringComparison.Ordinal);
            Assert.Contains("Second.cs has 6 lines", failedChecks, StringComparison.Ordinal);
            Assert.Contains("recorded ceiling of 4", failedChecks, StringComparison.Ordinal);
            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            Assert.Empty(store.ReadSuppressedPairs());
            var eligibility = ConductorAcceptanceCohortSelector.Select(
            [
                new ConductorSpeculativeAcceptanceCandidate(
                    firstGoal.Id,
                    new GateReadyCandidateProjectionResult.Ready(selection.Members[0])),
                new ConductorSpeculativeAcceptanceCandidate(
                    secondGoal.Id,
                    new GateReadyCandidateProjectionResult.Ready(selection.Members[1]))
            ], suppressedPairFingerprints: store.ReadSuppressedPairs());
            Assert.NotNull(eligibility.Selection);

            BatchTickSummary? ordinaryTick = null;
            _ = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                Path.Combine(repo, "stop-does-not-exist"),
                maxIterations: 1,
                onTick: tick => ordinaryTick = tick);

            foreach (var goal in new[] { firstGoal, secondGoal })
            {
                Assert.Contains(ordinaryTick!.ProgressLines!, line =>
                    line.StartsWith($"ACCEPTANCE goal={goal.Id.Value[..8]}", StringComparison.Ordinal) &&
                    line.Contains("result=started", StringComparison.Ordinal));
                Assert.DoesNotContain(ordinaryTick.ProgressLines!, line =>
                    line.Contains($"goal={goal.Id.Value[..8]}", StringComparison.Ordinal) &&
                    line.Contains("result=held", StringComparison.Ordinal) &&
                    line.Contains("cohort", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ProductionMergeTrain_RatchetBreach_SkipsGateLease()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            AddSourceSizeAuthority(repo, ("tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs", 2));
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First ratchet train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second ratchet train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Third ratchet train member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs",
                "one\ntwo\nthree");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs",
                "second");
            _ = CreateWorktreeCandidate(
                repo,
                thirdGoal.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/TrainThird.cs",
                "third");
            var verifier = new SequenceAcceptanceVerifier(
            [
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: false,
                    ExitCode: 1,
                    OutputTail: "source size breach",
                    Checks: [new AcceptanceCheckResult("source size ratchet preflight", false, 1, "breach")])
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());
            var selection = ProjectTrainSelection(driver, firstGoal, secondGoal, thirdGoal);
            var gateAdmitted = false;

            var result = driver.RunMergeTrain(
                selection,
                [firstGoal, secondGoal, thirdGoal],
                ConductorAutonomyPolicy.Permissive,
                onGateAdmitted: () => gateAdmitted = true);

            Assert.False(gateAdmitted, "The merge-train stable-slot lease must not be acquired for a ratchet breach.");
            Assert.Equal(0, verifier.RunCount);
            Assert.Contains("outcome=Failed", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void RatchetBreachWithoutTrx_IsContentFailure()
    {
        var result = new AcceptanceVerificationResult(
            Passed: false,
            Skipped: false,
            ExitCode: 1,
            OutputTail: "source size breach",
            Checks: [new AcceptanceCheckResult("source size ratchet preflight", false, 1, "breach")]);

        Assert.Equal(
            AcceptanceCohortGateOutcome.Failed,
            ConductorDriver.ClassifyCohortVerification(result));
    }

    private static void AddAcceptanceManifest(string repo)
    {
        Directory.CreateDirectory(Path.Combine(repo, "config"));
        File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
        RunGit(repo, "add", "config/acceptance-manifest.json");
        RunGit(repo, "commit", "-m", "Add manifest");
    }

    private static void AddSourceSizeAuthority(
        string repo,
        params (string Path, int Ceiling)[] ceilings)
    {
        var authorityPath = Path.Combine(
            repo,
            SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
        File.WriteAllLines(
            authorityPath,
            ceilings.Select(ceiling =>
                $"new SourceSizeCeiling(\"{ceiling.Path}\", {ceiling.Ceiling})"));
        RunGit(repo, "add", SourceSizeRatchet.SourcePath);
        RunGit(repo, "commit", "-m", "Add source size authority");
    }

    private static string ValidPassingTrx() => """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="1" testName="Passes" outcome="Passed" />
          </Results>
          <ResultSummary outcome="Completed">
            <Counters total="1" executed="1" passed="1" failed="0" />
          </ResultSummary>
        </TestRun>
        """;

    private static string WritePassingTrx(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        File.WriteAllText(path, ValidPassingTrx());
        return path;
    }

    private static string WriteFailingTrx(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        File.WriteAllText(path, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="Fails" outcome="Failed" />
              </Results>
              <ResultSummary outcome="Failed">
                <Counters total="1" executed="1" passed="0" failed="1" />
              </ResultSummary>
            </TestRun>
            """);
        return path;
    }

    private static AcceptanceVerificationResult FailedVerification(
        string directory,
        string trxFileName,
        string checkName) => new(
            Passed: false,
            Skipped: false,
            ExitCode: 1,
            OutputTail: $"{checkName} failed",
            Checks: [new AcceptanceCheckResult(checkName, false, 1, $"{checkName} failed")],
            TestResultPaths: [WriteFailingTrx(directory, trxFileName)]);

    private static string CreateAcceptanceCohortRepository()
    {
        var repo = CreateSeededRepository();
        RunGit(repo, "branch", "-M", "main");
        return repo;
    }

    private static void AssertNoCohortWorkspaces(string repo)
    {
        var worktreeRoot = Path.Combine(repo, GoalWorktrees.DirectoryName);
        Assert.Empty(Directory.EnumerateDirectories(worktreeRoot, "c-*"));
        Assert.Empty(Directory.EnumerateDirectories(worktreeRoot, "p-*"));
        Assert.Empty(Directory.EnumerateDirectories(worktreeRoot, "cohort-*"));
    }

    private static string CreateWorktreeCandidate(
        string repo,
        GoalId goalId,
        string relativePath,
        string contents)
    {
        var worktree = GoalWorktrees.Ensure(repo, goalId);
        var path = Path.Combine(worktree, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        RunGit(worktree, "add", relativePath);
        RunGit(worktree, "commit", "-m", $"Candidate {goalId.Value[..8]}");
        return RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
    }

    private static ConductorAcceptanceCohortSelection ProjectSelection(
        ConductorDriver driver,
        Goal first,
        Goal second)
    {
        var members = new[] { first, second }
            .Select(goal =>
            {
                var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(driver.ExecutionDirectory, goal.Id));
                var revisions = ConductorGitRevisionReader.ReadRequiredPair(worktree);
                Assert.False(string.IsNullOrWhiteSpace(revisions.BranchRevision));
                Assert.False(string.IsNullOrWhiteSpace(revisions.MainRevision));
                var result = driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive);
                var ready = result as GateReadyCandidateProjectionResult.Ready;
                Assert.True(
                    ready is not null,
                    result is GateReadyCandidateProjectionResult.Excluded excluded
                        ? $"Expected Ready projection for {goal.Id.Value[..8]}, but got {excluded.Reason}."
                        : $"Expected Ready projection for {goal.Id.Value[..8]}.");
                return ready.Projection;
            })
            .ToArray();
        return new ConductorAcceptanceCohortSelection(members, []);
    }

    private static ConductorMergeTrainSelection ProjectTrainSelection(
        ConductorDriver driver,
        params Goal[] goals)
    {
        var candidates = goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(
            goal.Id,
            driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive))).ToArray();
        return Assert.IsType<ConductorMergeTrainSelection>(ConductorMergeTrainSelector.Select(candidates));
    }

    private static void AssertNoMergeTrainWorkspaces(string repo)
    {
        var worktreeRoot = Path.Combine(repo, GoalWorktrees.DirectoryName);
        Assert.Empty(Directory.EnumerateDirectories(worktreeRoot, "t-*"));
    }

    private static (GoalId GoalId, string Revision) CreateCandidate(
        string repo,
        string goalValue,
        string relativePath,
        string contents)
    {
        var goalId = new GoalId(goalValue);
        RunGit(repo, "checkout", "-b", GoalWorktrees.BranchName(goalId), "main");
        var path = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        RunGit(repo, "add", relativePath);
        RunGit(repo, "commit", "-m", $"Candidate {goalValue[..8]}");
        var revision = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
        RunGit(repo, "checkout", "main");
        return (goalId, revision);
    }

    private static AcceptanceCohortMemberBinding Bind(
        GoalId goalId,
        string revision,
        string path,
        string resource) => new(
            goalId,
            revision,
            revision,
            [path],
            [resource],
            ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto,
             GateReadyMergeStatus.Clean.ToString(),
             GateReadyMergeReason.NoConflictsDetected.ToString());

    private static MergeTrainMemberBinding TrainBind(
        GoalId goalId,
        string revision,
        string path,
        string resource) => new(
            goalId,
            revision,
            revision,
            [path],
            [resource],
            ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto,
            GateReadyMergeStatus.Clean.ToString(),
             GateReadyMergeReason.NoConflictsDetected.ToString());

    private sealed class ModeledIntegrityLabeler(bool failLowSet = false) : IWorkerIntegrityLabeler
    {
        private readonly Dictionary<string, IntegrityLabelState> _states =
            new(StringComparer.OrdinalIgnoreCase);

        internal List<(string Kind, string Path)> Operations { get; } = [];

        internal List<(string Path, string Level, bool Recursive)> SetCalls { get; } = [];

        public IntegrityLabelState Query(string path)
        {
            var normalized = Path.GetFullPath(path);
            Operations.Add(("query", normalized));
            return ResolveState(normalized) ??
                new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
        }

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            var normalized = Path.GetFullPath(path);
            Operations.Add(("set", normalized));
            SetCalls.Add((normalized, level, recursive));
            var low = level.EndsWith('L');
            if (low && failLowSet)
            {
                return false;
            }

            _states[normalized] = new IntegrityLabelState(
                Exists: true,
                Low: low,
                Inheritable: level.Contains("(OI)(CI)", StringComparison.Ordinal),
                Medium: level.EndsWith('M'));
            return true;
        }

        internal void Seed(string path, IntegrityLabelState state) =>
            _states[Path.GetFullPath(path)] = state;

        internal int FindSetIndex(string path, string level, bool recursive)
        {
            var normalized = Path.GetFullPath(path);
            return SetCalls.FindIndex(call =>
                string.Equals(call.Path, normalized, StringComparison.OrdinalIgnoreCase) &&
                call.Level == level &&
                call.Recursive == recursive);
        }

        internal bool WouldDenyLowWrite(string path)
        {
            var current = Path.GetFullPath(path);
            return ResolveState(current) is not { Low: true };
        }

        private IntegrityLabelState? ResolveState(string path)
        {
            var current = Path.GetFullPath(path);
            var exact = true;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (_states.TryGetValue(current, out var state) && (exact || state.Inheritable))
                {
                    return state;
                }

                exact = false;
                current = Path.GetDirectoryName(current) ?? string.Empty;
            }

            return null;
        }
    }

    private sealed class BlockingAcceptanceVerifier(
        ManualResetEventSlim started,
        ManualResetEventSlim release,
        AcceptanceVerificationResult result) : IGoalAcceptanceVerifier
    {
        private int _runCount;

        internal int RunCount => Volatile.Read(ref _runCount);
        internal bool StableSlotLeaseObserved { get; private set; }
        internal string WorktreePath { get; private set; } = string.Empty;

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runCount);
            WorktreePath = worktreePath;
            StableSlotLeaseObserved = stableSlotLease is not null && stableSlotIndex is not null;
            var now = DateTimeOffset.UtcNow;
            typeof(GoalAcceptanceVerifier)
                .GetMethod("EmitGateProgress", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null,
                [
                    new AcceptanceGateProgress(
                        GoalId: goalId?.Value,
                        Phase: "controlled-cohort",
                        CurrentTarget: "blocking-verifier",
                        SlotIndex: stableSlotIndex,
                        ProcessId: Environment.ProcessId,
                        ChildProcessId: null,
                        StartedAt: now,
                        LastObservedAt: now,
                        LastProgressAt: now,
                        Elapsed: TimeSpan.Zero,
                        OutputBytes: 0,
                        HeartbeatPath: Path.Combine(worktreePath, "controlled-heartbeat.json"))
                ]);
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10), cancellationToken))
            {
                throw new TimeoutException("Controlled cohort verifier was not released.");
            }
            return Task.FromResult(result);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Focused evidence is not used by the blocking cohort fixture.");
    }

    private sealed class SequenceAcceptanceVerifier(
        IReadOnlyList<AcceptanceVerificationResult> results) : IGoalAcceptanceVerifier
    {
        private int _nextResult;

        internal int RunCount => _nextResult;
        internal List<GoalId?> GoalIds { get; } = [];
        internal List<IReadOnlyList<string>> ChangedFiles { get; } = [];

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default)
        {
            GoalIds.Add(goalId);
            ChangedFiles.Add(changedFiles?.ToArray() ?? []);
            Assert.True(_nextResult < results.Count, "The cohort invoked the acceptance verifier more times than expected.");
            return Task.FromResult(results[_nextResult++]);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Focused evidence is not used by the cohort sequence fixture.");
    }

    private sealed class ManifestThrowingAcceptanceVerifier(Exception exception) : IGoalAcceptanceVerifier
    {
        internal int RunCount { get; private set; }

        public string ComputeEffectivePlanIdentity(
            string worktreePath,
            IReadOnlyList<string>? changedFiles = null) => throw exception;

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            throw new InvalidOperationException("The gate must not run when manifest identity cannot be computed.");
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Focused evidence is not used by the manifest-failure fixture.");
    }
}
