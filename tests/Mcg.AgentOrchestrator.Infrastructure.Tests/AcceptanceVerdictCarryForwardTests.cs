using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;
using static LandingExecutorTests;

public sealed class AcceptanceVerdictCarryForwardTests
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
    public void CarryForward_OverlappingLanding_StillRequiresRegate()
    {
        var repo = CreateGitRepository();
        try
        {
            AppendCommit(repo, "src/shared.txt", "base-1\nbase-2\nbase-3\nbase-4\nbase-5\nbase-6\nbase-7\nbase-8\nbase-9");
            var fixture = CreateAcceptedCandidate(repo, "src/shared.txt", "candidate-1\nbase-2\nbase-3\nbase-4\nbase-5\nbase-6\nbase-7\nbase-8\nbase-9");
            AppendCommit(repo, "src/shared.txt", "base-1\nbase-2\nbase-3\nbase-4\nbase-5\nbase-6\nbase-7\nbase-8\nracing-9");
            var racingMain = ReadGit(repo, "rev-parse", "main");

            var result = fixture.Driver.CompleteParallelLandingAcceptance(
                fixture.Candidate,
                ConductorAutonomyPolicy.Conservative,
                fixture.Acceptance,
                out _);

            var escalation = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
            Assert.Equal("acceptance verification not passed", escalation.Reason);
            Assert.Equal(racingMain, ReadGit(repo, "rev-parse", "main"));
            Assert.DoesNotContain(
                GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries,
                entry => entry.Operation == "conductor:acceptance-main-advance-carry");
            var diagnostic = Assert.Single(GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:acceptance-main-advance"));
            Assert.Null(diagnostic.AcceptanceOutcome);
            Assert.Contains("disposition=overlap", diagnostic.Detail, StringComparison.Ordinal);
            AssertLandingEscalation(fixture, repo);
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

            var escalation = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
            Assert.Equal("acceptance verification not passed", escalation.Reason);
            Assert.Equal(racingMain, ReadGit(repo, "rev-parse", "main"));
            Assert.DoesNotContain(
                GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries,
                entry => entry.Operation == "conductor:acceptance-main-advance-carry");
            var diagnostic = Assert.Single(GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:acceptance-main-advance"));
            Assert.Null(diagnostic.AcceptanceOutcome);
            Assert.Contains("disposition=overlap", diagnostic.Detail, StringComparison.Ordinal);
            Assert.Contains("kind=BuildSystem", diagnostic.Detail, StringComparison.Ordinal);
            AssertLandingEscalation(fixture, repo);
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

            var escalation = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
            Assert.Equal("acceptance verification not passed", escalation.Reason);
            Assert.DoesNotContain(
                GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries,
                entry => entry.Operation == "conductor:acceptance-main-advance-carry");
            var diagnostic = Assert.Single(GoalOperationJournal.Read(repo, fixture.Goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:acceptance-main-advance"));
            Assert.Null(diagnostic.AcceptanceOutcome);
            Assert.Contains("disposition=unknown", diagnostic.Detail, StringComparison.Ordinal);
            AssertLandingEscalation(fixture, repo);
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
        var (kernel, goal) = CreateVerifiedGoal(repo);
        var goalBranch = GoalWorktrees.BranchName(goal.Id);
        AddGoalBranchCommit(repo, goalBranch, path, content);
        var worktree = GoalWorktrees.Ensure(repo, goal.Id);
        var branchHead = ReadGit(worktree, "rev-parse", "HEAD");
        var mainHead = ReadGit(repo, "rev-parse", "main");
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
            branchHead);
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

    private static void AssertLandingEscalation(CandidateFixture fixture, string repo)
    {
        var inbox = OperatorInbox.Build(
            fixture.Kernel,
            [],
            WorkerProfileCatalog.Default(),
            OrchestratorWorkspace.ForDirectory(repo),
            fixture.Goal.Id.Value[..8]);
        Assert.Contains(inbox.Items, item => item.Kind == OperatorInboxKind.LandingEscalation);
    }

    private sealed record CandidateFixture(
        OrchestratorWorkspace Workspace,
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        ConductorParallelAcceptanceCandidate Candidate,
        AcceptanceVerificationSummary Acceptance,
        ConductorDriver Driver,
        string Worktree,
        string BranchHeadSha);
}
