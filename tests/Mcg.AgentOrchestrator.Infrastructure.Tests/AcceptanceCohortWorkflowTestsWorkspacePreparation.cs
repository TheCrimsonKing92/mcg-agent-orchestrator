using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class AcceptanceCohortWorkflowTestsWorkspacePreparation : AcceptanceCohortWorkflowTests
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
            var originalCandidate = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            var integratedMain = RunGitOutput(repo, "rev-parse", "main").Trim();

            var result = ConductorDriver.IntegrateMainBeforeDeveloperDispatch(repo, goal, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);

            Assert.Equal(DeveloperBranchIntegrationStatus.Integrated, result.Status);
            Assert.Empty(result.ConflictPaths);
            Assert.Equal(originalCandidate, result.OriginalCandidateSha);
            Assert.Equal(integratedMain, result.IntegratedMainSha);
            Assert.Equal(RunGitOutput(worktree, "rev-parse", "HEAD").Trim(), result.ResultingCandidateSha);
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

            var result = ConductorDriver.IntegrateMainBeforeDeveloperDispatch(repo, goal, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);

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
    public void WorkspaceRemoverOverride_DoesNotLeakIntoConcurrentExecutionContexts()
    {
        var defaultRemover = AcceptanceCohortWorkspace.WorkspaceRemover;
        Action<string, string> throwing = (_, _) =>
            throw new InvalidOperationException("simulated concurrent cleanup failure");
        using var overrideApplied = new ManualResetEventSlim();
        using var siblingObserved = new ManualResetEventSlim();
        Action<string, string>? observedByMutator = null;
        Action<string, string>? observedBySibling = null;

        var mutator = Task.Run(() =>
        {
            try
            {
                AcceptanceCohortWorkspace.WorkspaceRemover = throwing;
                overrideApplied.Set();
                siblingObserved.Wait(TimeSpan.FromSeconds(30));
                observedByMutator = AcceptanceCohortWorkspace.WorkspaceRemover;
            }
            finally
            {
                AcceptanceCohortWorkspace.WorkspaceRemover = defaultRemover;
            }
        });
        var sibling = Task.Run(() =>
        {
            overrideApplied.Wait(TimeSpan.FromSeconds(30));
            observedBySibling = AcceptanceCohortWorkspace.WorkspaceRemover;
            siblingObserved.Set();
        });
        Task.WaitAll(mutator, sibling);

        Assert.Same(throwing, observedByMutator);
        Assert.Same(defaultRemover, observedBySibling);
        Assert.Same(defaultRemover, AcceptanceCohortWorkspace.WorkspaceRemover);
    }
}
