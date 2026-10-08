using System.Xml.Linq;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its git repository, worktrees, database and cleanup hooks; no timers or shared state.
public sealed class AcceptanceCohortWorkflowTestsTrainRedAttribution : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GuardSubjects_ImplicateRevisionBeforeRemainderAndEmitOneEvent(bool remainderPasses)
    {
        WithTrain(repo => [GuardRed(repo, "guards.trx", "Ns.OldestCulpritTests", "OldestCulpritTests.cs"),
            remainderPasses ? Passing(repo) : GuardRed(repo, "retry.trx", "Ns.MissingTests", "Missing.cs")], scenario =>
        {
            Assert.Equal(2, scenario.Verifier.RunCount);
            AssertAttributedDrop(scenario, 0);
            var keys = scenario.Driver.ReadTrainImplicatedMemberKeys();
            Assert.Equal(Key(scenario, 0), Assert.Single(keys));
            Assert.Empty(scenario.Driver.ReadSuppressedGroupedPairs());
            var candidates = scenario.Selection.Members.Select(Candidate).ToArray();
            Assert.NotNull(ConductorMergeTrainSelector.Select(candidates));
            Assert.Null(ConductorMergeTrainSelector.Select(candidates, trainImplicatedMemberKeys: keys));
            foreach (var pair in new[] { candidates.Take(2).ToArray(), candidates.Take(2).Reverse().ToArray() })
            {
                Assert.NotNull(ConductorAcceptanceCohortSelector.Select(pair).Selection);
                Assert.Null(ConductorAcceptanceCohortSelector.Select(pair, trainImplicatedMemberKeys: keys).Selection);
            }

            var attribution = Assert.Single(AttributionEvents(scenario));
            Assert.Equal(scenario.Goals[0].Id.Value, attribution.GoalId);
            var trainId = attribution.Detail.Split(' ').Single(token => token.StartsWith("train=", StringComparison.Ordinal))[6..];
            var store = new MergeTrainAcceptanceStore(Path.Combine(scenario.Repo, ".orchestrator", "merge-train-acceptance.db"));
            var failed = Assert.IsType<MergeTrainReceipt>(store.TryReadReceipt(trainId));
            Assert.Equal(MergeTrainGateOutcome.Failed, failed.Outcome);
            Assert.Equal(scenario.Goals.Select(goal => goal.Id), failed.Identity.Members.Select(member => member.GoalId));
            Assert.Equal(trainId, failed.Identity.Value);
            Assert.Contains($"member={scenario.Goals[0].Id.Value}", attribution.Detail, StringComparison.Ordinal);
            Assert.Contains($"candidate_revision={scenario.Selection.Members[0].CandidateRevision}", attribution.Detail, StringComparison.Ordinal);
            Assert.Contains("subjects=Ns.OldestCulpritTests,OldestCulpritTests.cs", attribution.Detail, StringComparison.Ordinal);

            var old = scenario.Selection.Members[0];
            _ = CreateWorktreeCandidate(scenario.Repo, old.GoalId, old.LandingPaths[0],
                "namespace Ns; public class OldestCulpritTests { } // revised");
            var moved = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                scenario.Driver.ProjectGateReadyCandidate(scenario.Goals[0], ConductorAutonomyPolicy.Permissive)).Projection;
            Assert.NotEqual(old.CandidateRevision, moved.CandidateRevision);
            candidates[0] = Candidate(moved);
            Assert.Equal(moved.GoalId, ConductorMergeTrainSelector.Select(candidates,
                trainImplicatedMemberKeys: keys)!.Members[0].GoalId);
            Assert.Contains(ConductorAcceptanceCohortSelector.Select(candidates.Take(2).ToArray(),
                trainImplicatedMemberKeys: keys).Selection!.Members, member => member.GoalId == moved.GoalId);
        });
    }

    [Theory]
    [InlineData("missing", 2)]
    [InlineData("shared", 2)]
    [InlineData("different-members", 2)]
    [InlineData("unreadable", 2)]
    [InlineData("missing", 3)]
    [InlineData("shared", 3)]
    [InlineData("different-members", 3)]
    [InlineData("unreadable", 3)]
    public void GuardSubjects_AmbiguityRecordsNoImplicationSuppressionOrEvent(string reason, int memberCount)
    {
        WithTrain(repo =>
        {
            var red = GuardRed(repo, "guards.trx", reason switch
            {
                "missing" => "Ns.MissingTests",
                "shared" => "Ns.SharedTests",
                _ => "Ns.OldestCulpritTests"
            }, reason == "different-members" ? "OtherMemberTests.cs" : "OldestCulpritTests.cs");
            if (reason == "unreadable")
            {
                // Capture can copy malformed evidence, but the classifier refuses RED before bisection.
                var malformed = Path.Combine(repo, "malformed.trx");
                File.WriteAllText(malformed, "<TestRun");
                Assert.Equal(AcceptanceTrxReadStatus.Unparseable, AcceptanceTrxFailureReader.Read(malformed).Status);
                red = red with { TestResultPaths = [.. red.TestResultPaths!, malformed] };
                Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, ConductorDriver.ClassifyCohortVerification(red));
            }
            return memberCount == 2 || reason == "unreadable" ? [red] : [red, Passing(repo)];
        }, scenario =>
        {
            Assert.Equal(memberCount == 2 || reason == "unreadable" ? 1 : 2, scenario.Verifier.RunCount);
            Assert.Empty(scenario.Driver.ReadTrainImplicatedMemberKeys());
            Assert.Empty(scenario.Driver.ReadSuppressedGroupedPairs());
            Assert.Empty(AttributionEvents(scenario));
            if (reason == "unreadable")
            {
                Assert.Equal(MergeTrainGateOutcome.InfrastructureFailure,
                    Assert.IsType<MergeTrainReceipt>(scenario.Result.RecordedReceipt).Outcome);
                Assert.Empty(scenario.Result.Ejections);
            }
            else if (memberCount == 3)
                Assert.Equal(MergeTrainEjectionReason.RedNewestMember, Assert.Single(scenario.Result.Ejections).Reason);
        }, memberCount: memberCount);
    }

    private static ConductEventRecord[] AttributionEvents(Scenario scenario)
    {
        var log = OrchestratorWorkspace.ForDirectory(scenario.Repo).ConductEventsLogPath;
        return File.Exists(log) ? File.ReadAllLines(log)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(record => record.EventKind == "train-attribution").ToArray() : [];
    }

    private static ConductorSpeculativeAcceptanceCandidate Candidate(GateReadyCandidateProjection projection) =>
        new(projection.GoalId, new GateReadyCandidateProjectionResult.Ready(projection));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AttributedRed_PassingRemainderLandsTheOtherTwoMembers(int culprit)
    {
        WithTrain(repo => [Red(repo, "red.trx", Identity(culprit)), Passing(repo)], scenario =>
        {
            Assert.Equal(2, scenario.Verifier.RunCount);
            Assert.Equal(Paths, scenario.Verifier.ChangedFiles[0]);
            var expectedMembers = scenario.Selection.Members.Where((_, index) => index != culprit).ToArray();
            Assert.Equal(expectedMembers.SelectMany(member => member.LandingPaths), scenario.Verifier.ChangedFiles[1]);
            AssertAttributedDrop(scenario, culprit);
            var receipt = Assert.IsType<MergeTrainReceipt>(scenario.Result.Receipt);
            Assert.Equal(MergeTrainGateOutcome.Passed, receipt.Outcome);
            Assert.Equal(expectedMembers.Select(member => member.GoalId), receipt.Identity.Members.Select(member => member.GoalId));
            Assert.Equal(expectedMembers.Select(member => member.GoalId.Value).Order(), scenario.Result.MemberResults.Keys.Order());
            Assert.Equal(Key(scenario, culprit), Assert.Single(scenario.Driver.ReadTrainImplicatedMemberKeys()));
            Assert.Empty(scenario.Driver.ReadSuppressedGroupedPairs());
            AssertLandedFiles(scenario, culprit);
        }, gateOnly: false);
    }

    [Fact]
    public void GateOnlyThenReceiptReplay_EjectsTheSameOldestMemberAndLandsRemainder()
    {
        WithTrain(repo => [Red(repo, "red.trx", Identity(0)), Passing(repo)], scenario =>
        {
            AssertAttributedDrop(scenario, 0);
            Assert.Empty(scenario.Result.MemberResults);
            Assert.Equal(2, scenario.Verifier.RunCount);
            var passed = Assert.IsType<MergeTrainReceipt>(scenario.Result.Receipt);
            Assert.Equal(MergeTrainGateOutcome.Passed, passed.Outcome);
            Assert.Equal(scenario.Goals.Skip(1).Select(goal => goal.Id), passed.Identity.Members.Select(member => member.GoalId));
            Assert.Equal(Paths.Skip(1), scenario.Verifier.ChangedFiles[1]);
            Assert.Equal(Key(scenario, 0), Assert.Single(scenario.Driver.ReadTrainImplicatedMemberKeys()));
            Assert.All(Paths, path => Assert.False(File.Exists(Path.Combine(scenario.Repo, path))));

            var replay = scenario.Driver.RunMergeTrain(scenario.Selection, scenario.Goals,
                ConductorAutonomyPolicy.Permissive, landFromReceiptOnly: true);

            Assert.Equal(2, scenario.Verifier.RunCount);
            Assert.DoesNotContain("outcome=replay-miss", replay.Detail);
            Assert.Equal(passed.ReceiptId, replay.Receipt!.ReceiptId);
            Assert.Equal(passed.Identity.Value, replay.Receipt.Identity.Value);
            var originalDrop = Assert.Single(scenario.Result.Ejections);
            var replayDrop = Assert.Single(replay.Ejections);
            Assert.Equal(originalDrop.GoalId, replayDrop.GoalId);
            Assert.Equal(originalDrop.Reason, replayDrop.Reason);
            Assert.Equal(originalDrop.Detail, replayDrop.Detail);
            Assert.Equal(scenario.Goals.Skip(1).Select(goal => goal.Id.Value).Order(), replay.MemberResults.Keys.Order());
            Assert.Equal(Key(scenario, 0), Assert.Single(scenario.Driver.ReadTrainImplicatedMemberKeys()));
            AssertLandedFiles(scenario, 0);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TwoMemberRed_ImplicatesOnlyAttributedMemberAndStillSuppressesPair(int culprit)
    {
        WithTrain(repo => [Red(repo, "pair.trx", Identity(culprit))], scenario =>
        {
            Assert.Null(scenario.Result.Receipt);
            Assert.Empty(scenario.Result.MemberResults);
            Assert.Empty(scenario.Result.Ejections);
            Assert.Equal(1, scenario.Verifier.RunCount);
            Assert.Equal(Key(scenario, culprit), Assert.Single(scenario.Driver.ReadTrainImplicatedMemberKeys()));
            Assert.DoesNotContain(Key(scenario, 1 - culprit), scenario.Driver.ReadTrainImplicatedMemberKeys());
            AssertPairSuppressed(scenario, 0, 1);
            Assert.Contains("fallback=ordinary", scenario.Result.Detail);
        }, memberCount: 2);
    }

    [Fact]
    public void SecondCompositionRed_RecordsItsAttributedMemberAndPairSuppression()
    {
        WithTrain(repo => [Red(repo, "first.trx", Identity(0)), Red(repo, "pair.trx", Identity(1))], scenario =>
        {
            AssertAttributedDrop(scenario, 0);
            Assert.Null(scenario.Result.Receipt);
            Assert.Empty(scenario.Result.MemberResults);
            Assert.Equal(2, scenario.Verifier.RunCount);
            Assert.Equal(Paths.Skip(1), scenario.Verifier.ChangedFiles[1]);
            Assert.Equal(Key(scenario, 1), Assert.Single(scenario.Driver.ReadTrainImplicatedMemberKeys()));
            Assert.DoesNotContain(Key(scenario, 0), scenario.Driver.ReadTrainImplicatedMemberKeys());
            Assert.DoesNotContain(Key(scenario, 2), scenario.Driver.ReadTrainImplicatedMemberKeys());
            AssertPairSuppressed(scenario, 1, 2);
            Assert.Contains("fallback=ordinary", scenario.Result.Detail);
        });
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("unchanged")]
    [InlineData("shared")]
    [InlineData("different-members")]
    public void AmbiguousRed_RetainsNewestDropAndPassingRemainder(string scenario)
    {
        var names = scenario switch
        {
            "unresolved" => new[] { "Ns.MissingTests.Fails" },
            "unchanged" => ["Ns.UnchangedTests.Fails"],
            "shared" => ["Ns.SharedTests.Fails"],
            "different-members" => [Identity(0), Identity(1)],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        WithTrain(repo => [Red(repo, "red.trx", names), Passing(repo)], train =>
        {
            Assert.Equal(2, train.Verifier.RunCount);
            Assert.Equal(MergeTrainGateOutcome.Passed, train.Result.Receipt!.Outcome);
            Assert.Equal(Paths.Take(2), train.Verifier.ChangedFiles[1]);
            var ejection = Assert.Single(train.Result.Ejections);
            Assert.Equal(train.Goals[2].Id, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedNewestMember, ejection.Reason);
            Assert.Equal(train.Goals.Take(2).Select(goal => goal.Id),
                train.Result.Receipt.Identity.Members.Select(member => member.GoalId));
        });
    }

    private static readonly string[] Paths =
    [
        "tests/Mcg.AgentOrchestrator.Core.Tests/OldestCulpritTests.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OtherMemberTests.cs",
        "tests/Mcg.AgentOrchestrator.Dashboard.Tests/NewestMemberTests.cs"
    ];

    private void WithTrain(
        Func<string, IReadOnlyList<AcceptanceVerificationResult>> results,
        Action<Scenario> assert,
        int memberCount = 3,
        bool gateOnly = true)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            var unchanged = "tests/UnchangedTests.cs";
            Directory.CreateDirectory(Path.Combine(repo, "tests"));
            File.WriteAllText(Path.Combine(repo, unchanged), "namespace Ns; public class UnchangedTests { }");
            RunGit(repo, "add", unchanged);
            RunGit(repo, "commit", "-m", "Add unchanged test source");
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, memberCount).Select(index =>
            {
                var goal = CreateCompletedGoal(kernel, $"Attributed train member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                var className = Identity(index).Split('.')[1];
                var shared = index < 2 ? "public partial class SharedTests { }" : string.Empty;
                var guards = index == 1
                    ? "public class AcceptanceGateEngineSettingsTests { } public class WorkflowDecisionCoverageRatchetTests { }" : string.Empty;
                _ = CreateWorktreeCandidate(repo, goal.Id, Paths[index],
                    $"namespace Ns; public class {className} {{ }} {shared} {guards}");
                return goal;
            }).ToArray();
            var verifier = new SequenceAcceptanceVerifier(results(repo));
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var selection = new ConductorMergeTrainSelection(goals.Select(goal =>
                Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                    driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive)).Projection).ToArray());
            Assert.Equal(goals.Select(goal => goal.Id), selection.Members.Select(member => member.GoalId));
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive, gateOnly: gateOnly);
            assert(new Scenario(repo, goals, driver, selection, result, verifier));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    private static AcceptanceVerificationResult Red(string repo, string fileName, params string[] identities)
    {
        var path = Path.Combine(repo, fileName);
        XNamespace trxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(trxNamespace + "TestRun", new XElement(trxNamespace + "Results", identities.Select((identity, index) =>
            new XElement(trxNamespace + "UnitTestResult", new XAttribute("testId", index), new XAttribute("testName", identity),
                new XAttribute("outcome", "Failed")))),
            new XElement(trxNamespace + "ResultSummary", new XAttribute("outcome", "Failed"), new XElement(trxNamespace + "Counters",
                new XAttribute("total", identities.Length), new XAttribute("executed", identities.Length),
                new XAttribute("passed", 0), new XAttribute("failed", identities.Length))))).Save(path);
        var read = AcceptanceTrxFailureReader.Read(path);
        Assert.Equal(AcceptanceTrxReadStatus.Readable, read.Status);
        Assert.Equal(identities, read.Failures.Select(failure => failure.TestName));
        Assert.All(read.Failures, failure =>
        {
            Assert.True(AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome));
            Assert.Null(ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace));
        });
        var result = new AcceptanceVerificationResult(false, false, 1, "train failed",
            Checks: [new AcceptanceCheckResult("train", false, 1, "train failed")], TestResultPaths: [path]);
        Assert.Equal(AcceptanceCohortGateOutcome.Failed, ConductorDriver.ClassifyCohortVerification(result));
        return result;
    }

    private static AcceptanceVerificationResult Passing(string repo) => new(true, false, 0, null,
        Checks: [new AcceptanceCheckResult("remainder", true, 0, null)], TestResultPaths: [WritePassingTrx(repo, "pass.trx")]);

    private static AcceptanceVerificationResult GuardRed(string repo, string file, string testClass, string sourceFile)
    {
        var red = Red(repo, file,
            "Ns.AcceptanceGateEngineSettingsTests.AcceptanceGateEngineDisabledCollectionsSpanningLanesShareAnExclusiveResource",
            "Ns.WorkflowDecisionCoverageRatchetTests.EveryUndecidedSite_IsAllowListedByFileAndMember");
        var document = XDocument.Load(red.TestResultPaths![0]);
        var laneMessage = "Disabled collection 'DotnetBuildEnvironmentManagerStaticHooks' spans acceptance lanes " +
            "[Remainder, Dotnet build slots] without a shared exclusive resource key. " +
            $"Mapped classes: [{testClass} -> Remainder, DotnetBuildEnvironmentManagerTests -> Dotnet build slots].";
        var messages = new[]
        {
            Assert.Throws<Xunit.Sdk.TrueException>(() => Assert.True(false, laneMessage)).Message,
            $"Unlisted undecided sites:\n{sourceFile} : DeferredRun"
        };
        foreach (var (result, message) in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult").Zip(messages))
        {
            var ns = result.Name.Namespace;
            result.Add(new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", message))));
        }
        document.Save(red.TestResultPaths[0]);
        return red;
    }

    private static string Identity(int member) => member switch
    {
        0 => "Ns.OldestCulpritTests.Fails",
        1 => "Ns.OtherMemberTests.Fails",
        2 => "Ns.NewestMemberTests.Fails",
        _ => throw new ArgumentOutOfRangeException(nameof(member))
    };

    private static string Key(Scenario scenario, int member) => ConductorAcceptanceCohortAttributedMembers.Key(
        scenario.Selection.Members[member].GoalId, scenario.Selection.Members[member].CandidateRevision);

    private static void AssertAttributedDrop(Scenario scenario, int culprit)
    {
        var dropped = Assert.Single(scenario.Result.Ejections);
        Assert.Equal(scenario.Goals[culprit].Id, dropped.GoalId);
        Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, dropped.Reason);
    }

    private static void AssertPairSuppressed(Scenario scenario, int first, int second)
    {
        var pairs = scenario.Driver.ReadSuppressedGroupedPairs();
        Assert.Equal(2, pairs.Count);
        Assert.Contains(ConductorAcceptanceCohortSelector.PairFingerprint(
            scenario.Selection.Members[first], scenario.Selection.Members[second]), pairs);
        Assert.Contains(ConductorAcceptanceCohortSelector.PairFingerprint(
            scenario.Selection.Members[second], scenario.Selection.Members[first]), pairs);
    }

    private static void AssertLandedFiles(Scenario scenario, int culprit)
    {
        for (var index = 0; index < Paths.Length; index++)
            Assert.Equal(index != culprit, File.Exists(Path.Combine(scenario.Repo, Paths[index])));
    }

    private sealed record Scenario(string Repo, Goal[] Goals, ConductorDriver Driver,
        ConductorMergeTrainSelection Selection, ConductorMergeTrainRunResult Result, SequenceAcceptanceVerifier Verifier);
}
