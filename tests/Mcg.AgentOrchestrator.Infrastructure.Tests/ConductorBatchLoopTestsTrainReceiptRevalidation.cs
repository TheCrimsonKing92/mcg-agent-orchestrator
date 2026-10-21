using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsTrainReceiptRevalidation : AcceptanceCohortWorkflowTests
{
    private const string FirstPath = "tests/Mcg.AgentOrchestrator.Core.Tests/ReceiptFirst.cs";
    private const string SecondPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ReceiptSecond.cs";
    private const string RacingPath = "src/Mcg.AgentOrchestrator.Infrastructure/RacingLanding.cs";

    [Fact]
    public void DisjointMainAdvance_RecordsPendingThenLandedFromNewReceipt()
    {
        using var fixture = CreatePassedTrain();
        AdvanceMain(fixture, RacingPath, "// racing landing");
        OccupyAcceptance(fixture);
        var current = RunGitOutput(fixture.Repo, "rev-parse", "main").Trim();
        RunTick(fixture);

        // Receipt diagnostics go directly to the conduct log, outside the tick summary accumulator.
        var conductLines = ConductDetails(fixture);
        var revalidate = Assert.Single(conductLines.Where(line => line.StartsWith("TRAIN_RECEIPT_REVALIDATE ", StringComparison.Ordinal)));
        Assert.Contains($"receipt={fixture.Source.ReceiptId}", revalidate);
        Assert.Contains($"sourceMain={fixture.Source.Identity.ObservedMainRevision} currentMain={current}", revalidate);
        Assert.DoesNotContain(conductLines, line => line.Contains("TRAIN_RECEIPT_STALE", StringComparison.Ordinal));
        Assert.All(fixture.Goals, goal =>
        {
            var pending = Assert.Single(Receipts(fixture, goal));
            Assert.Equal("pending", pending.Outcome);
            Assert.Equal("disjoint", pending.Reason);
            Assert.Equal(fixture.Source.ReceiptId, pending.SourceReceiptId);
            Assert.Equal(current, pending.CurrentMainSha);
        });
        Assert.Equal(1, fixture.Verifier.RunCount); // Width occupant prevents pair-cohort admission.
        Assert.All(fixture.Goals, goal => Assert.NotEqual(GoalStatus.Completed, goal.Status));

        // Two members use pair-cohort admission in production. Drive the ordinary train gate
        // explicitly while capacity remains occupied, then let the next tick land its receipt.
        var gate = fixture.Driver.RunMergeTrain(Selection(fixture.Driver, fixture.Goals), fixture.Goals,
            ConductorAutonomyPolicy.Permissive, gateOnly: true);
        Assert.Equal(2, fixture.Verifier.RunCount);
        var newReceipt = Assert.IsType<MergeTrainReceipt>(gate.RecordedReceipt);
        Assert.Equal(MergeTrainGateOutcome.Passed, newReceipt.Outcome);
        Assert.NotEqual(fixture.Source.ReceiptId, newReceipt.ReceiptId);
        var next = RunTick(fixture);

        Assert.Contains($"receipt={newReceipt.ReceiptId}", string.Join(" | ", next.ProgressLines ?? []));
        Assert.All(fixture.Goals, goal =>
        {
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Contains(goal.Timeline,
                entry => entry.Message == $"Landed by merge train receipt {newReceipt.ReceiptId}.");
            var landed = Assert.Single(Receipts(fixture, goal).Where(receipt => receipt.Outcome == "landed"));
            Assert.Equal(fixture.Source.ReceiptId, landed.SourceReceiptId);
            Assert.Equal(newReceipt.ReceiptId, landed.RevalidationReceiptId);
            Assert.Equal("revalidated", landed.Reason);
        });
        Assert.Equal(2, fixture.Verifier.RunCount);
    }

    [Fact]
    public void OverlappingMainAdvance_PreservesExactStaleLineAndRecordsRegate()
    {
        using var fixture = CreatePassedTrain();
        AdvanceMain(fixture, FirstPath, "first candidate");
        OccupyAcceptance(fixture);
        _ = Selection(fixture.Driver, fixture.Goals); // Both projections must stay Ready.
        RunTick(fixture);

        var conductLines = ConductDetails(fixture);
        Assert.Equal($"TRAIN_RECEIPT_STALE tick=1 train={fixture.Source.Identity.Value} receipt={fixture.Source.ReceiptId} moved=main",
            Assert.Single(conductLines.Where(line => line.StartsWith("TRAIN_RECEIPT_STALE ", StringComparison.Ordinal))));
        Assert.DoesNotContain(conductLines, line => line.Contains("TRAIN_RECEIPT_REVALIDATE", StringComparison.Ordinal));
        Assert.All(fixture.Goals, goal =>
        {
            var receipt = Assert.Single(Receipts(fixture, goal));
            Assert.Equal("regate", receipt.Outcome);
            Assert.Equal("overlap", receipt.Reason);
            Assert.Equal(fixture.Source.ReceiptId, receipt.SourceReceiptId);
            Assert.NotEqual(GoalStatus.Completed, goal.Status);
        });
        Assert.Equal(1, fixture.Verifier.RunCount);
    }

    [Fact]
    public void FailedRevalidation_NeverRecordsLandedOrCompletesMembers()
    {
        using var fixture = CreatePassedTrain(failRevalidation: true);
        AdvanceMain(fixture, RacingPath, "// racing landing");
        OccupyAcceptance(fixture);
        RunTick(fixture);
        Assert.All(fixture.Goals, goal => Assert.Equal("pending", Assert.Single(Receipts(fixture, goal)).Outcome));
        var gate = fixture.Driver.RunMergeTrain(Selection(fixture.Driver, fixture.Goals), fixture.Goals,
            ConductorAutonomyPolicy.Permissive, gateOnly: true);
        Assert.Equal(2, fixture.Verifier.RunCount);
        var receipt = Assert.IsType<MergeTrainReceipt>(gate.RecordedReceipt);
        Assert.NotEqual(fixture.Source.ReceiptId, receipt.ReceiptId);
        Assert.NotEqual(MergeTrainGateOutcome.Passed, receipt.Outcome);
        Assert.NotEqual(MergeTrainGateOutcome.Passed, fixture.Store.TryReadReceipt(receipt.Identity.Value)!.Outcome);
        RunTick(fixture);

        Assert.All(fixture.Goals, goal =>
        {
            Assert.NotEqual(GoalStatus.Completed, goal.Status);
            Assert.DoesNotContain(Receipts(fixture, goal), entry => entry.Outcome == "landed");
        });
        Assert.Equal(2, fixture.Verifier.RunCount);
    }

    [Fact]
    public void NoMainAdvance_LandsOriginalReceiptWithoutRevalidationJournal()
    {
        using var fixture = CreatePassedTrain();
        Assert.All(fixture.Source.Identity.Members, member => Assert.Equal(
            fixture.Kernel.GetGoal(member.GoalId).AuthoritativeBrief.Version, member.BriefVersion));
        var tick = RunTick(fixture);

        Assert.Contains($"receipt={fixture.Source.ReceiptId}", string.Join(" | ", tick.ProgressLines ?? []));
        Assert.All(fixture.Goals, goal =>
        {
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Contains(goal.Timeline,
                entry => entry.Message == $"Landed by merge train receipt {fixture.Source.ReceiptId}.");
            Assert.Empty(Receipts(fixture, goal));
        });
        Assert.Equal(1, fixture.Verifier.RunCount);
    }

    private static Fixture CreatePassedTrain(bool failRevalidation = false)
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
        var verifier = new SequenceAcceptanceVerifier([passed, failRevalidation
            ? FailedVerification(repo, "train-red.trx", "core tests") : passed]);
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
        Assert.Equal(1, verifier.RunCount);
        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
        return new(repo, kernel, goals, driver, verifier, store, source);
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

    private static string[] ConductDetails(Fixture fixture) =>
        File.ReadAllLines(fixture.LogPath).Select(line =>
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            return document.RootElement.GetProperty("detail").GetString()!;
        }).ToArray();

    private static AcceptanceRevalidationReceipt[] Receipts(Fixture fixture, Goal goal) =>
        GoalOperationJournal.Read(fixture.Repo, goal.Id).Entries.Where(entry => entry.Operation == AcceptanceRevalidationReceipt.Operation)
            .Select(entry =>
            {
                Assert.Equal(GoalOperationStatus.Completed, entry.Status);
                Assert.True(AcceptanceRevalidationReceipt.TryParse(entry.Detail, out var receipt));
                return receipt!;
            }).ToArray();

    private sealed record Fixture(string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals, ConductorDriver Driver,
        SequenceAcceptanceVerifier Verifier, MergeTrainAcceptanceStore Store, MergeTrainReceipt Source) : IDisposable
    {
        internal string LogPath { get; } = Path.Combine(Repo, "revalidation-conduct.jsonl");
        internal ConductorBatchLoop Loop { get; } = new(conductEventLogWriter:
            new ConductEventLogWriter(Path.Combine(Repo, "revalidation-conduct.jsonl")));
        public void Dispose() => DeleteDirectory(Repo);
    }
}
