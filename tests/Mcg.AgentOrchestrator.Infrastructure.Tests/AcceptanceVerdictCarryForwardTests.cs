using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;
using static LandingExecutorTests;

public sealed class AcceptanceVerdictCarryForwardTests : HostCapacityBoundTestBase
{
    [Xunit.Fact]
    public void CarryForward_DisjointLanding_LandsWithoutRegateOrIntent()
    {
        var repo = CreateGitRepository();
        try
        {
            var fixture = CreateAcceptedCandidate(repo, "src/candidate-only.txt", "candidate");
            AppendCommit(repo, "src/unrelated-lane.txt", "racing landing");
            var racingMain = ReadGit(repo, "rev-parse", "main");

            var result = fixture.Driver.CompleteParallelLandingAcceptance(
                fixture.Candidate,
                ConductorAutonomyPolicy.Conservative,
                fixture.Acceptance,
                out var leaseHeld);

            Assert.False(leaseHeld);
            Assert.DoesNotContain("acceptance verification not passed", result.Outcome.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("candidate", ReadGit(repo, "show", "main:src/candidate-only.txt"));
            var carry = Assert.Single(GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:acceptance-main-advance-carry"));
            Assert.Equal("passed", carry.AcceptanceOutcome);
            Assert.Equal(racingMain, carry.MainHeadSha);
            Assert.Contains("rule=path-level-independence", carry.Detail, StringComparison.Ordinal);
            AssertNoLandingEscalation(fixture, repo);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void CarryForward_Overlap_HoldsForRevalidationAndPreservesPriorReceipt()
    {
        var repo = CreateGitRepository();
        try
        {
            var fixture = CreateAcceptedCandidate(
                repo,
                "scripts/Invoke-ProcessLifecycleEvidenceHarness.ps1",
                "candidate");
            var originalReceipt = Assert.Single(GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:acceptance"));
            var task = fixture.Goal.Tasks.Single();
            var originalTaskStatus = fixture.Kernel.GetTask(fixture.Goal.Id, task.Id).Status;
            AppendCommit(repo, "docs/architecture-migration.md", "racing landing");
            var racingMain = ReadGit(repo, "rev-parse", "main");

            var result = fixture.Driver.CompleteParallelLandingAcceptance(
                fixture.Candidate,
                ConductorAutonomyPolicy.Conservative,
                fixture.Acceptance,
                out _);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Contains("revalidation required before landing", held.Reason, StringComparison.Ordinal);
            Assert.Contains(
                "ACCEPTANCE_MAIN_ADVANCE disposition=overlap kind=Script " +
                "landed=docs/architecture-migration.md " +
                "verified=scripts/Invoke-ProcessLifecycleEvidenceHarness.ps1",
                held.Reason,
                StringComparison.Ordinal);
            Assert.Equal(racingMain, ReadGit(repo, "rev-parse", "main"));
            Assert.DoesNotContain(
                GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries,
                entry => entry.Operation == "conductor:acceptance-main-advance-carry");
            var diagnostic = Assert.Single(GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:acceptance-main-advance"));
            Assert.Null(diagnostic.AcceptanceOutcome);
            Assert.Contains("disposition=overlap", diagnostic.Detail, StringComparison.Ordinal);
            Assert.Contains("kind=Script", diagnostic.Detail, StringComparison.Ordinal);
            var preservedReceipt = Assert.Single(GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:acceptance"));
            Assert.Equal(originalReceipt, preservedReceipt);
            Assert.Equal(originalTaskStatus, fixture.Kernel.GetTask(fixture.Goal.Id, task.Id).Status);
            AssertNoLandingEscalation(fixture, repo);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void CarryForward_Overlap_FreshPairLandsOnSubsequentTick()
    {
        var repo = CreateGitRepository();
        try
        {
            AppendCommit(repo, "src/shared.txt", "base-1\nbase-2\nbase-3\nbase-4\nbase-5\nbase-6\nbase-7\nbase-8\nbase-9");
            var fixture = CreateAcceptedCandidate(
                repo,
                "src/shared.txt",
                "candidate-1\nbase-2\nbase-3\nbase-4\nbase-5\nbase-6\nbase-7\nbase-8\nbase-9");
            var originalReceipt = Assert.Single(GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:acceptance"));
            var task = fixture.Goal.Tasks.Single();
            var originalTaskStatus = fixture.Kernel.GetTask(fixture.Goal.Id, task.Id).Status;
            AppendCommit(repo, "src/shared.txt", "base-1\nbase-2\nbase-3\nbase-4\nbase-5\nbase-6\nbase-7\nbase-8\nracing-9");

            var heldResult = fixture.Driver.CompleteParallelLandingAcceptance(
                fixture.Candidate,
                ConductorAutonomyPolicy.Conservative,
                fixture.Acceptance,
                out _);

            Assert.IsType<ConductorAdvanceOutcome.Held>(heldResult.Outcome);
            Assert.Equal(originalTaskStatus, fixture.Kernel.GetTask(fixture.Goal.Id, task.Id).Status);
            var pairBBranch = ReadGit(fixture.Worktree, "rev-parse", "HEAD");
            var pairBMain = ReadGit(repo, "rev-parse", "main");
            var pairBCandidate = ConductorParallelAcceptanceCandidate.Create(
                fixture.Goal,
                0,
                ["src/shared.txt"],
                pairBBranch,
                pairBMain);
            Assert.NotEqual(fixture.Candidate.CandidateKey, pairBCandidate.CandidateKey);
            GoalOperationJournal.AcceptancePassed(
                repo,
                fixture.Goal,
                "conductor:acceptance",
                pairBBranch,
                pairBMain,
                "fresh acceptance after overlapping main advance");
            var pairBAcceptance = fixture.Acceptance with
            {
                BranchHeadSha = pairBBranch,
                MainHeadSha = pairBMain
            };

            var landing = fixture.Driver.CompleteParallelLandingAcceptance(
                pairBCandidate,
                ConductorAutonomyPolicy.Conservative,
                pairBAcceptance,
                out _);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(landing.Outcome);
            Assert.Equal(
                "candidate-1\nbase-2\nbase-3\nbase-4\nbase-5\nbase-6\nbase-7\nbase-8\nracing-9",
                ReadGit(repo, "show", "main:src/shared.txt"));
            Assert.Equal(originalTaskStatus, fixture.Kernel.GetTask(fixture.Goal.Id, task.Id).Status);
            Assert.Single(GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:acceptance-main-advance"));
            Assert.Contains(
                GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries,
                entry => entry.Operation == "conductor:acceptance" &&
                    entry.HasCandidate(fixture.BranchHeadSha, fixture.MainHeadSha) &&
                    entry == originalReceipt);
            AssertNoLandingEscalation(fixture, repo);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void CarryForward_RepositoryWideCandidate_StillRequiresRegate()
    {
        var repo = CreateGitRepository();
        try
        {
            var fixture = CreateAcceptedCandidate(repo, "Directory.Build.props", "candidate");
            AppendCommit(repo, "src/unrelated-lane.txt", "racing landing");
            var racingMain = ReadGit(repo, "rev-parse", "main");

            var result = fixture.Driver.CompleteParallelLandingAcceptance(
                fixture.Candidate,
                ConductorAutonomyPolicy.Conservative,
                fixture.Acceptance,
                out _);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Contains("revalidation required before landing", held.Reason, StringComparison.Ordinal);
            Assert.Contains("disposition=overlap kind=BuildSystem", held.Reason, StringComparison.Ordinal);
            Assert.Equal(racingMain, ReadGit(repo, "rev-parse", "main"));
            Assert.DoesNotContain(
                GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries,
                entry => entry.Operation == "conductor:acceptance-main-advance-carry");
            var diagnostic = Assert.Single(GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:acceptance-main-advance"));
            Assert.Null(diagnostic.AcceptanceOutcome);
            Assert.Contains("disposition=overlap", diagnostic.Detail, StringComparison.Ordinal);
            Assert.Contains("kind=BuildSystem", diagnostic.Detail, StringComparison.Ordinal);
            AssertNoLandingEscalation(fixture, repo);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void CarryForward_UnknownRelationship_StillRequiresRegate()
    {
        var repo = CreateGitRepository();
        try
        {
            var fixture = CreateAcceptedCandidate(repo, "src/candidate-only.txt", "candidate");
            AppendCommit(repo, "src/unrelated-lane.txt", "racing landing");
            var missingMainEvidence = fixture.Acceptance with { MainHeadSha = null };

            var result = fixture.Driver.CompleteParallelLandingAcceptance(
                fixture.Candidate,
                ConductorAutonomyPolicy.Conservative,
                missingMainEvidence,
                out _);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Contains("revalidation required before landing", held.Reason, StringComparison.Ordinal);
            Assert.Contains("disposition=unknown", held.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(
                GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries,
                entry => entry.Operation == "conductor:acceptance-main-advance-carry");
            var diagnostic = Assert.Single(GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:acceptance-main-advance"));
            Assert.Null(diagnostic.AcceptanceOutcome);
            Assert.Contains("disposition=unknown", diagnostic.Detail, StringComparison.Ordinal);
            AssertNoLandingEscalation(fixture, repo);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void CarryForward_UnchangedCandidate_LandsWithoutMainAdvanceDiagnostic()
    {
        var repo = CreateGitRepository();
        try
        {
            var fixture = CreateAcceptedCandidate(repo, "src/candidate-only.txt", "candidate");

            var result = fixture.Driver.CompleteParallelLandingAcceptance(
                fixture.Candidate,
                ConductorAutonomyPolicy.Conservative,
                fixture.Acceptance,
                out var leaseHeld);

            Assert.False(leaseHeld);
            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Equal("candidate", ReadGit(repo, "show", "main:src/candidate-only.txt"));
            Assert.DoesNotContain(
                GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries,
                entry => entry.Operation.StartsWith("conductor:acceptance-main-advance", StringComparison.Ordinal));
            AssertNoLandingEscalation(fixture, repo);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void CarryForward_FailingVerdict_DoesNotEnterClassifier()
    {
        var repo = CreateGitRepository();
        try
        {
            var fixture = CreateAcceptedCandidate(repo, "src/candidate-only.txt", "candidate");
            AppendCommit(repo, "src/unrelated-lane.txt", "racing landing");
            var failing = fixture.Acceptance with { Passed = false };

            _ = fixture.Driver.CompleteParallelLandingAcceptance(
                fixture.Candidate,
                ConductorAutonomyPolicy.Conservative,
                failing,
                out _);

            Assert.DoesNotContain(
                GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries,
                entry => entry.Operation.StartsWith("conductor:acceptance-main-advance", StringComparison.Ordinal));
            Assert.Equal(fixture.BranchHeadSha, ReadGit(fixture.Worktree, "rev-parse", "HEAD"));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void CarryForward_SecondMainAdvance_InvalidatesCarriedReceipt()
    {
        var repo = CreateGitRepository();
        try
        {
            var fixture = CreateAcceptedCandidate(repo, "src/candidate-only.txt", "candidate");
            AppendCommit(repo, "src/unrelated-lane.txt", "racing landing");
            var rebase = GoalWorktrees.TryRebaseOntoMain(repo, fixture.Goal.Id);
            Assert.True(rebase.UpdatedBranch, rebase.Message);

            var disposition = fixture.Driver.CarryForwardGreenVerdict(fixture.Candidate, fixture.Acceptance);

            Assert.IsType<AcceptanceMainAdvanceDisposition.Disjoint>(disposition);
            Assert.True(GoalAcceptanceStatusProjector.Build(fixture.Kernel, fixture.Goal, repo).IsAccepted);
            AppendCommit(repo, "src/second-race.txt", "second main advance");
            Assert.False(GoalAcceptanceStatusProjector.Build(fixture.Kernel, fixture.Goal, repo).IsAccepted);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("Directory.Build.props", "BuildSystem")]
    [Xunit.InlineData("src/bin/generated.cs", "GeneratedOrNoisy")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", "SharedInfrastructure")]
    [Xunit.InlineData("scripts/verify.ps1", "Script")]
    [Xunit.InlineData("unmapped/file.bin", "Unknown")]
    public void Classifier_RepositoryWideLanding_FailsClosed(string landedPath, string expectedKind)
    {
        var disposition = AcceptanceMainAdvanceClassifier.Classify(
            "main-1",
            "main-2",
            ["src/candidate.cs"],
            ["src/candidate.cs"],
            ["src/candidate.cs"],
            [landedPath]);

        var overlap = Assert.IsType<AcceptanceMainAdvanceDisposition.Overlap>(disposition);
        Assert.Equal(expectedKind, overlap.Kind);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Directory.Build.props", "BuildSystem")]
    [Xunit.InlineData("src/bin/generated.cs", "GeneratedOrNoisy")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", "SharedInfrastructure")]
    [Xunit.InlineData("scripts/verify.ps1", "Script")]
    [Xunit.InlineData("unmapped/file.bin", "Unknown")]
    public void Classifier_RepositoryWideCandidate_FailsClosed(string candidatePath, string expectedKind)
    {
        var disposition = AcceptanceMainAdvanceClassifier.Classify(
            "main-1",
            "main-2",
            [candidatePath],
            [candidatePath],
            [candidatePath],
            ["src/unrelated.cs"]);

        var overlap = Assert.IsType<AcceptanceMainAdvanceDisposition.Overlap>(disposition);
        Assert.Equal(candidatePath, overlap.VerifiedPath);
        Assert.Equal(expectedKind, overlap.Kind);
    }

    private static CandidateFixture CreateAcceptedCandidate(string repo, string path, string content)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(repo);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement the accepted candidate.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Acceptance carry-forward test", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var mainHead = ReadGit(repo, "rev-parse", "main");
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("test-worker", "implement", repo, DateTimeOffset.UtcNow));
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, mainHead);
        var goalBranch = GoalWorktrees.BranchName(goal.Id);
        AddGoalBranchCommit(repo, goalBranch, path, content);
        var worktree = GoalWorktrees.Ensure(repo, goal.Id);
        var branchHead = ReadGit(worktree, "rev-parse", "HEAD");
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, branchHead);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        GoalOperationJournal.AcceptancePassed(
            repo,
            goal,
            "conductor:acceptance",
            branchHead,
            mainHead,
            "passing acceptance before racing landing");
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            [path],
            branchHead,
            mainHead);
        var acceptance = new AcceptanceVerificationSummary(
            true,
            [],
            BranchHeadSha: branchHead,
            MainHeadSha: mainHead);
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default(),
            cleanupHooks: WorktreeCleanupContext.Load(
                attentionStoreDirectory: workspace.OrchestratorDirectory,
                buildStorageRoot: new DotnetBuildStorageRoot(
                    Path.Combine(workspace.ExecutionDirectory, ".orchestrator", "test-dotnet"))).Hooks);
        return new CandidateFixture(
            workspace,
            kernel,
            goal,
            candidate,
            acceptance,
            driver,
            worktree,
            branchHead,
            mainHead);
    }

    private static void AssertNoLandingEscalation(CandidateFixture fixture, string repo)
    {
        var inbox = OperatorInbox.Build(
            fixture.Kernel,
            [],
            WorkerProfileCatalog.Default(),
            OrchestratorWorkspace.ForDirectory(repo),
            fixture.Goal.Id.Value[..8]);
        Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.LandingEscalation);
    }

    private sealed record CandidateFixture(
        OrchestratorWorkspace Workspace,
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        ConductorParallelAcceptanceCandidate Candidate,
        AcceptanceVerificationSummary Acceptance,
        ConductorDriver Driver,
        string Worktree,
        string BranchHeadSha,
        string MainHeadSha);
}
