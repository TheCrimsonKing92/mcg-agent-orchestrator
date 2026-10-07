using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsParallelAcceptanceCohortInteractionOnlyRouting : AcceptanceCohortWorkflowTests
{
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "CrossTick")]
    public void InteractionOnlyReceipt_IsReportedOnceAndRoutesMembersToSoloAcrossMainAdvance(bool nonAcceptanceObligations)
    {
        using var fixture = new RoutingFixture();
        var receipt = fixture.SeedInteractionOnly();
        if (nonAcceptanceObligations)
        {
            foreach (var member in receipt.Identity.Members)
                fixture.Kernel.MapCriterionEvidenceOwner(member.GoalId, 0, 1, CriterionEvidenceOwner.Operator,
                    "test", "operator observation", expectedCandidateSha: member.CandidateRevision);
            Assert.All(fixture.PairGoals, goal => Assert.Contains(goal.OutstandingCriterionEvidenceObligations,
                obligation => obligation.Owner == CriterionEvidenceOwner.Operator));
        }
        var readySnapshot = fixture.Kernel.ExportSnapshot();
        var mainBefore = RunGitOutput(fixture.Repo, "rev-parse", "main").Trim();
        var loop = fixture.CreateLoop();

        var first = fixture.Tick(loop);

        var outcome = Assert.Single(OutcomeLines(first, receipt));
        Assert.Contains("outcome=interaction-only", outcome);
        Assert.Contains($"attribution=InteractionOnly", outcome);
        foreach (var goal in fixture.PairGoals)
        {
            Assert.Contains(goal.Id.Value[..8], outcome);
            Assert.Contains($"{goal.Id.Value[..8]}:Passed", outcome);
        }
        var started = Assert.Single(first.ProgressLines!.Where(line =>
            line.StartsWith("ACCEPTANCE goal=", StringComparison.Ordinal) && line.Contains("result=started", StringComparison.Ordinal)));
        Assert.Contains(fixture.PairGoals, goal => started.Contains(goal.Id.Value[..8], StringComparison.Ordinal));
        Assert.Single(fixture.Launched);
        Assert.Contains(fixture.PairGoals, goal => goal.Id.Value == fixture.Launched[0].GoalId);
        Assert.Contains(first.ProgressLines!, line => line.Contains("reason=interaction-only-priority", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.EventLines(), IsEntry);
        Assert.Empty(fixture.Landings);
        Assert.Equal(mainBefore, RunGitOutput(fixture.Repo, "rev-parse", "main").Trim());
        Assert.False(fixture.Store.TryReadReceipt(receipt.Identity.Value)!.ValidForLanding);
        Assert.Equal(1, fixture.ConsumptionCount(receipt));

        var second = fixture.Tick(loop);
        Assert.Empty(OutcomeLines(second, receipt));
        Assert.DoesNotContain(fixture.EventLines(), IsEntry);
        Assert.Single(fixture.Launched);

        // Retire the controlled solo child and re-arm all three original Ready candidates.
        // A free slot and a selectable A+C pair make the post-main-change check discriminating.
        fixture.RetireSoloAttempts();
        fixture.Kernel = AgentOrchestratorKernel.FromSnapshot(readySnapshot);
        File.WriteAllText(Path.Combine(fixture.Repo, "unrelated-main.txt"), "main advanced");
        RunGit(fixture.Repo, "add", "unrelated-main.txt");
        RunGit(fixture.Repo, "commit", "-m", "Unrelated main advance");
        Assert.NotEqual(mainBefore, RunGitOutput(fixture.Repo, "rev-parse", "main").Trim());
        fixture.RebuildDriver(enableGrouped: true);
        Assert.Empty(fixture.Driver.ParallelAcceptanceAttemptCoordinator.GetCapacityReservingAttempts(
            fixture.Kernel.Goals.Select(goal => goal.Id.Value).ToArray()));
        var candidates = fixture.Kernel.Goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(
            goal.Id, fixture.Driver.ProjectGateReadyCandidate(goal, RoutingFixture.Policy))).ToArray();
        Assert.All(candidates, candidate => Assert.IsType<GateReadyCandidateProjectionResult.Ready>(candidate.ProjectionResult));
        if (nonAcceptanceObligations)
        {
            var productionCandidates = ConductorBatchLoop.ExcludeGroupedAcceptanceCandidatesWithNonAcceptanceObligations(
                candidates, fixture.Kernel.Goals.ToArray(), new HashSet<string>(), new HashSet<string>());
            Assert.DoesNotContain(productionCandidates,
                candidate => receipt.Identity.Members.Any(member => member.GoalId == candidate.GoalId));
        }
        foreach (var member in receipt.Identity.Members)
        {
            var ready = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                candidates.Single(candidate => candidate.GoalId == member.GoalId).ProjectionResult);
            Assert.Equal(member.CandidateRevision, ready.Projection.CandidateRevision);
        }
        var possiblePair = ConductorAcceptanceCohortSelector.Select(candidates,
            suppressedPairFingerprints: fixture.Store.ReadSuppressedPairs()).Selection;
        Assert.NotNull(possiblePair);
        Assert.Contains(possiblePair.Members, member => receipt.Identity.Members.Any(old => old.GoalId == member.GoalId));

        var third = fixture.Tick(fixture.CreateLoop());
        Assert.Empty(OutcomeLines(third, receipt));
        Assert.DoesNotContain(fixture.EventLines(), IsEntry);
        Assert.Contains(third.ProgressLines!, line => line.Contains("reason=interaction-only-priority", StringComparison.Ordinal)
            && fixture.PairGoals.Any(goal => line.Contains($"goal={goal.Id.Value[..8]}", StringComparison.Ordinal)));
        var restarted = Assert.Single(third.ProgressLines!.Where(line => line.StartsWith("ACCEPTANCE goal=", StringComparison.Ordinal)
            && line.Contains("result=started", StringComparison.Ordinal)));
        Assert.Contains(fixture.PairGoals, goal => restarted.Contains($"goal={goal.Id.Value[..8]}", StringComparison.Ordinal));
        Assert.Equal(2, fixture.Launched.Count);
        Assert.Contains(fixture.PairGoals, goal => goal.Id.Value == fixture.Launched[1].GoalId);
        Assert.Empty(fixture.Landings);
        Assert.Equal(1, fixture.ConsumptionCount(receipt));
    }

    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void PassedReceipt_PrelandsBothMembersOnceWithoutOutcomeConsumption()
    {
        using var fixture = new RoutingFixture();
        fixture.Driver.SetCohortGateBackgroundStartForTests(action => action());
        var selection = ProjectSelection(fixture.Driver, fixture.PairGoals[0], fixture.PairGoals[1]);
        _ = fixture.Driver.RunAcceptanceCohort(selection, fixture.PairGoals, RoutingFixture.Policy, runGateInBackground: true);
        var passed = Assert.Single(fixture.Store.ReadPassedReceiptsForGoal(fixture.PairGoals[0].Id));
        Assert.True(passed.HasAuthoritativeLandingEvidence);
        Assert.Empty(fixture.Driver.GetActiveCohortGateMemberGoalIds());
        var loop = new ConductorBatchLoop();

        var first = fixture.Tick(loop);
        Assert.Single(first.ProgressLines!.Where(line => line.Contains(
            $"prelanded=true outcome=passed receipt={passed.ReceiptId}", StringComparison.Ordinal)));
        Assert.Equal(fixture.PairGoals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal),
            fixture.Landings.Select(landing => landing.GoalId).Order(StringComparer.Ordinal));
        Assert.All(fixture.PairGoals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
        AssertNoFailedOutcome(first);
        Assert.Equal(0, fixture.ConsumptionCount(passed));

        var second = fixture.Tick(loop);
        Assert.DoesNotContain(second.ProgressLines!, line => line.Contains("prelanded=true", StringComparison.Ordinal));
        AssertNoFailedOutcome(second);
        fixture.RebuildDriver();
        var third = fixture.Tick(new ConductorBatchLoop());
        Assert.DoesNotContain(third.ProgressLines!, line => line.Contains("prelanded=true", StringComparison.Ordinal));
        AssertNoFailedOutcome(third);
        Assert.Equal(2, fixture.Landings.Count);
        Assert.Equal(0, fixture.ConsumptionCount(passed));
    }

    private static bool IsEntry(string line) => line.StartsWith("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal);
    private static IEnumerable<string> OutcomeLines(BatchTickSummary tick, AcceptanceCohortReceipt receipt) =>
        tick.ProgressLines!.Where(line => line.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal)
            && line.Contains($"receipt={receipt.ReceiptId}", StringComparison.Ordinal));
    private static void AssertNoFailedOutcome(BatchTickSummary tick) =>
        Assert.DoesNotContain(tick.ProgressLines!, line => line.Contains("outcome=interaction-only", StringComparison.Ordinal)
            || line.Contains("outcome=failed", StringComparison.Ordinal));

    private sealed class RoutingFixture : IDisposable
    {
        internal static ConductorAutonomyPolicy Policy => ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 1 };
        private readonly IDisposable _probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        private readonly WorktreeCleanupContext _cleanup;
        private readonly SequenceAcceptanceVerifier _verifier;
        private readonly ConductorGroupedGateAttemptCoordinator _grouped;
        private readonly HashSet<int> _liveProcesses = [];
        private int _nextPid = 9800;
        internal string Repo { get; } = CreateReducedAcceptanceCohortRepository();
        internal AgentOrchestratorKernel Kernel { get; set; } = new();
        internal ConductorDriver Driver { get; private set; } = null!;
        internal OrchestratorWorkspace Workspace { get; }
        internal CohortAcceptanceStore Store { get; }
        internal Goal[] PairGoals { get; }
        internal List<ConductorLandingReceipt> Landings { get; } = [];
        internal List<ConductorParallelAcceptanceAttempt> Launched { get; } = [];
        private string EventLogPath => Path.Combine(Repo, "interaction-routing-conduct.jsonl");

        internal ConductorBatchLoop CreateLoop() => new(conductEventLogWriter: new ConductEventLogWriter(EventLogPath));

        internal string[] EventLines() => File.ReadAllLines(EventLogPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Detail).ToArray();

        internal RoutingFixture()
        {
            ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();
            var clock = new RoutingClock();
            Kernel = new AgentOrchestratorKernel(clock);
            AddAcceptanceManifest(Repo);
            _cleanup = CreateIsolatedCleanupContext(Repo);
            Workspace = OrchestratorWorkspace.ForDirectory(Repo);
            var paths = new[] { "src/Mcg.AgentOrchestrator.Infrastructure/RoutingA.cs", "tests/RoutingB.cs",
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/RoutingC.cs" };
            var goals = paths.Select((path, index) =>
            {
                clock.UtcNow = DateTimeOffset.Parse("2026-10-06T00:00:00Z").AddMinutes(index);
                var goal = CreateCompletedGoal(Kernel, $"Routing member {index}", Repo);
                var candidate = CreateWorktreeCandidate(Repo, goal.Id, path, $"member {index}");
                Kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id,
                    ManualVerificationRecorder.Create(true, "Passed", Repo,
                        DateTimeOffset.Parse("2026-10-06T00:00:00Z").AddMinutes(index)));
                Kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                Kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
                Kernel.RecordGoalPolicyDecision(goal.Id, "Fixture Verified ordering");
                return goal;
            }).ToArray();
            PairGoals = goals[..2];
            var trx = WritePassingTrx(Repo, "routing-green.trx");
            _verifier = new SequenceAcceptanceVerifier([new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("routing", true, 0, null)], TestResultPaths: [trx])]);
            Store = new CohortAcceptanceStore(Path.Combine(Workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            _grouped = new ConductorGroupedGateAttemptCoordinator(
                Path.Combine(Workspace.OrchestratorDirectory, "grouped-gate-attempts"),
                isProcessAlive: _ => false, buildStorageRoot: _cleanup.Hooks.BuildStorageRoot,
                launch: _ => throw new InvalidOperationException("No new cohort child may start in this fixture."));
            RebuildDriver();
        }

        internal void RebuildDriver(bool enableGrouped = false)
        {
            Driver = new ConductorDriver(Kernel, Workspace, _verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: _cleanup.Hooks);
            Driver.SuccessfulLandingSink = Landings.Add;
            if (enableGrouped) Driver.EnableOwnedGroupedGateAttempts(_grouped);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(Workspace.OrchestratorDirectory, "acceptance-gate-attempts"), Repo,
                isProcessAlive: pid => _liveProcesses.Contains(pid),
                launchOwnedProcess: launch =>
                {
                    Launched.Add(launch.Attempt);
                    var pid = ++_nextPid;
                    _liveProcesses.Add(pid);
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(pid);
                }, buildStorageRoot: _cleanup.Hooks.BuildStorageRoot);
            // Inject the existing coordinator seam without widening the goal's production scope.
            var field = typeof(ConductorDriver).GetField("_parallelAcceptanceAttemptCoordinator",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(Driver, coordinator);
        }

        internal AcceptanceCohortReceipt SeedInteractionOnly()
        {
            var selection = ProjectSelection(Driver, PairGoals[0], PairGoals[1]);
            var identity = AcceptanceCohortIdentity.Create(selection.BindMembers(), selection.Members[0].MainRevision,
                new string('d', 40), "routing-manifest");
            var receipt = Store.SaveGateReceipt(new AcceptanceCohortReceipt("interaction-routing", identity,
                AcceptanceCohortGateOutcome.Failed, DateTimeOffset.Parse("2026-10-06T00:00:00Z"), 0, ["red"], 2, []));
            var partitions = identity.Members.Select((member, ordinal) => new AcceptanceCohortPartitionReceipt(
                $"partition-{ordinal}", member.GoalId, ordinal, member.CandidateRevision, identity.ObservedMainRevision,
                new string('e', 40), identity.ManifestIdentity, AcceptanceCohortGateOutcome.Passed, 0, [])).ToArray();
            receipt = Store.SaveAttribution(identity.Value, AcceptanceCohortAttributionOutcome.InteractionOnly,
                partitions, ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]), null);
            Assert.Equal(2, Store.ReadPartitionReceipts(identity.Value).Count);
            Assert.Single(Store.ReadSuppressedPairs());
            var attempt = _grouped.Create("cohort", selection.Members, identity.ObservedMainRevision,
                identity.CombinedTreeRevision, identity.ManifestIdentity, identity.Value, Repo, Policy)
                with { OwnerProcessId = 7700, OwnerProcessStartedAt = DateTimeOffset.UnixEpoch, OwnerExecutablePath = "controlled-child" };
            ConductorGroupedGateAttemptCoordinator.Save(attempt);
            File.WriteAllText(attempt.ResultPath, JsonSerializer.Serialize(new { attempt.IdentityValue, Status = "completed" }));
            File.WriteAllText(attempt.ExitCodePath, "0");
            Assert.Equal("Running", Assert.Single(_grouped.ReadAll()).Outcome);
            Driver.EnableOwnedGroupedGateAttempts(_grouped);
            return receipt;
        }

        internal BatchTickSummary Tick(ConductorBatchLoop loop)
        {
            BatchTickSummary? observed = null;
            loop.Run(Kernel, Driver, Policy, Path.Combine(Repo, "stop-does-not-exist"), maxIterations: 1,
                onTick: tick => observed = tick);
            return Assert.IsType<BatchTickSummary>(observed);
        }

        internal int ConsumptionCount(AcceptanceCohortReceipt receipt)
        {
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(Workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_outcome_consumptions WHERE cohort_id=$cohort;";
            command.Parameters.AddWithValue("$cohort", receipt.Identity.Value);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        internal void RetireSoloAttempts()
        {
            var attempts = Driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts(
                Kernel.Goals.Select(goal => goal.Id.Value).ToArray());
            Assert.Single(attempts);
            foreach (var attempt in attempts)
            {
                File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt with
                {
                    Outcome = ConductorParallelAcceptanceAttemptOutcome.Cancelled,
                    CompletedAt = DateTimeOffset.Parse("2026-10-06T01:00:00Z"),
                    ReconciledAt = DateTimeOffset.Parse("2026-10-06T01:00:00Z")
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            _liveProcesses.Clear();
        }

        public void Dispose()
        {
            _probe.Dispose();
            DeleteDirectory(Repo);
            ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();
        }
    }

    private sealed class RoutingClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
    }
}
