using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its repository, SQLite stores and fake runners.
public sealed class MergeTrainFocusedRecheckTests : AcceptanceCohortWorkflowTests
{
    internal const string FailingTest = "Ns.CulpritTests.Fails";
    internal static readonly string[] DependencyClasses = ["Ns.DependentOneTests", "Ns.DependentTwoTests"];
    internal static readonly string[] ExpectedSelection = [FailingTest, .. DependencyClasses];
    private static readonly string[] Paths = ["src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
        "src/Mcg.AgentOrchestrator.Infrastructure/Second.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CulpritTests.cs"];
    private const string SharedPath = "src/Mcg.AgentOrchestrator.Infrastructure/Shared.cs";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedRed_CompleteRecheck_LandsRemainderWithoutSecondGate(bool gateOnly)
    {
        WithTrain("green", gateOnly, train =>
        {
            var call = Assert.Single(train.Calls);
            Assert.Equal(ExpectedSelection, call.Selection.Identities);
            Assert.Equal(string.Join("; ", ExpectedSelection.Select(name => $"FullyQualifiedName~{name}")),
                call.Selection.Request);
            Assert.Equal(new[] { Paths[0], Paths[1] }, call.ChangedFiles);
            Assert.False(call.CulpritFileExists);
            Assert.Equal(train.Red.Identity.ObservedMainRevision, call.Identity.ObservedMainRevision);
            Assert.Equal(call.Identity.TrainTreeRevision, call.Tree);
            Assert.Equal(new[] { Paths[2] }, Assert.Single(train.DependencyCalls));
            Assert.Equal(1, train.FullGateCount());
            var receipt = Assert.IsType<MergeTrainReceipt>(train.Result.Receipt);
            var proof = Assert.IsType<MergeTrainRecheckProvenance>(receipt.Recheck);
            Assert.Equal(train.Red.ReceiptId, proof.RedReceiptId);
            Assert.Equal(train.Red.Identity.Value, proof.RedTrainId);
            Assert.Equal(train.Red.GateAttemptId, proof.TrainAttemptId);
            Assert.Equal(train.Goals[2].Id, proof.CulpritGoalId);
            Assert.Equal("ownership", proof.AttributionSource);
            Assert.Equal("fixture-recheck", proof.RecheckAttemptId);
            Assert.Equal(ExpectedSelection, proof.Selection);
            Assert.True(proof.DependencySelectionResolved);
            Assert.True(proof.LandingPathsDisjoint);
            Assert.Equal(DependencyClasses, proof.DependencyClasses);
            Assert.Equal(train.Goals.Take(2).Select(goal => goal.Id), receipt.Identity.Members.Select(member => member.GoalId));
            Assert.True(receipt.HasAuthoritativeLandingEvidence);
            Assert.True(MergeTrainFocusedRecheckReceiptAcceptance.Accepts(receipt, train.Store));
            var checkEvidence = Assert.Single(train.Red.FailedCheckEvidence!);
            Assert.Equal("train", checkEvidence.CheckName);
            Assert.True(checkEvidence.ExplainedByFatalTrx);
            Assert.Equal(new[] { FailingTest }, checkEvidence.FatalTestIdentities);
            Assert.Equal(train.Goals[2].Id, Assert.Single(train.Result.Ejections).GoalId);
            Assert.Equal(GoalStatus.AcceptanceFailed, train.Goals[2].Status);
            Assert.NotNull(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(train.Goals[2].LatestAcceptanceFailure!));

            if (gateOnly)
            {
                Assert.Equal(train.Red.Identity.ObservedMainRevision, RunGitOutput(train.Repo, "rev-parse", "main").Trim());
                Assert.All(train.Goals.Take(2), goal => Assert.Equal(GoalStatus.Verified, goal.Status));
                // Replay through a fresh driver, proving persisted provenance rather than in-memory state.
                var replayDriver = new ConductorDriver(train.Kernel, OrchestratorWorkspace.ForDirectory(train.Repo),
                    new SequenceAcceptanceVerifier([]), AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                    cleanupHooks: CreateIsolatedCleanupContext(train.Repo).Hooks);
                var replay = replayDriver.RunMergeTrain(new(train.Selection.Members.Take(2).ToArray()),
                    train.Goals.Take(2).ToArray(), ConductorAutonomyPolicy.Permissive, landFromReceiptOnly: true);
                Assert.Equal(receipt.ReceiptId, replay.Receipt!.ReceiptId);
            }

            Assert.All(train.Goals.Take(2), goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            Assert.Equal(receipt.Identity.TrainTreeRevision, RunGitOutput(train.Repo, "rev-parse", "main^{tree}").Trim());
            Assert.Equal(receipt.Identity.Value, Assert.Single(train.Store.ReadFinalizedLandingTrainIds()));
            Assert.All(train.Goals.Take(2), goal =>
                Assert.Equal(receipt.ReceiptId, Assert.Single(train.Store.ReadFinalizedLandingsForGoal(goal.Id)).ReceiptId));
            Assert.Single(train.Calls);
            Assert.Equal(1, train.FullGateCount());
            AssertDecision(train, "landed", "passed", 3);
        });
    }

    [Theory]
    [InlineData("abandoned", "dependency-abandoned", 0, 0)]
    [InlineData("overlap", "overlapping-paths", 0, 0)]
    [InlineData("failed", "recheck-failed", 1, 3)]
    [InlineData("not-run", "not-run-selections", 1, 3)]
    [InlineData("unattributed", "no-attribution", 0, 0)]
    [InlineData("bounds", "focused-bounds", 0, 0)]
    [InlineData("missing-coverage", "not-run-selections", 1, 3)]
    [InlineData("malformed", "invalid-evidence", 1, 3)]
    [InlineData("unexplained-command", "invalid-evidence", 0, 0)]
    [InlineData("unexplained-test", "invalid-evidence", 0, 0)]
    public void IneligibleRecheck_RunsSecondFullGateWithReason(
        string mode, string reason, int focusedCalls, int selectionCount)
    {
        WithTrain(mode, true, train =>
        {
            Assert.Equal(train.Goals.Select(goal => goal.Id), train.Red.Identity.Members.Select(member => member.GoalId));
            Assert.Equal(2, train.FullGateCount());
            Assert.Equal(focusedCalls, train.Calls.Count);
            var receipt = Assert.IsType<MergeTrainReceipt>(train.Result.Receipt);
            Assert.Null(receipt.Recheck);
            Assert.DoesNotContain(train.Store.ReadReceiptsSince(DateTimeOffset.MinValue), item => item.Recheck is not null);
            Assert.Equal(train.Goals.Take(2).Select(goal => goal.Id), receipt.Identity.Members.Select(member => member.GoalId));
            var dropped = Assert.Single(train.Result.Ejections);
            Assert.Equal(train.Goals[2].Id, dropped.GoalId);
            Assert.Equal(mode == "unattributed" ? MergeTrainEjectionReason.RedNewestMember :
                MergeTrainEjectionReason.RedAttributedMember, dropped.Reason);
            AssertDecision(train, "fallback", reason, selectionCount);
            if (mode is "unexplained-command" or "unexplained-test")
            {
                Assert.Empty(train.DependencyCalls);
                Assert.Equal(new[] { "train", "unexplained" }, train.Red.FailedChecks);
                var evidence = Assert.IsAssignableFrom<IReadOnlyList<MergeTrainFailedCheckEvidence>>(
                    train.Red.FailedCheckEvidence);
                Assert.Equal(2, evidence.Count);
                Assert.True(evidence[0].ExplainedByFatalTrx);
                Assert.False(evidence[1].ExplainedByFatalTrx);
                Assert.Empty(evidence[1].FatalTestIdentities);
            }
        });
    }

    private static void AssertDecision(Scenario train, string outcome, string reason, int count)
    {
        var events = File.ReadAllLines(OrchestratorWorkspace.ForDirectory(train.Repo).ConductEventsLogPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(item => item.EventKind == "train-focused-recheck").ToArray();
        Assert.Equal($"MERGE_TRAIN_FOCUSED_RECHECK outcome={outcome} reason={reason} recheck={count} " +
            $"culprit={train.Goals[2].Id.Value[..8]}", Assert.Single(events).Detail);
    }

    internal static void WithTrain(string mode, bool gateOnly, Action<Scenario> assert)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            var sharedLines = Enumerable.Range(0, 40).Select(index => $"// line {index}").ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, SharedPath))!);
            // Match the candidate line endings so A and C edit separate lines, not the whole file.
            File.WriteAllText(Path.Combine(repo, SharedPath), string.Join('\n', sharedLines) + "\n");
            Directory.CreateDirectory(Path.Combine(repo, "tests"));
            File.WriteAllText(Path.Combine(repo, "tests/UnownedTests.cs"), "namespace Ns; public class UnownedTests { }");
            RunGit(repo, "add", SharedPath, "tests/UnownedTests.cs");
            RunGit(repo, "commit", "-m", "Seed independent and shared source");
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3).Select(index =>
            {
                var goal = CreateCompletedGoal(kernel, $"Focused recheck member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                if (mode == "overlap" && index == 0)
                {
                    var lines = sharedLines.ToArray();
                    lines[0] = "// A's change";
                    CreateWorktreeCandidate(repo, goal.Id, SharedPath, string.Join('\n', lines) + "\n");
                }
                else
                    CreateWorktreeCandidate(repo, goal.Id, Paths[index],
                        index == 2 ? "namespace Ns; public class CulpritTests { }" : $"public class Member{index} {{ }}");
                if (mode == "overlap" && index == 2)
                {
                    var lines = sharedLines.ToArray();
                    lines[^1] = "// C's change";
                    CreateWorktreeCandidate(repo, goal.Id, SharedPath, string.Join('\n', lines) + "\n");
                }
                return goal;
            }).ToArray();
            var failing = mode == "unattributed" ? "Ns.UnownedTests.Fails" : FailingTest;
            var redTrx = WriteTrx(repo, "red.trx", [(failing, "Failed"), ("Ns.UnaffectedTests.Passes", "Passed")]);
            var checks = new List<AcceptanceCheckResult>
            {
                new("train", false, 1, "confirmed failure", TestResultPaths: [redTrx])
            };
            var redPaths = new List<string> { redTrx };
            if (mode is "unexplained-command" or "unexplained-test")
            {
                var checkPaths = mode == "unexplained-test" ?
                    new[] { WritePassingTrx(repo, "unexplained.trx") } : Array.Empty<string>();
                checks.Add(new("unexplained", false, 1, "failure without fatal TRX rows", TestResultPaths: checkPaths));
                redPaths.AddRange(checkPaths);
            }
            var red = new AcceptanceVerificationResult(false, false, 1, "confirmed failure",
                Checks: checks, TestResultPaths: redPaths);
            var green = new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new("remainder", true, 0, null)], TestResultPaths: [WritePassingTrx(repo, "full-green.trx")]);
            var verifier = new SequenceAcceptanceVerifier([red, green]);
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var calls = new List<Invocation>();
            var dependencyCalls = new List<IReadOnlyList<string>>();
            string? trainCommit = null;
            driver.MergeTrainReproductionRunner = (_, _, _, _) => null;
            driver.MergeTrainRecheckDependencySelector = (_, paths) =>
            {
                dependencyCalls.Add(paths.ToArray());
                return new(mode != "abandoned", mode == "bounds" ?
                    Enumerable.Range(0, 97).Select(index => $"Ns.Dependent{index}Tests").ToArray() : DependencyClasses);
            };
            driver.MergeTrainRecheckRunner = (path, identity, selection, _, _) =>
            {
                trainCommit = RunGitOutput(path, "rev-parse", "HEAD").Trim();
                var tree = RunGitOutput(path, "rev-parse", "HEAD^{tree}").Trim();
                var changes = RunGitOutput(path, "diff", "--name-only", identity.ObservedMainRevision, "HEAD")
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
                calls.Add(new(identity, selection, changes, tree, File.Exists(Path.Combine(path, Paths[2]))));
                var names = selection.Identities.Select(name => name == FailingTest ? name : name + ".Passes");
                if (mode == "missing-coverage") names = names.Take(2);
                var trx = WriteTrx(repo, "focused.trx", names.Select((name, index) =>
                    (name, mode == "failed" && index == 0 ? "Failed" : "Passed")).ToArray());
                if (mode == "malformed") File.WriteAllText(trx, "not xml");
                return new("fixture-recheck", identity.ObservedMainRevision,
                    new(ConductorCohortFocusedPassKind.Executed, mode == "failed" ? [FailingTest] : [],
                        mode == "failed" ? ["focused"] : [], selection.Identities.Count,
                        mode != "failed", [trx], tree), mode == "not-run" ? 1 : 0);
            };
            var selection = new ConductorMergeTrainSelection(goals.Select(goal =>
            {
                var projection = driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive);
                Assert.True(projection is GateReadyCandidateProjectionResult.Ready,
                    $"Expected Ready projection for {goal.Id.Value[..8]}, but got {projection}.");
                return Assert.IsType<GateReadyCandidateProjectionResult.Ready>(projection).Projection;
            }).ToArray());
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive, gateOnly: gateOnly);
            var store = new MergeTrainAcceptanceStore(Path.Combine(repo, ".orchestrator/merge-train-acceptance.db"));
            var failed = Assert.Single(store.ReadReceiptsSince(DateTimeOffset.MinValue)
                .Where(receipt => receipt.Outcome == MergeTrainGateOutcome.Failed));
            assert(new(repo, kernel, goals, driver, selection, result, store, failed, () => verifier.RunCount,
                calls, dependencyCalls, trainCommit));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    internal static string WriteTrx(string repo, string name, (string Name, string Outcome)[] tests)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var path = Path.Combine(repo, name);
        var failed = tests.Count(test => test.Outcome == "Failed");
        new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", tests.Select((test, index) =>
            new XElement(ns + "UnitTestResult", new XAttribute("testId", index + 1),
                new XAttribute("testName", test.Name), new XAttribute("outcome", test.Outcome)))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", failed > 0 ? "Failed" : "Completed"),
                new XElement(ns + "Counters", new XAttribute("total", tests.Length),
                    new XAttribute("executed", tests.Length), new XAttribute("passed", tests.Length - failed),
                    new XAttribute("failed", failed))))).Save(path);
        return path;
    }

    internal sealed record Invocation(MergeTrainIdentity Identity, ConductorCohortFocusedSelection Selection,
        IReadOnlyList<string> ChangedFiles, string Tree, bool CulpritFileExists);
    internal sealed record Scenario(string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals,
        ConductorDriver Driver, ConductorMergeTrainSelection Selection, ConductorMergeTrainRunResult Result,
        MergeTrainAcceptanceStore Store, MergeTrainReceipt Red, Func<int> FullGateCount,
        List<Invocation> Calls, List<IReadOnlyList<string>> DependencyCalls, string? TrainCommit);
}
