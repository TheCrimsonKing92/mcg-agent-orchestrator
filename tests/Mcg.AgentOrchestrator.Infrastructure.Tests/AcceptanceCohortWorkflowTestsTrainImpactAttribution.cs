using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its git repository, worktrees, database and cleanup hooks.
public sealed class AcceptanceCohortWorkflowTestsTrainImpactAttribution : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void ImpactRed_ImplicatesSourceMemberAndAdmitsOtherMembersNextTime()
    {
        WithTrain(ownership: false, (repo, driver, selection, goals, result, verifier) =>
        {
            Assert.Equal(2, verifier.RunCount);
            var ejection = Assert.Single(result.Ejections);
            Assert.Equal(goals[0].Id, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, ejection.Reason);
            Assert.Equal(MergeTrainGateOutcome.Passed, result.Receipt!.Outcome);
            Assert.Equal(goals.Skip(1).Select(goal => goal.Id), result.Receipt.Identity.Members.Select(member => member.GoalId));
            var keys = driver.ReadTrainImplicatedMemberKeys();
            Assert.Equal(ConductorAcceptanceCohortAttributedMembers.Key(goals[0].Id, selection.Members[0].CandidateRevision), Assert.Single(keys));

            var candidates = selection.Members.Select(Candidate).ToArray();
            Assert.NotNull(ConductorMergeTrainSelector.Select(candidates));
            Assert.Null(ConductorMergeTrainSelector.Select(candidates, trainImplicatedMemberKeys: keys));
            var partner = Candidate(new GateReadyCandidateProjection(new GoalId(new string('f', 32)), GoalLifecycleState.Verified,
                GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
                ["src/Partner.cs"], ["partner:f"], new GateReadyMergeEvidence(new string('f', 40), selection.Members[0].MainRevision,
                    GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected)));
            var next = ConductorMergeTrainSelector.Select([.. candidates, partner], trainImplicatedMemberKeys: keys);
            Assert.Equal(new[] { goals[1].Id, goals[2].Id, partner.GoalId }, next!.Members.Select(member => member.GoalId));

            var events = AttributionEvents(repo);
            Assert.NotEmpty(events);
            Assert.All(events, record =>
            {
                Assert.Equal(goals[0].Id.Value, record.GoalId);
                Assert.Contains("attribution=impact", record.Detail, StringComparison.Ordinal);
                Assert.Contains("failing_test_classes=ProbeTests", record.Detail, StringComparison.Ordinal);
                Assert.Contains("subjects=ProbeTests", record.Detail, StringComparison.Ordinal);
            });
        });
    }

    [Fact]
    public void AmbiguousImpactRed_RecordsNoImplicationAndDropsNewestMember()
    {
        WithTrain(ownership: false, (repo, driver, selection, goals, result, verifier) =>
        {
            Assert.Equal(2, verifier.RunCount);
            var ejection = Assert.Single(result.Ejections);
            Assert.Equal(goals[^1].Id, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedNewestMember, ejection.Reason);
            Assert.Equal(selection.Members.SelectMany(member => member.LandingPaths), verifier.ChangedFiles[0]);
            Assert.Equal(selection.Members.Take(2).SelectMany(member => member.LandingPaths), verifier.ChangedFiles[1]);
            Assert.Null(result.Receipt);
            var red = Assert.IsType<MergeTrainReceipt>(result.RecordedReceipt);
            Assert.Equal(MergeTrainGateOutcome.Failed, red.Outcome);
            Assert.Equal(goals.Take(2).Select(goal => goal.Id), red.Identity.Members.Select(member => member.GoalId));
            Assert.Empty(result.MemberResults);
            Assert.Contains("attempts=2 fallback=ordinary", result.Detail, StringComparison.Ordinal);

            var keys = driver.ReadTrainImplicatedMemberKeys();
            Assert.Empty(keys);
            var next = ConductorMergeTrainSelector.Select(selection.Members.Select(Candidate).ToArray(),
                trainImplicatedMemberKeys: keys);
            Assert.Equal(goals.Select(goal => goal.Id), Assert.IsType<ConductorMergeTrainSelection>(next).Members.Select(member => member.GoalId));
            Assert.Empty(File.Exists(OrchestratorWorkspace.ForDirectory(repo).ConductEventsLogPath) ? AttributionEvents(repo) : []);
        }, ambiguousImpact: true);
    }

    [Fact]
    public void OwnershipEvent_AddsOnlySourceMarkerToExistingDetail()
    {
        WithTrain(ownership: true, (repo, driver, selection, goals, result, verifier) =>
        {
            Assert.Equal(goals[0].Id, Assert.Single(result.Ejections).GoalId);
            var events = AttributionEvents(repo);
            Assert.NotEmpty(events);
            Assert.All(events, record =>
            {
                var train = record.Detail.Split(' ').Single(token => token.StartsWith("train=", StringComparison.Ordinal));
                Assert.Equal($"TRAIN_IMPLICATED {train} member={goals[0].Id.Value} " +
                    $"candidate_revision={selection.Members[0].CandidateRevision} subjects=ProbeTests attribution=ownership", record.Detail);
            });
        });
    }

    private void WithTrain(bool ownership,
        Action<string, ConductorDriver, ConductorMergeTrainSelection, Goal[], ConductorMergeTrainRunResult, SequenceAcceptanceVerifier> assert,
        bool ambiguousImpact = false)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            Write(repo, "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(repo, "src/Mcg.AgentOrchestrator.Core/Application/CandidateTreeProbe.cs", "public class CandidateTreeProbe { }");
            if (ambiguousImpact)
                Write(repo, "src/Mcg.AgentOrchestrator.Core/Application/OtherService.cs", "public class OtherService { }");
            Write(repo, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<ProjectReference Include=\"../../src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj\" />" +
                "</ItemGroup></Project>");
            Write(repo, "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            const string test = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProbeTests.cs";
            Write(repo, test, "public class ProbeTests { private CandidateTreeProbe probe = new(); " +
                (ambiguousImpact ? "private OtherService other = new(); " : string.Empty) +
                "[Xunit.Fact] public void Fails() { } }");
            RunGit(repo, "add", "src", "tests");
            RunGit(repo, "commit", "-m", "Add impact fixture projects and unchanged test");
            var kernel = new AgentOrchestratorKernel();
            string[] paths = [ownership ? test : "src/Mcg.AgentOrchestrator.Core/Application/CandidateTreeProbe.cs",
                ambiguousImpact ? "src/Mcg.AgentOrchestrator.Core/Application/OtherService.cs"
                    : "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OtherMemberTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/NewestMemberTests.cs"];
            string[] contents = [ownership
                ? "public class ProbeTests { private CandidateTreeProbe probe = new(); [Xunit.Fact] public void Fails() { } } // changed"
                : "public class CandidateTreeProbe { public bool Changed => true; }",
                ambiguousImpact ? "public class OtherService { public bool Changed => true; }"
                    : "public class OtherMemberTests { [Xunit.Fact] public void Runs() { } }",
                "public class NewestMemberTests { [Xunit.Fact] public void Runs() { } }"];
            var goals = Enumerable.Range(0, 3).Select(index =>
            {
                var goal = CreateCompletedGoal(kernel, $"Impact train member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                _ = CreateWorktreeCandidate(repo, goal.Id, paths[index], contents[index]);
                return goal;
            }).ToArray();
            var path = Path.Combine(repo, "red.trx");
            XNamespace trxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(trxNamespace + "TestRun", new XElement(trxNamespace + "Results", new XElement(trxNamespace + "UnitTestResult",
                new XAttribute("testName", "Ns.ProbeTests.Fails"), new XAttribute("outcome", "Failed"))),
                new XElement(trxNamespace + "ResultSummary", new XAttribute("outcome", "Failed"), new XElement(trxNamespace + "Counters",
                    new XAttribute("total", 1), new XAttribute("executed", 1), new XAttribute("passed", 0), new XAttribute("failed", 1))))).Save(path);
            var red = new AcceptanceVerificationResult(false, false, 1, "train failed",
                Checks: [new AcceptanceCheckResult("train", false, 1, "train failed")], TestResultPaths: [path]);
            Assert.Equal(AcceptanceCohortGateOutcome.Failed, ConductorDriver.ClassifyCohortVerification(red));
            // A still-red remainder supplies no isolation proof against the dropped member.
            var verifier = new SequenceAcceptanceVerifier([red, ambiguousImpact ? red : new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("remainder", true, 0, null)], TestResultPaths: [WritePassingTrx(repo, "pass.trx")])]);
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var selection = ProjectTrainSelection(driver, goals);
            if (!ownership)
            {
                Assert.DoesNotContain(selection.Members.SelectMany(member => member.LandingPaths), landing => landing == test);
                var memberTree = Assert.IsType<string>(GoalWorktrees.TryResolve(repo, goals[0].Id));
                Assert.Null(MergeTrainRedAttribution.TryAttribute([path], memberTree, selection.BindMembers(), out _));
                Assert.Contains(RepositoryTestImpactPlanner.Plan(selection.Members[0].LandingPaths, memberTree).Checks,
                    check => check.TestClassSelections?.Contains("ProbeTests") == true);
                if (ambiguousImpact)
                {
                    var members = selection.BindMembers();
                    foreach (var member in members.Take(2))
                    {
                        var plan = RepositoryTestImpactPlanner.Plan(member.LandingPaths, memberTree);
                        Assert.False(plan.RequiresBroadVerification);
                        Assert.Null(plan.ReverseDependencyDegradation);
                        Assert.Contains(plan.Checks, check => check.TestClassSelections?.Contains("ProbeTests") == true);
                        Assert.Same(member, MergeTrainImpactAttribution.TryAttribute([path], memberTree, [member], out _));
                    }
                    Assert.Null(MergeTrainImpactAttribution.TryAttribute([path], memberTree, members, out var classes));
                    Assert.Empty(classes);
                }
            }
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            assert(repo, driver, selection, goals, result, verifier);
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    private static void Write(string repo, string path, string text)
    {
        var absolute = Path.Combine(repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, text);
    }

    private static ConductEventRecord[] AttributionEvents(string repo) =>
        File.ReadAllLines(OrchestratorWorkspace.ForDirectory(repo).ConductEventsLogPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(record => record.EventKind == "train-attribution").ToArray();

    private static ConductorSpeculativeAcceptanceCandidate Candidate(GateReadyCandidateProjection projection) =>
        new(projection.GoalId, new GateReadyCandidateProjectionResult.Ready(projection));
}
