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

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ReadOnlyChild_ParentCheckpointsFailureBeforeNextSelection(int count)
    {
        WithTrain(count, shared: false, ownership: false, scenario =>
        {
            var repository = new SqliteOrchestratorStateRepository(
                OrchestratorWorkspace.ForDirectory(scenario.Repo).SqliteStatePath);
            var before = repository.LoadAsync().GetAwaiter().GetResult();
            Assert.All(before.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            Assert.All(scenario.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            Assert.Equal(GoalStatus.AcceptanceFailed, scenario.ChildKernel!.GetGoal(scenario.Goals[0].Id).Status);
            Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, Assert.Single(scenario.Result.Ejections).Reason);
            var changed = new HashSet<GoalId>();
            var remaining = MergeTrainAttributedFailureReconciliation.PrepareAdmission(scenario.Driver,
                scenario.Goals, ConductorAutonomyPolicy.Permissive, changed);
            Assert.Equal(scenario.Goals[0].Id, Assert.Single(changed));
            Assert.Equal(scenario.Goals.Skip(1).Select(goal => goal.Id), remaining.Select(goal => goal.Id));
            repository.SaveGoalSnapshotsAsync(scenario.Kernel.ExportSnapshot().Goals
                .Where(goal => changed.Contains(new GoalId(goal.Id))).ToArray()).GetAwaiter().GetResult();
            var persisted = repository.LoadAsync().GetAwaiter().GetResult();
            var culprit = persisted.GetGoal(scenario.Goals[0].Id);
            Assert.Equal(GoalStatus.AcceptanceFailed, culprit.Status);
            Assert.Equal(new[] { Alpha, Zebra }, culprit.LatestAcceptanceFailure!.FailedChecks);
            Assert.Equal(scenario.Selection.Members[0].CandidateRevision, culprit.LatestAcceptanceFailure.BranchHeadSha);
            Assert.All(scenario.Goals.Skip(1), goal =>
            {
                Assert.Equal(GoalStatus.Verified, persisted.GetGoal(goal.Id).Status);
                Assert.Null(persisted.GetGoal(goal.Id).LatestAcceptanceFailure);
            });
            var failure = scenario.Goals[0].LatestAcceptanceFailure;
            Assert.Empty(scenario.Driver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive));
            Assert.Same(failure, scenario.Goals[0].LatestAcceptanceFailure);
            AssertNextSelectionExcludesCulprit(scenario);
            AssertPersistedEjection(scenario, culprit.Id, "RedAttributedMember");

            scenario.Kernel.RetryAcceptanceGate(culprit.Id, "Recheck the same candidate");
            repository.SaveAsync(scenario.Kernel).GetAwaiter().GetResult();
            var restarted = repository.LoadAsync().GetAwaiter().GetResult();
            var restartedDriver = new ConductorDriver(restarted, OrchestratorWorkspace.ForDirectory(scenario.Repo),
                new SequenceAcceptanceVerifier([]), AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(scenario.Repo).Hooks);
            var retryProjection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                restartedDriver.ProjectGateReadyCandidate(restarted.GetGoal(culprit.Id), ConductorAutonomyPolicy.Permissive));
            Assert.Equal(scenario.Selection.Members[0].CandidateRevision, retryProjection.Projection.CandidateRevision);
            Assert.Empty(restartedDriver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive));
            Assert.Equal(GoalStatus.Verified, restarted.GetGoal(culprit.Id).Status);
            Assert.Null(restarted.GetGoal(culprit.Id).LatestAcceptanceFailure);
        }, readOnlyChild: true);
    }

    [Fact]
    public void ReadOnlyChild_LandedRemainderStillFailsCulprit()
    {
        WithTrain(3, shared: false, ownership: false, scenario =>
        {
            Assert.Equal(MergeTrainGateOutcome.Passed, scenario.Result.Receipt!.Outcome);
            Assert.All(scenario.Goals.Skip(1), goal =>
                Assert.Equal(GoalStatus.Completed, scenario.ChildKernel!.GetGoal(goal.Id).Status));
            var projection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                scenario.Driver.ProjectGateReadyCandidate(scenario.Goals[0], ConductorAutonomyPolicy.Permissive));
            Assert.NotEqual(scenario.Selection.Members[0].MainRevision, projection.Projection.MainRevision);
            Assert.Equal(scenario.Selection.Members[0].CandidateRevision, projection.Projection.CandidateRevision);
            var checkpoints = new HashSet<GoalId>();
            var lines = new List<string>();
            MergeTrainAttributedFailureReconciliation.PrepareAdmission(scenario.Driver,
                scenario.Goals, ConductorAutonomyPolicy.Permissive, checkpoints, lines);
            Assert.Equal(scenario.Goals[0].Id, Assert.Single(checkpoints));
            Assert.Contains("TRAIN_FAILURE_RECONCILED", Assert.Single(lines));
            var repository = new SqliteOrchestratorStateRepository(
                OrchestratorWorkspace.ForDirectory(scenario.Repo).SqliteStatePath);
            repository.SaveGoalSnapshotsAsync(scenario.Kernel.ExportSnapshot().Goals
                .Where(goal => checkpoints.Contains(new GoalId(goal.Id))).ToArray()).GetAwaiter().GetResult();
            Assert.Equal(GoalStatus.AcceptanceFailed, repository.LoadAsync().GetAwaiter().GetResult()
                .GetGoal(scenario.Goals[0].Id).Status);
        }, readOnlyChild: true, gateOnly: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyChild_LaterFaultRetainsEarlierCheckpoint(bool mutationFault)
    {
        WithTrain(3, shared: false, ownership: false, scenario =>
        {
            var workspace = OrchestratorWorkspace.ForDirectory(scenario.Repo);
            var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            var red = store.ReadReceiptsSince(DateTimeOffset.MinValue).Single(receipt => receipt.Outcome == MergeTrainGateOutcome.Failed);
            var later = scenario.Goals[1];
            store.RecordTrainImplicatedCandidate(later.Id, scenario.Selection.Members[1].CandidateRevision,
                red.Identity.Value, red.Identity.ObservedMainRevision);
            store.RecordEjections("later-member", [new MergeTrainEjection(later.Id,
                MergeTrainEjectionReason.RedAttributedMember, [], $"Dropped after RED receipt {red.ReceiptId}.")]);
            var checkpoints = new HashSet<GoalId>();
            var projected = new List<GoalId>();
            IReadOnlySet<GoalId> Reconcile() => MergeTrainAttributedFailureReconciliation.Reconcile(
                workspace, scenario.Kernel, store, goal =>
                {
                    projected.Add(goal.Id);
                    if (!mutationFault && goal.Id == later.Id) throw new IOException("unreadable later candidate");
                    return scenario.Driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive);
                }, (goal, checks, branch, main, attributions, baseline) =>
                {
                    if (mutationFault && goal.Id == later.Id) throw new InvalidOperationException("failure recording stopped");
                    scenario.Kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attributions, baseline);
                }, checkpoints);
            if (mutationFault)
                Assert.Equal("failure recording stopped", Assert.Throws<InvalidOperationException>(() => Reconcile()).Message);
            else
                Assert.Equal(scenario.Goals[0].Id, Assert.Single(Reconcile()));
            Assert.Equal(scenario.Goals.Take(2).Select(goal => goal.Id), projected);
            Assert.Contains(scenario.Goals[0].Id, checkpoints);
            Assert.Equal(GoalStatus.AcceptanceFailed, scenario.Goals[0].Status);
            Assert.Equal(GoalStatus.Verified, later.Status);
            if (!mutationFault)
            {
                Assert.Contains(later.Id, checkpoints);
                Assert.Contains(later.Timeline, record => record.Message.Contains("TRAIN_FAILURE_RECONCILIATION_ERROR"));
            }
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            repository.SaveGoalSnapshotsAsync(scenario.Kernel.ExportSnapshot().Goals
                .Where(goal => checkpoints.Contains(new GoalId(goal.Id))).ToArray()).GetAwaiter().GetResult();
            Assert.Equal(GoalStatus.AcceptanceFailed, repository.LoadAsync().GetAwaiter().GetResult()
                .GetGoal(scenario.Goals[0].Id).Status);
        }, readOnlyChild: true);
    }

    [Fact]
    public void ReadOnlyChild_NewCandidateRevisionDoesNotInheritFailure()
    {
        WithTrain(3, shared: false, ownership: false, scenario =>
        {
            CreateWorktreeCandidate(scenario.Repo, scenario.Goals[0].Id, Fixture.Source,
                "public class ChangedSource { public bool Revised => true; }");
            var projection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                scenario.Driver.ProjectGateReadyCandidate(scenario.Goals[0], ConductorAutonomyPolicy.Permissive));
            Assert.NotEqual(scenario.Selection.Members[0].CandidateRevision, projection.Projection.CandidateRevision);
            Assert.Empty(scenario.Driver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive));
            Assert.All(scenario.Goals, goal =>
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.Null(goal.LatestAcceptanceFailure);
            });
        }, readOnlyChild: true);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ReadOnlyChild_SharedReachDoesNotFailParentGoals(int count)
    {
        WithTrain(count, shared: true, ownership: false, scenario =>
        {
            Assert.Equal(count == 3 ? 1 : 0, scenario.Result.Ejections.Count);
            Assert.All(scenario.Result.Ejections, ejection =>
                Assert.Equal(MergeTrainEjectionReason.RedNewestMember, ejection.Reason));
            Assert.Empty(scenario.Driver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive));
            Assert.All(scenario.Goals, goal =>
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.Null(goal.LatestAcceptanceFailure);
            });
        }, readOnlyChild: true);
    }

    private void WithTrain(int count, bool shared, bool ownership, Action<Scenario> assert,
        bool readOnlyChild = false, bool gateOnly = true)
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
            AgentOrchestratorKernel? childKernel = null;
            var executingDriver = driver;
            var executingGoals = goals;
            if (readOnlyChild)
            {
                var workspace = OrchestratorWorkspace.ForDirectory(repo);
                new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
                childKernel = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                    .LoadGoalsAsync(goals.Select(goal => goal.Id).ToArray()).GetAwaiter().GetResult();
                executingGoals = goals.Select(goal => childKernel.GetGoal(goal.Id)).ToArray();
                executingDriver = new ConductorDriver(childKernel, workspace, verifier,
                    AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            }
            var result = executingDriver.RunMergeTrain(selection, executingGoals, ConductorAutonomyPolicy.Permissive, gateOnly: gateOnly);
            assert(new Scenario(repo, driver, selection, goals, result, verifier, kernel, childKernel));
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
        var partners = Enumerable.Range(0, 4 - scenario.Goals.Length).Select(index =>
        {
            var id = (char)('f' - index);
            var projection = new GateReadyCandidateProjection(new GoalId(new string(id, 32)), GoalLifecycleState.Verified,
                GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
                [$"src/Partner{id}.cs"], [$"partner:{id}"], new GateReadyMergeEvidence(new string(id, 40),
                    scenario.Selection.Members[0].MainRevision, GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));
            return new ConductorSpeculativeAcceptanceCandidate(projection.GoalId,
                new GateReadyCandidateProjectionResult.Ready(projection));
        }).ToArray();
        var next = ConductorMergeTrainSelector.Select([.. candidates, .. partners],
            trainImplicatedMemberKeys: scenario.Driver.ReadTrainImplicatedMemberKeys());
        Assert.NotNull(next);
        Assert.Equal(scenario.Goals.Skip(1).Select(goal => goal.Id).Concat(partners.Select(partner => partner.GoalId)),
            next.Members.Select(member => member.GoalId));
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
        Goal[] Goals, ConductorMergeTrainRunResult Result, SequenceAcceptanceVerifier Verifier,
        AgentOrchestratorKernel Kernel, AgentOrchestratorKernel? ChildKernel);
}
