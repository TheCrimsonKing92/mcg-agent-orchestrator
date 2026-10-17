using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Fixture = RepositoryTestReachTests.RepositoryFixture;

// Parallel-safe: each scenario owns its git repositories, worktrees, stores and hooks.
public sealed class MergeTrainRedAttributionReplayScenarioTests : AcceptanceCohortWorkflowTests
{
    private const string Alpha = "AlphaReachTests";
    private const string Zebra = "ZebraReachTests";
    private const string FailuresDirectory = Fixture.TestDirectory + "/Unchanged";
    private const string EvidenceTest = Fixture.TestDirectory + "/Evidence/MemberEvidenceTests.cs";
    private const string NewestTest = Fixture.TestDirectory + "/Cli/NewestMemberTests.cs";

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void IncidentReplay_EjectsAndFailsOnlyReachCulprit(int count)
    {
        WithTrain(count, shared: false, ownership: false, scenario =>
        {
            var ejection = Assert.Single(scenario.Result.Ejections);
            Assert.Equal(scenario.Goals[0].Id, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, ejection.Reason);
            AssertPersistedEjection(scenario, scenario.Goals[0].Id, "RedAttributedMember");
            Assert.Equal(GoalStatus.AcceptanceFailed, scenario.Goals[0].Status);
            var failure = Assert.IsType<AcceptanceFailureSummary>(scenario.Goals[0].LatestAcceptanceFailure);
            Assert.Equal(new[] { Alpha, Zebra }, failure.FailedChecks);
            Assert.Equal(scenario.Selection.Members[0].CandidateRevision, failure.BranchHeadSha);
            Assert.All(scenario.Goals.Skip(1), goal =>
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.Null(goal.LatestAcceptanceFailure);
            });
            Assert.Equal(Key(scenario, 0), Assert.Single(scenario.Driver.ReadTrainImplicatedMemberKeys()));
            var events = AttributionEvents(scenario.Repo);
            Assert.NotEmpty(events);
            Assert.All(events, record =>
            {
                Assert.Equal(scenario.Goals[0].Id.Value, record.GoalId);
                Assert.Contains("TRAIN_IMPLICATED", record.Detail);
                Assert.Contains("attribution=reach", record.Detail);
                Assert.Contains($"subjects={Alpha},{Zebra}", record.Detail);
            });
            if (count == 3)
            {
                Assert.Equal(2, scenario.Verifier.RunCount);
                Assert.Equal(scenario.Selection.Members.Skip(1).SelectMany(member => member.LandingPaths), scenario.Verifier.ChangedFiles[1]);
                Assert.Equal(MergeTrainGateOutcome.Passed, scenario.Result.Receipt!.Outcome);
                Assert.Equal(scenario.Goals.Skip(1).Select(goal => goal.Id), scenario.Result.Receipt.Identity.Members.Select(member => member.GoalId));
                AssertNextSelectionExcludesCulprit(scenario);
            }
            else
            {
                Assert.Equal(1, scenario.Verifier.RunCount);
                Assert.Null(scenario.Result.Receipt);
                Assert.Contains("fallback=ordinary", scenario.Result.Detail);
                AssertPairSuppressed(scenario);
            }

            // A re-entry at the exact candidate must not record or route the failure again.
            var red = new MergeTrainAcceptanceStore(Path.Combine(OrchestratorWorkspace.ForDirectory(scenario.Repo).OrchestratorDirectory,
                "merge-train-acceptance.db")).ReadReceiptsSince(DateTimeOffset.MinValue)
                .Single(receipt => receipt.Outcome == MergeTrainGateOutcome.Failed);
            var records = 0;
            var routes = 0;
            MergeTrainAttributedFailure.Apply(red, scenario.Selection.BindMembers()[0], scenario.Goals[0], "reach", count,
                [], _ => { }, (_, _, _, _, _, _) => records++, (_, _) => { routes++; return true; });
            Assert.Equal(0, records);
            Assert.Equal(0, routes);
            Assert.Same(failure, scenario.Goals[0].LatestAcceptanceFailure);
        });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void SharedReach_KeepsOrdinaryFallbackAndNewestDrop(int count)
    {
        WithTrain(count, shared: true, ownership: false, scenario =>
        {
            Assert.All(scenario.Goals, goal =>
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.Null(goal.LatestAcceptanceFailure);
            });
            Assert.Empty(scenario.Driver.ReadTrainImplicatedMemberKeys());
            Assert.Empty(AttributionEvents(scenario.Repo));
            if (count == 3)
            {
                var ejection = Assert.Single(scenario.Result.Ejections);
                Assert.Equal(scenario.Goals[2].Id, ejection.GoalId);
                Assert.Equal(MergeTrainEjectionReason.RedNewestMember, ejection.Reason);
                AssertPersistedEjection(scenario, scenario.Goals[2].Id, "RedNewestMember");
                Assert.Equal(2, scenario.Verifier.RunCount);
            }
            else
            {
                Assert.Empty(scenario.Result.Ejections);
                Assert.Equal(1, scenario.Verifier.RunCount);
                Assert.Contains("fallback=ordinary", scenario.Result.Detail);
                AssertPairSuppressed(scenario);
            }
        });
    }

    [Fact]
    public void OwnershipWins_WhenReachWouldNameAnotherMember()
    {
        WithTrain(3, shared: false, ownership: true, scenario =>
        {
            Assert.Equal(scenario.Goals[1].Id, Assert.Single(scenario.Result.Ejections).GoalId);
            Assert.Equal(GoalStatus.AcceptanceFailed, scenario.Goals[1].Status);
            Assert.Equal(GoalStatus.Verified, scenario.Goals[0].Status);
            var events = AttributionEvents(scenario.Repo);
            Assert.NotEmpty(events);
            Assert.All(events, record =>
            {
                Assert.Equal(scenario.Goals[1].Id.Value, record.GoalId);
                Assert.Contains("attribution=ownership", record.Detail);
                Assert.DoesNotContain("attribution=reach", record.Detail);
            });
        });
    }

    private void WithTrain(int count, bool shared, bool ownership, Action<Scenario> assert)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            Fixture.Populate(repo);
            Fixture.WriteAt(repo, Fixture.Intermediate, "public class Intermediate { private ChangedSource value; " +
                (shared ? "private OtherSource other; " : string.Empty) + "}");
            foreach (var name in new[] { Alpha, Zebra })
                Fixture.WriteAt(repo, $"{FailuresDirectory}/{name}.cs", TestSource(name));
            RunGit(repo, "add", "src", "tests");
            RunGit(repo, "commit", "-m", "Add source reach fixture");
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, count).Select(index =>
            {
                var goal = CreateCompletedGoal(kernel, $"Reach train member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                if (index == 0)
                {
                    CreateWorktreeCandidate(repo, goal.Id, Fixture.Source, "public class ChangedSource { public bool Changed => true; }");
                    CreateWorktreeCandidate(repo, goal.Id, EvidenceTest,
                        "public class MemberEvidenceTests { [Xunit.Fact] public void Runs() { } }");
                }
                else if (index == 1)
                {
                    CreateWorktreeCandidate(repo, goal.Id, Fixture.Other, "public class OtherSource { public bool Changed => true; }");
                    if (ownership)
                        foreach (var name in new[] { Alpha, Zebra })
                            CreateWorktreeCandidate(repo, goal.Id, $"{FailuresDirectory}/{name}.cs", TestSource(name) + " // owned");
                }
                else
                    CreateWorktreeCandidate(repo, goal.Id, NewestTest,
                        "public class NewestMemberTests { [Xunit.Fact] public void Runs() { } }");
                return goal;
            }).ToArray();
            var paths = MergeTrainReachAttributionTests.Trx(repo, Zebra + ".Fails", Alpha + ".Fails");
            var red = new AcceptanceVerificationResult(false, false, 1, "train failed",
                Checks: [new AcceptanceCheckResult("train", false, 1, "train failed")], TestResultPaths: paths);
            Assert.Equal(AcceptanceCohortGateOutcome.Failed, ConductorDriver.ClassifyCohortVerification(red));
            // Shared failures persist when the innocent newest member is dropped;
            // a passing remainder would itself authorize the existing bisect implication.
            var verifier = new SequenceAcceptanceVerifier([red, shared ? red : new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("remainder", true, 0, null)], TestResultPaths: [WritePassingTrx(repo, "pass.trx")])]);
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var selection = ProjectTrainSelection(driver, goals);
            var memberTree = Assert.IsType<string>(GoalWorktrees.TryResolve(repo, goals[0].Id));
            var members = selection.BindMembers();
            foreach (var member in members.Take(2))
                Assert.Contains(RepositoryTestImpactPlanner.Plan(member.LandingPaths, memberTree).Checks,
                    check => check.TestProject == RepositoryTestProject.Infrastructure && check.TestClassSelections is null);
            // Ownership names D while reach names E; observing ownership proves precedence.
            Assert.Equal(shared ? null : goals[0].Id,
                MergeTrainReachAttribution.TryAttribute(paths, memberTree, members, out _)?.GoalId);
            if (!ownership)
            {
                Assert.Null(MergeTrainRedAttribution.TryAttribute(paths, memberTree, members, out _));
                Assert.Null(MergeTrainImpactAttribution.TryAttribute(paths, memberTree, members, out _));
            }
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            assert(new Scenario(repo, driver, selection, goals, result, verifier));
        }
        finally { DeleteDirectory(repo); }
    }

    private static string TestSource(string name) =>
        $"public class {name} {{ private Intermediate value; [Xunit.Fact] public void Fails() {{ }} }}";

    private static string Key(Scenario scenario, int index) => ConductorAcceptanceCohortAttributedMembers.Key(
        scenario.Goals[index].Id, scenario.Selection.Members[index].CandidateRevision);

    private static void AssertNextSelectionExcludesCulprit(Scenario scenario)
    {
        var candidates = scenario.Selection.Members.Select(projection => new ConductorSpeculativeAcceptanceCandidate(
            projection.GoalId, new GateReadyCandidateProjectionResult.Ready(projection))).ToArray();
        var partnerProjection = new GateReadyCandidateProjection(new GoalId(new string('f', 32)), GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
            ["src/Partner.cs"], ["partner:f"], new GateReadyMergeEvidence(new string('f', 40),
                scenario.Selection.Members[0].MainRevision, GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));
        var partner = new ConductorSpeculativeAcceptanceCandidate(partnerProjection.GoalId,
            new GateReadyCandidateProjectionResult.Ready(partnerProjection));
        var next = ConductorMergeTrainSelector.Select([.. candidates, partner],
            trainImplicatedMemberKeys: scenario.Driver.ReadTrainImplicatedMemberKeys());
        Assert.Equal(new[] { scenario.Goals[1].Id, scenario.Goals[2].Id, partner.GoalId }, next!.Members.Select(member => member.GoalId));
    }

    private static void AssertPairSuppressed(Scenario scenario)
    {
        var pairs = scenario.Driver.ReadSuppressedGroupedPairs();
        Assert.Equal(2, pairs.Count);
        Assert.Contains(ConductorAcceptanceCohortSelector.PairFingerprint(scenario.Selection.Members[0], scenario.Selection.Members[1]), pairs);
        Assert.Contains(ConductorAcceptanceCohortSelector.PairFingerprint(scenario.Selection.Members[1], scenario.Selection.Members[0]), pairs);
    }

    private static void AssertPersistedEjection(Scenario scenario, GoalId goal, string reason)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(scenario.Repo);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"),
            Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT goal_id, reason FROM merge_train_ejections";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(goal.Value, reader.GetString(0));
        Assert.Equal(reason, reader.GetString(1));
        Assert.False(reader.Read());
    }

    private static ConductEventRecord[] AttributionEvents(string repo)
    {
        var path = OrchestratorWorkspace.ForDirectory(repo).ConductEventsLogPath;
        return !File.Exists(path) ? [] : File.ReadAllLines(path)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(record => record.EventKind == "train-attribution").ToArray();
    }

    private sealed record Scenario(string Repo, ConductorDriver Driver, ConductorMergeTrainSelection Selection,
        Goal[] Goals, ConductorMergeTrainRunResult Result, SequenceAcceptanceVerifier Verifier);
}
