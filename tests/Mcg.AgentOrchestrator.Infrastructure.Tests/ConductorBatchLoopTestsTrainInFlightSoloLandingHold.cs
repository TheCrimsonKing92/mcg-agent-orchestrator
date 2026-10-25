using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsTrainInFlightSoloLandingHold : AcceptanceCohortWorkflowTests
{
    private static readonly ConductorAutonomyPolicy Policy =
        ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 1 };

    [Fact]
    public void NonMember_FailedTrain_HoldsThenLandsWithoutAnotherSoloGate()
    {
        using var fixture = CreateFixture(trainPasses: false);
        StartTrain(fixture);
        AssertHeld(fixture);
        var soloRuns = SoloRuns(fixture);
        var mainBefore = Main(fixture);

        fixture.Starts.RunPending();
        Assert.Equal(0, fixture.Starts.PendingCount);
        Assert.Equal(0, fixture.Driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);
        Assert.Empty(new MergeTrainAcceptanceStore(Path.Combine(
            OrchestratorWorkspace.ForDirectory(fixture.Repo).OrchestratorDirectory,
            "merge-train-acceptance.db")).ReadPassedReceiptsForGoal(fixture.Members[0].Id));
        Assert.Equal(mainBefore, Main(fixture));

        var release = RunOneTick(fixture);

        // Criterion 3: land on the release tick; record and cleanup follow on ordinary ticks.
        Assert.Contains(release.ProgressLines ?? [], line => line.StartsWith(
            $"GOAL goal={fixture.Solo.Id.Value[..8]} result=executed state=Verified", StringComparison.Ordinal));
        Assert.Equal(fixture.Solo.Id.Value, Assert.Single(fixture.Landings).GoalId);
        Assert.NotEqual(mainBefore, Main(fixture));
        Assert.Equal(soloRuns, SoloRuns(fixture));
        CompleteLandedSoloLifecycle(fixture);
        Assert.Equal(soloRuns, SoloRuns(fixture));
        Assert.Equal(3, fixture.Verifier.RunCount);
    }

    [Fact]
    public void NonMember_PassedTrain_RegatesOnNewMainBeforeLanding()
    {
        using var fixture = CreateFixture(trainPasses: true);
        StartTrain(fixture);
        AssertHeld(fixture);
        var mainBefore = Main(fixture);
        var soloRuns = SoloRuns(fixture);
        fixture.Starts.RunPending();
        Assert.Equal(0, fixture.Driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);

        // Land the persisted train receipt before solo replay; settle-tick ordering is separate.
        var landed = fixture.Driver.RunMergeTrain(ProjectTrainSelection(fixture.Driver, fixture.Members),
            fixture.Members, Policy, runGateInBackground: true);
        Assert.Equal(3, landed.MemberResults.Count);
        Assert.All(fixture.Members, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
        Assert.Equal(3, fixture.Landings.Count);
        var postTrainMain = Main(fixture);
        Assert.NotEqual(mainBefore, postTrainMain);
        Assert.DoesNotContain(fixture.Landings, receipt => receipt.GoalId == fixture.Solo.Id.Value);
        Assert.Empty(PassedOutcomes(fixture, postTrainMain));

        // Observe the new pass while mutation is held, before any solo landing receipt exists.
        fixture.Driver.LandingMutationBlocker = () => "observe post-train solo pass";
        for (var tick = 0; tick < 6 && (PassedOutcomes(fixture, postTrainMain).Count == 0 ||
            fixture.Solo.Status != GoalStatus.Verified ||
            !fixture.Driver.ParallelAcceptanceAttemptCoordinator.HasPassedReconciledReceipt(fixture.Solo.Id.Value)); tick++)
            RunOneTick(fixture);
        var currentPass = Assert.Single(PassedOutcomes(fixture, postTrainMain));
        Assert.Equal(postTrainMain, currentPass.MainHeadSha);
        Assert.NotEqual("conductor:acceptance-main-advance-carry", currentPass.Operation);
        Assert.Equal(soloRuns + 1, SoloRuns(fixture));
        Assert.DoesNotContain(fixture.Landings, receipt => receipt.GoalId == fixture.Solo.Id.Value);
        Assert.Equal(GoalStatus.Verified, fixture.Solo.Status);
        Assert.Equal(postTrainMain, Main(fixture));

        fixture.Driver.LandingMutationBlocker = null;
        RunOneTick(fixture);

        Assert.Single(fixture.Landings, receipt => receipt.GoalId == fixture.Solo.Id.Value);
        CompleteLandedSoloLifecycle(fixture);
        Assert.Equal(soloRuns + 1, SoloRuns(fixture));
        Assert.Equal(3, fixture.Verifier.RunCount);
    }

    [Fact]
    public void NonMember_LiveCohort_MatchesFirstTickLandingWithoutGroupedRun()
    {
        var noRun = RunBaseline(registerCohort: false);
        var cohort = RunBaseline(registerCohort: true);

        Assert.Equal(("executed", GoalStatus.Verified, 1, true), noRun);
        Assert.Equal(noRun, cohort);
    }

    private static (string Outcome, GoalStatus Status, int Receipts, bool MainMoved)
        RunBaseline(bool registerCohort)
    {
        using var fixture = CreateFixture(trainPasses: false);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (registerCohort)
            {
                var selection = ProjectSelection(fixture.Driver, fixture.Members[0], fixture.Members[1]);
                Assert.True(fixture.Driver.TryRegisterCohortGateRunForTests(selection, completion));
                var root = Assert.Single(fixture.Driver.GetActiveAcceptanceCohortCapacity().ActiveRoots);
                Assert.False(root.Key.StartsWith("train:", StringComparison.Ordinal));
                Assert.DoesNotContain(fixture.Solo.Id.Value, root.MemberGoalIds);
            }
            else
                Assert.Equal(0, fixture.Driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);
            var mainBefore = Main(fixture);

            var tick = RunOneTick(fixture);

            Assert.Equal(fixture.Solo.Id.Value, Assert.Single(fixture.Landings).GoalId);
            Assert.NotEqual(mainBefore, Main(fixture));
            // Compare the landing tick while the cohort is still in flight; record and cleanup are later steps.
            Assert.Equal(1, SoloRuns(fixture));
            if (registerCohort)
            {
                Assert.False(completion.Task.IsCompleted);
                Assert.Equal(1, fixture.Driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);
            }
            var line = Assert.Single(tick.ProgressLines ?? [], line =>
                line.StartsWith($"GOAL goal={fixture.Solo.Id.Value[..8]} ", StringComparison.Ordinal));
            var result = Assert.Single(line.Split(' '), part => part.StartsWith("result=", StringComparison.Ordinal));
            return (result["result=".Length..], fixture.Solo.Status, fixture.Landings.Count,
                mainBefore != Main(fixture));
        }
        finally { completion.TrySetResult(); }
    }

    private static void StartTrain(Fixture fixture)
    {
        fixture.Starts.Capture(fixture.Driver);
        var started = fixture.Driver.RunMergeTrain(ProjectTrainSelection(fixture.Driver, fixture.Members),
            fixture.Members, Policy, runGateInBackground: true);
        Assert.Contains("outcome=inflight", started.Detail, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Starts.PendingCount);
        var root = Assert.Single(fixture.Driver.GetActiveAcceptanceCohortCapacity().ActiveRoots);
        Assert.StartsWith("train:", root.Key);
        Assert.Equal(3, root.MemberGoalIds.Count);
        Assert.DoesNotContain(fixture.Solo.Id.Value, root.MemberGoalIds);
        Assert.Equal(1, fixture.Verifier.RunCount);
    }

    private static void AssertHeld(Fixture fixture)
    {
        var mainBefore = Main(fixture);
        var tick = RunOneTick(fixture);
        var progress = string.Join(" | ", tick.ProgressLines ?? []);
        Assert.Equal(GoalStatus.Verified, fixture.Solo.Status);
        Assert.Equal(mainBefore, Main(fixture));
        Assert.Empty(fixture.Landings);
        var key = Assert.Single(fixture.Driver.GetActiveAcceptanceCohortCapacity().ActiveRoots).Key;
        var reason = $"Landing held at mutation boundary: merge train {key} is in flight and goal " +
            $"{fixture.Solo.Id.Value[..8]} is not a member; landing waits for the train to settle";
        Assert.Contains($"GOAL goal={fixture.Solo.Id.Value[..8]} result=held state=Verified", progress,
            StringComparison.Ordinal);
        Assert.Contains("reason=" + ConductorBatchLoop.SanitizeReason(reason), progress, StringComparison.Ordinal);
        Assert.Contains("decision=landing-completion/8/landing-mutation-boundary-hold",
            progress, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Starts.PendingCount);
        Assert.Equal(1, fixture.Driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);
        Assert.Equal(1, SoloRuns(fixture));
    }

    private static BatchTickSummary RunOneTick(Fixture fixture)
    {
        BatchTickSummary? observed = null;
        _ = new ConductorBatchLoop().Run(fixture.Kernel, fixture.Driver, Policy,
            Path.Combine(fixture.Repo, "stop-does-not-exist"), maxIterations: 1,
            onTick: summary => observed = summary, onlyGoalId: fixture.Solo.Id.Value);
        return Assert.IsType<BatchTickSummary>(observed);
    }

    private static void CompleteLandedSoloLifecycle(Fixture fixture)
    {
        // Landing leaves Verified with durable merge evidence; later batch ticks record and clean up.
        Assert.True(GoalOperationJournal.HasCompletedLandingEvidence(
            GoalOperationJournal.Read(fixture.Driver.ExecutionDirectory, fixture.Solo.Id)));
        Assert.Equal(GoalStatus.Verified, fixture.Solo.Status);
        var soloRuns = SoloRuns(fixture);
        var main = Main(fixture);
        var receipts = fixture.Landings.Count;
        var record = RunOneTick(fixture);
        Assert.Contains(record.ProgressLines ?? [], line => line.StartsWith(
            $"GOAL goal={fixture.Solo.Id.Value[..8]} result=executed state=Merged", StringComparison.Ordinal));
        var cleanup = RunOneTick(fixture);
        Assert.Contains(cleanup.ProgressLines ?? [], line => line.StartsWith(
            $"GOAL goal={fixture.Solo.Id.Value[..8]} result=executed state=Recorded", StringComparison.Ordinal));
        Assert.Equal(GoalStatus.Completed, fixture.Solo.Status);
        Assert.Equal(soloRuns, SoloRuns(fixture));
        Assert.Equal(main, Main(fixture));
        Assert.Equal(receipts, fixture.Landings.Count);
    }

    private static IReadOnlyList<GoalOperationJournalEntry> PassedOutcomes(Fixture fixture, string main)
    {
        var journal = GoalOperationJournal.Read(fixture.Driver.ExecutionDirectory, fixture.Solo.Id);
        var branch = RunGitOutput(GoalWorktrees.TryResolve(fixture.Repo, fixture.Solo.Id)!,
            "rev-parse", "HEAD").Trim();
        return GoalOperationJournal.AcceptanceOutcomesForCandidate(journal, branch, main)
            .Where(entry => entry.AcceptanceOutcome == "passed").ToArray();
    }

    private static int SoloRuns(Fixture fixture) =>
        fixture.Verifier.GoalIds.Count(id => id == fixture.Solo.Id);

    private static string Main(Fixture fixture) => RunGitOutput(fixture.Repo, "rev-parse", "main").Trim();

    private static Fixture CreateFixture(bool trainPasses)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var solo = ReadyGoal(kernel, repo, "Solo fourth goal",
                "src/Mcg.AgentOrchestrator.Infrastructure/SoloFourth.cs");
            AcceptanceVerificationResult Pass(string name) => new(true, false, 0, null,
                Checks: [new AcceptanceCheckResult(name, true, 0, null)],
                TestResultPaths: [WritePassingTrx(repo, name + ".trx")]);
            var verifier = new SequenceAcceptanceVerifier([
                Pass("solo-before-train"),
                trainPasses ? Pass("train") : FailedVerification(repo, "train-red.trx", "train"),
                trainPasses ? Pass("solo-after-train") : FailedVerification(repo, "train-pair-red.trx", "train pair")]);
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                runAcceptanceAttemptsInCurrentProcess: true,
                cleanupHooks: CreateIsolatedCleanupContext(repo).Hooks);
            var fixture = new Fixture(repo, kernel, solo, driver, verifier);
            driver.SuccessfulLandingSink = fixture.Landings.Add;
            driver.LandingMutationBlocker = () => "seed hold";
            for (var tick = 0; tick < 5 &&
                !driver.ParallelAcceptanceAttemptCoordinator.HasPassedReconciledReceipt(solo.Id.Value); tick++)
                RunOneTick(fixture);
            Assert.True(driver.ParallelAcceptanceAttemptCoordinator.HasPassedReconciledReceipt(solo.Id.Value),
                "The pre-train solo pass was not reconciled during the seed ticks.");
            Assert.Equal(GoalStatus.Verified, solo.Status);
            Assert.Equal(1, SoloRuns(fixture));
            Assert.Empty(fixture.Landings);
            Assert.NotEmpty(PassedOutcomes(fixture, Main(fixture)));
            driver.LandingMutationBlocker = null;
            fixture.Members = [
                ReadyGoal(kernel, repo, "Train first", "tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs"),
                ReadyGoal(kernel, repo, "Train second", "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs"),
                ReadyGoal(kernel, repo, "Train third", "tests/Mcg.AgentOrchestrator.Dashboard.Tests/TrainThird.cs")];
            return fixture;
        }
        catch
        {
            DeleteDirectory(repo);
            throw;
        }
    }

    private static Goal ReadyGoal(AgentOrchestratorKernel kernel, string repo, string label, string path)
    {
        var goal = CreateCompletedGoal(kernel, label, repo);
        var candidate = CreateWorktreeCandidate(repo, goal.Id, path, "// " + label);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
        });
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "test",
            CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
        return goal;
    }

    private sealed class Fixture(string repo, AgentOrchestratorKernel kernel, Goal solo,
        ConductorDriver driver, SequenceAcceptanceVerifier verifier) : IDisposable
    {
        internal string Repo { get; } = repo;
        internal AgentOrchestratorKernel Kernel { get; } = kernel;
        internal Goal Solo { get; } = solo;
        internal Goal[] Members { get; set; } = [];
        internal ConductorDriver Driver { get; } = driver;
        internal SequenceAcceptanceVerifier Verifier { get; } = verifier;
        internal BackgroundGateStartHarness Starts { get; } = new();
        internal List<ConductorLandingReceipt> Landings { get; } = [];
        public void Dispose() => DeleteDirectory(Repo);
    }
}
