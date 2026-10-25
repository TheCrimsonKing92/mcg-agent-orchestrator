using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Real git/worktree integration: isolated repositories, DotnetBuildSlots collection.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsMergeTrainLandingBlockerClear : AcceptanceCohortWorkflowTests
{
    private const string FirstPath = "tests/Mcg.AgentOrchestrator.Core.Tests/ReceiptFirst.cs";
    private const string SecondPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ReceiptSecond.cs";
    private const string RacingPath = "src/Mcg.AgentOrchestrator.Infrastructure/RacingLanding.cs";

    [Fact]
    public void PassedTrain_UnchangedMain_ClearsBothLandedMemberBlockers()
    {
        using var fixture = CreatePassedTrain();
        Assert.Equal(fixture.Source.Identity.ObservedMainRevision,
            RunGitOutput(fixture.Repo, "rev-parse", "main").Trim());
        SeedBlockers(fixture);

        RunTick(fixture);

        var exported = fixture.Kernel.ExportSnapshot();
        Assert.All(fixture.Goals, member =>
        {
            var goal = fixture.Kernel.GetGoal(member.Id);
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Contains(goal.Timeline,
                entry => entry.Message == $"Landed by merge train receipt {fixture.Source.ReceiptId}.");
            Assert.Null(goal.LandingBlocker);
            var snapshot = Assert.Single(exported.Goals.Where(item => item.Id == goal.Id.Value));
            Assert.Equal(GoalStatus.Completed, snapshot.Status);
            Assert.Null(snapshot.LandingBlocker);
        });
        Assert.Equal(1, fixture.Verifier.RunCount);
    }

    [Fact]
    public void PassedTrain_MainAdvancedAndAcceptanceOccupied_KeepsLiveBlockers()
    {
        using var fixture = CreatePassedTrain();
        AdvanceMain(fixture, RacingPath, "// racing landing");
        Assert.NotEqual(fixture.Source.Identity.ObservedMainRevision,
            RunGitOutput(fixture.Repo, "rev-parse", "main").Trim());
        OccupyAcceptance(fixture);
        SeedBlockers(fixture);

        RunTick(fixture);

        Assert.All(fixture.Goals, member =>
        {
            var goal = fixture.Kernel.GetGoal(member.Id);
            Assert.NotEqual(GoalStatus.Completed, goal.Status);
            Assert.True(goal.Status is GoalStatus.Verified or GoalStatus.Verifying);
            Assert.NotNull(goal.LandingBlocker);
        });
        Assert.Equal(1, fixture.Verifier.RunCount);
    }

    private static void SeedBlockers(Fixture fixture)
    {
        Assert.Equal(2, fixture.Goals.Length);
        foreach (var member in fixture.Goals)
        {
            var goal = fixture.Kernel.GetGoal(member.Id);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            var blocker = new GoalLandingBlocker("untyped/BackgroundAttempt",
                "Acceptance cohort gate owns this member; awaiting train landing",
                "untyped/BackgroundAttempt:", new DateTimeOffset(2026, 10, 24, 9, 0, 0,
                    TimeSpan.FromHours(-5)), "unmapped", "unmapped");
            goal.ObserveLandingBlocker(blocker);
            Assert.Equal(blocker, goal.LandingBlocker);
        }
    }

    // Passed-train fixture shape mirrors ConductorBatchLoopTestsTrainReceiptRevalidation.
    private static Fixture CreatePassedTrain()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        Directory.CreateDirectory(Path.Combine(repo, "config"));
        File.WriteAllText(Path.Combine(repo, "config/acceptance-manifest.json"), """
            {"checks":[{"name":"core tests","type":"dotnet-test",
            "project":"tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"}]}
            """);
        var project = Path.Combine(repo, "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project />");
        RunGit(repo, "add", "config/acceptance-manifest.json", "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj");
        RunGit(repo, "commit", "-m", "Seed readable train manifest");
        var kernel = new AgentOrchestratorKernel();
        var goals = new[] { ReadyGoal(kernel, repo, "First", FirstPath, "first candidate"),
            ReadyGoal(kernel, repo, "Second", SecondPath, "second candidate") };
        var passed = new AcceptanceVerificationResult(true, false, 0, null,
            Checks: [new AcceptanceCheckResult("core tests", true, 0, null)],
            TestResultPaths: [WritePassingTrx(repo, "train-green.trx")]);
        var verifier = new SequenceAcceptanceVerifier([passed, passed]);
        var workspace = OrchestratorWorkspace.ForDirectory(repo);
        var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(), runAcceptanceAttemptsInCurrentProcess: true,
            cleanupHooks: CreateIsolatedCleanupContext(repo).Hooks);
        new BackgroundGateStartHarness().Inline(driver);
        var started = driver.RunMergeTrain(Selection(driver, goals), goals, ConductorAutonomyPolicy.Permissive,
            runGateInBackground: true);
        Assert.Contains("outcome=inflight", started.Detail, StringComparison.Ordinal);
        var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
        var source = Assert.Single(store.ReadPassedReceiptsForGoal(goals[0].Id));
        Assert.Equal(MergeTrainGateOutcome.Passed, source.Outcome);
        Assert.Equal(1, verifier.RunCount);
        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
        return new(repo, kernel, goals, driver, verifier, source);
    }

    private static Goal ReadyGoal(AgentOrchestratorKernel kernel, string repo, string label, string path, string contents)
    {
        var goal = CreateCompletedGoal(kernel, label, repo);
        var candidate = CreateWorktreeCandidate(repo, goal.Id, path, contents);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
        });
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "test",
            CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
        return goal;
    }

    private static ConductorMergeTrainSelection Selection(ConductorDriver driver, Goal[] goals) => new(goals.Select(goal =>
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(driver.ProjectGateReadyCandidate(goal,
            ConductorAutonomyPolicy.Permissive)).Projection).ToArray());

    private static void OccupyAcceptance(Fixture fixture)
    {
        var first = ReadyGoal(fixture.Kernel, fixture.Repo, "Occupant one",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Occupant.cs", "occupant one");
        // Both occupants need authoritative resource keys to project as Ready.
        var second = ReadyGoal(fixture.Kernel, fixture.Repo, "Occupant two",
            "tests/Mcg.AgentOrchestrator.Execution.Tests/Occupant.cs", "occupant two");
        new BackgroundGateStartHarness().Capture(fixture.Driver);
        var occupant = fixture.Driver.RunAcceptanceCohort(ProjectSelection(fixture.Driver, first, second),
            [first, second], ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
        Assert.Contains("outcome=inflight", occupant.Detail, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);
    }

    private static void AdvanceMain(Fixture fixture, string path, string contents)
    {
        var fullPath = Path.Combine(fixture.Repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
        RunGit(fixture.Repo, "add", path);
        RunGit(fixture.Repo, "commit", "-m", "Advance main");
    }

    private static BatchTickSummary RunTick(Fixture fixture)
    {
        BatchTickSummary? observed = null;
        fixture.Loop.Run(fixture.Kernel, fixture.Driver, ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 1 },
            Path.Combine(fixture.Repo, "stop-does-not-exist"), maxIterations: 1, onTick: summary => observed = summary);
        return Assert.IsType<BatchTickSummary>(observed);
    }

    private sealed record Fixture(string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals, ConductorDriver Driver,
        SequenceAcceptanceVerifier Verifier, MergeTrainReceipt Source) : IDisposable
    {
        internal ConductorBatchLoop Loop { get; } = new(conductEventLogWriter:
            new ConductEventLogWriter(Path.Combine(Repo, "blocker-clear-conduct.jsonl")));
        public void Dispose() => DeleteDirectory(Repo);
    }
}
