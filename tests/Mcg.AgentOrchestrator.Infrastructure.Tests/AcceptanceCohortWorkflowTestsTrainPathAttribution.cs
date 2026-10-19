using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its git repository, worktrees, stores and cleanup hooks.
public sealed class AcceptanceCohortWorkflowTestsTrainPathAttribution : AcceptanceCohortWorkflowTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Paths =
    [
        "tests/Mcg.AgentOrchestrator.Core.Tests/OldestCulpritTests.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OtherMemberTests.cs",
        "tests/Mcg.AgentOrchestrator.Dashboard.Tests/NewestMemberTests.cs"
    ];

    [Fact]
    public void UnchangedFailureNamingMiddleMember_EjectsAndRoutesThatMember()
    {
        WithTrain("Ns.UnchangedTests.Fails", $"Unproven occupancy identity: Ns.X at {Paths[1]}:70: x.", train =>
        {
            AssertPassingRemainder(train, 1, MergeTrainEjectionReason.RedAttributedMember);
            var culprit = train.Kernel.GetGoal(train.Goals[1].Id);
            Assert.Equal(GoalStatus.AcceptanceFailed, culprit.Status);
            var failure = Assert.IsType<AcceptanceFailureSummary>(culprit.LatestAcceptanceFailure);
            Assert.Equal(train.Selection.Members[1].CandidateRevision, failure.BranchHeadSha);
            var check = Assert.Single(failure.CheckAttributions!);
            Assert.StartsWith("merge train RED attributed (path) receipt ", check.Evidence);
            Assert.Equal("train-path", AttributedFailureDeveloperRoute.ReadAcceptanceFailure(failure)?.Source);
            Assert.All(train.Goals.Where((_, index) => index != 1), goal =>
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.Null(goal.LatestAcceptanceFailure);
            });
            var events = AttributionEvents(train.Repo);
            Assert.NotEmpty(events);
            Assert.All(events, record =>
            {
                Assert.Equal(culprit.Id.Value, record.GoalId);
                Assert.Contains("TRAIN_IMPLICATED", record.Detail);
                Assert.Contains("attribution=path", record.Detail);
                Assert.Contains("subjects=Ns.UnchangedTests@" + Paths[1], record.Detail);
            });
        });
    }

    [Fact]
    public void OwnedFailureNamingAnotherMember_PreservesOwnershipPrecedence()
    {
        WithTrain("Ns.OldestCulpritTests.Fails", $"Unproven occupancy identity: Ns.X at {Paths[1]}:70: x.", train =>
        {
            AssertPassingRemainder(train, 0, MergeTrainEjectionReason.RedAttributedMember);
            Assert.Equal(GoalStatus.AcceptanceFailed, train.Goals[0].Status);
            Assert.Equal(GoalStatus.Verified, train.Goals[1].Status);
            Assert.Null(train.Goals[1].LatestAcceptanceFailure);
            var events = AttributionEvents(train.Repo);
            Assert.NotEmpty(events);
            Assert.All(events, record =>
            {
                Assert.Equal(train.Goals[0].Id.Value, record.GoalId);
                Assert.Contains("attribution=ownership", record.Detail);
                Assert.DoesNotContain("attribution=path", record.Detail);
            });
        });
    }

    [Fact]
    public void SameFailureWithoutPath_PreservesNewestDropAndPassingRemainder()
    {
        WithTrain("Ns.UnchangedTests.Fails", "Unproven occupancy identity: Ns.X at nowhere: x.", train =>
        {
            AssertPassingRemainder(train, 2, MergeTrainEjectionReason.RedNewestMember);
            Assert.Empty(AttributionEvents(train.Repo));
            Assert.All(train.Goals, goal =>
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.Null(goal.LatestAcceptanceFailure);
            });
        });
    }

    private static void AssertPassingRemainder(Scenario train, int culprit, MergeTrainEjectionReason reason)
    {
        Assert.Equal(2, train.Verifier.RunCount);
        Assert.Equal(Paths, train.Verifier.ChangedFiles[0]);
        var ejection = Assert.Single(train.Result.Ejections);
        Assert.Equal(train.Goals[culprit].Id, ejection.GoalId);
        Assert.Equal(reason, ejection.Reason);
        var remaining = train.Goals.Where((_, index) => index != culprit).Select(goal => goal.Id).ToArray();
        var receipt = Assert.IsType<MergeTrainReceipt>(train.Result.Receipt);
        Assert.Equal(MergeTrainGateOutcome.Passed, receipt.Outcome);
        Assert.Equal(remaining, receipt.Identity.Members.Select(member => member.GoalId));
        Assert.Equal(Paths.Where((_, index) => index != culprit), train.Verifier.ChangedFiles[1]);
        Assert.Equal(remaining.Select(id => id.Value).Order(), train.Result.MemberResults.Keys.Order());
        var store = new MergeTrainAcceptanceStore(Path.Combine(train.Repo, ".orchestrator", "merge-train-acceptance.db"));
        var receipts = store.ReadReceiptsSince(DateTimeOffset.MinValue);
        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, stored => Assert.Equal(FixedUtcNow, stored.CompletedAt));
        var failed = Assert.Single(receipts, stored => stored.Outcome == MergeTrainGateOutcome.Failed);
        Assert.Equal(train.Goals.Select(goal => goal.Id), failed.Identity.Members.Select(member => member.GoalId));
        var passed = Assert.Single(receipts, stored => stored.Outcome == MergeTrainGateOutcome.Passed);
        Assert.Equal(receipt.ReceiptId, passed.ReceiptId);
        Assert.Equal(remaining, passed.Identity.Members.Select(member => member.GoalId));
    }

    private void WithTrain(string identity, string message, Action<Scenario> assert)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            const string unchanged = "tests/UnchangedTests.cs";
            Directory.CreateDirectory(Path.Combine(repo, "tests"));
            File.WriteAllText(Path.Combine(repo, unchanged), "namespace Ns; public class UnchangedTests { }");
            RunGit(repo, "add", unchanged);
            RunGit(repo, "commit", "-m", "Add unchanged test source");
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3).Select(index =>
            {
                var goal = CreateCompletedGoal(kernel, $"Path train member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                _ = CreateWorktreeCandidate(repo, goal.Id, Paths[index],
                    $"namespace Ns; public class {Path.GetFileNameWithoutExtension(Paths[index])} {{ }}");
                return goal;
            }).ToArray();
            var verifier = new SequenceAcceptanceVerifier([Red(repo, identity, message), Passing(repo)]);
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks,
                utcNow: () => FixedUtcNow);
            var selection = new ConductorMergeTrainSelection(goals.Select(goal =>
                Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                    driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive)).Projection).ToArray());
            Assert.Equal(goals.Select(goal => goal.Id), selection.Members.Select(member => member.GoalId));
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            assert(new Scenario(repo, kernel, goals, selection, result, verifier));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    private static AcceptanceVerificationResult Red(string repo, string identity, string message)
    {
        var path = Path.Combine(repo, "red.trx");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results",
            new XElement(ns + "UnitTestResult", new XAttribute("testName", identity), new XAttribute("outcome", "Failed"),
                new XElement(ns + "Output", new XElement(ns + "ErrorInfo",
                    new XElement(ns + "Message", message), new XElement(ns + "StackTrace", ""))))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Failed"), new XElement(ns + "Counters",
                new XAttribute("total", 1), new XAttribute("executed", 1),
                new XAttribute("passed", 0), new XAttribute("failed", 1))))).Save(path);
        var read = AcceptanceTrxFailureReader.Read(path);
        Assert.Equal(AcceptanceTrxReadStatus.Readable, read.Status);
        var failure = Assert.Single(read.Failures);
        Assert.Equal(identity, failure.TestName);
        Assert.Equal(message, failure.Message);
        Assert.True(AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome));
        Assert.Null(ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace));
        var result = new AcceptanceVerificationResult(false, false, 1, "train failed",
            Checks: [new AcceptanceCheckResult("train", false, 1, "train failed")], TestResultPaths: [path]);
        Assert.Equal(AcceptanceCohortGateOutcome.Failed, ConductorDriver.ClassifyCohortVerification(result));
        return result;
    }

    private static AcceptanceVerificationResult Passing(string repo) => new(true, false, 0, null,
        Checks: [new AcceptanceCheckResult("remainder", true, 0, null)], TestResultPaths: [WritePassingTrx(repo, "pass.trx")]);

    private static ConductEventRecord[] AttributionEvents(string repo)
    {
        var path = OrchestratorWorkspace.ForDirectory(repo).ConductEventsLogPath;
        return !File.Exists(path) ? [] : File.ReadAllLines(path)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(record => record.EventKind == "train-attribution").ToArray();
    }

    private sealed record Scenario(string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals,
        ConductorMergeTrainSelection Selection, ConductorMergeTrainRunResult Result, SequenceAcceptanceVerifier Verifier);
}
