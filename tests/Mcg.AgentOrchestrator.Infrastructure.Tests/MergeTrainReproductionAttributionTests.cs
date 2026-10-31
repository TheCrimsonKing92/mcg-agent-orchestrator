using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its repository, stores, worktrees and fake runner.
public sealed class MergeTrainReproductionAttributionTests : AcceptanceCohortWorkflowTests
{
    private const string TestName = "Ns.UnchangedTests.Fails";
    private const string FailureMessage = "member-specific reproduction failure";
    private static readonly string[] Paths =
    [
        "tests/Mcg.AgentOrchestrator.Core.Tests/OldestTests.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MiddleTests.cs",
        "tests/Mcg.AgentOrchestrator.Dashboard.Tests/NewestTests.cs"
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void IsolatedReproductionRemovesAndImplicatesTheActualMember(int guilty)
    {
        WithTrain([guilty], "complete", train =>
        {
            Assert.Equal(3, train.Calls.Count);
            Assert.Equal(2, train.Verifier.RunCount);
            var ejection = Assert.Single(train.Result.Ejections);
            Assert.Equal(train.Goals[guilty].Id, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, ejection.Reason);
            var remainder = train.Goals.Where((_, index) => index != guilty).Select(goal => goal.Id).ToArray();
            Assert.Equal(remainder, train.Result.Receipt!.Identity.Members.Select(member => member.GoalId));
            Assert.Equal(Paths.Where((_, index) => index != guilty), train.Verifier.ChangedFiles[1]);
            Assert.Equal(new[] { Key(train, guilty) }, train.Driver.ReadTrainImplicatedMemberKeys());
            var record = Assert.Single(AttributionEvents(train.Repo));
            Assert.Equal(train.Goals[guilty].Id.Value, record.GoalId);
            Assert.Contains("attribution=reproduction", record.Detail);
            Assert.Contains(TestName, record.Detail);
            AssertFailureDetails(train.Goals[guilty]);
            AssertNextSelection(train, [guilty]);
            Assert.All(train.Calls, call =>
            {
                Assert.Equal(train.Selection.Members[0].MainRevision, call.MainRevision);
                Assert.Equal(new[] { TestName }, call.Tests);
                Assert.Equal(train.Selection.Members.Single(member => member.GoalId == call.Member.GoalId).CandidateRevision,
                    call.Member.CandidateRevision);
            });
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryReproducingMemberIsRemovedWithoutLaunchingAnUndersizedTrain(bool all)
    {
        int[] guilty = all ? [0, 1, 2] : [0, 1];
        WithTrain(guilty, "complete", train =>
        {
            Assert.Equal(3, train.Calls.Count);
            Assert.Equal(1, train.Verifier.RunCount);
            Assert.Null(train.Result.Receipt);
            Assert.Equal(guilty.Select(index => train.Goals[index].Id), train.Result.Ejections.Select(item => item.GoalId));
            Assert.All(train.Result.Ejections, item => Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, item.Reason));
            Assert.Equal(guilty.Select(index => Key(train, index)).Order(StringComparer.Ordinal),
                train.Driver.ReadTrainImplicatedMemberKeys().Order(StringComparer.Ordinal));
            Assert.All(guilty, index => AssertFailureDetails(train.Goals[index]));
            AssertNextSelection(train, guilty);
        });
    }

    [Theory]
    [InlineData("passing")]
    [InlineData("unreadable")]
    [InlineData("incomplete")]
    [InlineData("skipped")]
    [InlineData("exception")]
    [InlineData("unrelated")]
    [InlineData("absent")]
    public void UnusableOrNonReproducingEvidenceKeepsNewestSplit(string mode)
    {
        WithTrain(mode is "passing" or "unrelated" ? [] : [0], mode, train =>
        {
            var ejection = Assert.Single(train.Result.Ejections);
            Assert.Equal(train.Goals[2].Id, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedNewestMember, ejection.Reason);
            Assert.Equal(2, train.Verifier.RunCount);
            Assert.Empty(AttributionEvents(train.Repo));
            Assert.DoesNotContain(Key(train, 0), train.Driver.ReadTrainImplicatedMemberKeys());
            Assert.DoesNotContain(Key(train, 1), train.Driver.ReadTrainImplicatedMemberKeys());
            Assert.All(train.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            Assert.All(train.Goals, goal => Assert.Null(goal.LatestAcceptanceFailure));
            Assert.Equal(mode == "absent" ? 0 : mode is "unreadable" or "incomplete" or "skipped" or "exception" ? 2 : 3,
                train.Calls.Count);
        });
    }

    [Theory]
    [InlineData("guard")]
    [InlineData("not-red")]
    [InlineData("owned")]
    public void GuardNonRedAndPreviouslyAttributedReceiptsNeverInvokeRunner(string mode)
    {
        WithTrain([0], mode, train => Assert.Empty(train.Calls));
    }

    [Fact]
    public void CancellationPropagatesWithoutPublishingPartialImplications()
    {
        WithTrain([0], "complete", train =>
        {
            var calls = 0;
            Assert.Throws<OperationCanceledException>(() => MergeTrainReproductionAttribution.TryAttribute(
                train.FailedReceipt, train.FailedReceipt.Identity.Members, (member, main, tests, _) =>
                {
                    if (++calls == 2) throw new OperationCanceledException();
                    return Result(member, main, tests, [new(TestName, "Failed", FailureMessage, null)]);
                }, CancellationToken.None));
            Assert.Equal(2, calls);
        });
    }

    [Fact]
    public void ParentReconciliationPreservesChildReproductionNamesAndMessages()
    {
        WithTrain([0], "complete", train =>
        {
            Assert.All(train.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            var changed = train.Driver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive);
            Assert.Equal(train.Goals[0].Id, Assert.Single(changed));
            AssertFailureDetails(train.Goals[0]);
            Assert.All(train.Goals.Skip(1), goal => Assert.Null(goal.LatestAcceptanceFailure));
            Assert.Empty(train.Driver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive));
        }, readOnlyChild: true);
    }

    [Fact]
    public void PersistedReproductionRejectsFailuresOutsideTheConfirmedSelection()
    {
        WithTrain([0], "complete", train =>
        {
            var member = train.FailedReceipt.Identity.Members[0];
            var ejection = MergeTrainReproductionAttribution.CreateEjection(train.FailedReceipt,
                new(member, [new("Ns.UnrelatedTests.Fails", "Failed", "not the confirmed failure", null)]));
            Assert.Null(MergeTrainReproductionAttribution.ReadEjectionFailures(train.FailedReceipt, ejection.Detail));
        });
    }

    [Fact]
    public void ReproductionOnTheSecondAttemptRoutesTheMemberAndDoesNotDuplicateItsEjection()
    {
        WithTrain([0], "second-attempt", train =>
        {
            Assert.Equal(5, train.Calls.Count);
            Assert.Equal(2, train.Verifier.RunCount);
            Assert.Null(train.Result.Receipt);
            Assert.Equal(2, train.Result.Ejections.Count);
            Assert.Equal(train.Goals[2].Id, train.Result.Ejections[0].GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedNewestMember, train.Result.Ejections[0].Reason);
            Assert.Equal(train.Goals[0].Id, train.Result.Ejections[1].GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, train.Result.Ejections[1].Reason);
            AssertFailureDetails(train.Goals[0]);
            Assert.Equal(GoalStatus.Verified, train.Goals[1].Status);
        });
    }

    private static void AssertFailureDetails(Goal goal)
    {
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        var summary = Assert.IsType<AcceptanceFailureSummary>(goal.LatestAcceptanceFailure);
        Assert.All(summary.CheckAttributions!, check =>
        {
            Assert.Contains(TestName, check.Evidence);
            Assert.Contains(FailureMessage, check.Evidence);
        });
        var failure = Assert.IsType<AttributedFailure>(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(summary));
        Assert.Equal("train-reproduction", failure.Source);
        Assert.Equal(new[] { TestName }, failure.FailingTests);
        var feedback = AttributedFailureDeveloperRoute.BuildFeedback(goal, failure, "signature");
        Assert.Contains(TestName, feedback);
        Assert.Contains(FailureMessage, feedback);
    }

    private static MergeTrainReproductionAttribution.RunResult Result(MergeTrainMemberBinding member, string main,
        IReadOnlyList<string> tests, IReadOnlyList<AcceptanceTrxFailure> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "reproduction-" + member.GoalId.Value);
        return new(tests, failures, new(main, member.CandidateRevision, member.CandidateRevision,
            member.CandidateRevision, root, [root], [Path.Combine(root, "tests.dll")]));
    }

    private void WithTrain(int[] guilty, string mode, Action<Scenario> assert, bool readOnlyChild = false)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            Directory.CreateDirectory(Path.Combine(repo, "tests"));
            File.WriteAllText(Path.Combine(repo, "tests/UnchangedTests.cs"), "namespace Ns; public class UnchangedTests { }");
            RunGit(repo, "add", "tests/UnchangedTests.cs");
            RunGit(repo, "commit", "-m", "Add unchanged test source");
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3).Select(index =>
            {
                var goal = CreateCompletedGoal(kernel, $"Reproduction member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                _ = CreateWorktreeCandidate(repo, goal.Id, Paths[index],
                    $"namespace Ns; public class {Path.GetFileNameWithoutExtension(Paths[index])} {{ }}");
                return goal;
            }).ToArray();
            var identity = mode == "guard" ? "Ns.WorkflowDecisionCoverageRatchetTests.EveryUndecidedSite_IsAllowListedByFileAndMember" :
                mode == "owned" ? "Ns.OldestTests.Fails" : TestName;
            var red = Red(repo, identity);
            var passing = new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("remainder", true, 0, null)],
                TestResultPaths: [WritePassingTrx(repo, "pass.trx")]);
            var verifier = new SequenceAcceptanceVerifier(mode == "not-red" ? [passing] :
                mode == "second-attempt" ? [red, red] : [red, passing]);
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var calls = new List<Invocation>();
            if (mode != "absent") driver.MergeTrainReproductionRunner = (member, main, tests, _) =>
            {
                calls.Add(new(member, main, tests));
                if (mode == "second-attempt" && calls.Count <= 3) return Result(member, main, tests, []);
                var index = Array.FindIndex(goals, goal => goal.Id == member.GoalId);
                if (index == 1)
                {
                    if (mode == "unreadable") return null;
                    if (mode == "incomplete") return new([], []);
                    if (mode == "skipped") return new(tests, [new(TestName, "NotExecuted", "skipped", null)]);
                    if (mode == "exception") throw new IOException("unavailable fixture result");
                }
                return Result(member, main, tests, guilty.Contains(index) ? [new(TestName, "Failed", FailureMessage, null)] :
                    mode == "unrelated" ? [new("Ns.OtherTests.Fails", "Failed", "unrelated", null)] : []);
            };
            var selection = new ConductorMergeTrainSelection(goals.Select(goal =>
                Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                    driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive)).Projection).ToArray());
            var executingDriver = driver;
            var executingGoals = goals;
            if (readOnlyChild)
            {
                var workspace = OrchestratorWorkspace.ForDirectory(repo);
                new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
                var child = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                    .LoadGoalsAsync(goals.Select(goal => goal.Id).ToArray()).GetAwaiter().GetResult();
                executingGoals = goals.Select(goal => child.GetGoal(goal.Id)).ToArray();
                executingDriver = new ConductorDriver(child, workspace, verifier, AgentCatalog.Default().Agents,
                    WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks)
                { MergeTrainReproductionRunner = driver.MergeTrainReproductionRunner };
            }
            var result = executingDriver.RunMergeTrain(selection, executingGoals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            var store = new MergeTrainAcceptanceStore(Path.Combine(repo, ".orchestrator/merge-train-acceptance.db"));
            var failed = store.ReadReceiptsSince(DateTimeOffset.MinValue).FirstOrDefault(receipt => receipt.Outcome == MergeTrainGateOutcome.Failed);
            assert(new(repo, goals, driver, selection, result, verifier, calls, failed!));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    private static AcceptanceVerificationResult Red(string repo, string identity)
    {
        var path = Path.Combine(repo, "red.trx");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results",
            new XElement(ns + "UnitTestResult", new XAttribute("testName", identity), new XAttribute("outcome", "Failed"),
                new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", "train failure"))))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Failed"), new XElement(ns + "Counters",
                new XAttribute("total", 1), new XAttribute("executed", 1),
                new XAttribute("passed", 0), new XAttribute("failed", 1))))).Save(path);
        return new(false, false, 1, "train failed",
            Checks: [new AcceptanceCheckResult("train", false, 1, "train failed")], TestResultPaths: [path]);
    }

    private static string Key(Scenario train, int index) => ConductorAcceptanceCohortAttributedMembers.Key(
        train.Goals[index].Id, train.Selection.Members[index].CandidateRevision);

    private static void AssertNextSelection(Scenario train, int[] guilty)
    {
        var candidates = train.Selection.Members.Select(projection => new ConductorSpeculativeAcceptanceCandidate(
            projection.GoalId, new GateReadyCandidateProjectionResult.Ready(projection))).ToArray();
        var partners = Enumerable.Range(0, 3).Select(index =>
        {
            var character = (char)('f' - index);
            var projection = new GateReadyCandidateProjection(new GoalId(new string(character, 32)), GoalLifecycleState.Verified,
                GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
                [$"src/Partner{character}.cs"], [$"partner:{character}"], new GateReadyMergeEvidence(new string(character, 40),
                    train.Selection.Members[0].MainRevision, GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));
            return new ConductorSpeculativeAcceptanceCandidate(projection.GoalId, new GateReadyCandidateProjectionResult.Ready(projection));
        }).ToArray();
        var next = ConductorMergeTrainSelector.Select([.. candidates, .. partners],
            trainImplicatedMemberKeys: train.Driver.ReadTrainImplicatedMemberKeys());
        Assert.NotNull(next);
        Assert.All(guilty, index => Assert.DoesNotContain(next.Members, member => member.GoalId == train.Goals[index].Id));
        Assert.All(train.Goals.Where((_, index) => !guilty.Contains(index)), goal =>
            Assert.Contains(next.Members, member => member.GoalId == goal.Id));
    }

    private static ConductEventRecord[] AttributionEvents(string repo)
    {
        var path = OrchestratorWorkspace.ForDirectory(repo).ConductEventsLogPath;
        return !File.Exists(path) ? [] : File.ReadAllLines(path)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(record => record.EventKind == "train-attribution").ToArray();
    }

    private sealed record Invocation(MergeTrainMemberBinding Member, string MainRevision, IReadOnlyList<string> Tests);
    private sealed record Scenario(string Repo, Goal[] Goals, ConductorDriver Driver, ConductorMergeTrainSelection Selection,
        ConductorMergeTrainRunResult Result, SequenceAcceptanceVerifier Verifier, List<Invocation> Calls, MergeTrainReceipt FailedReceipt);
}
