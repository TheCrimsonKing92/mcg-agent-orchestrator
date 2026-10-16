using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsAcceptanceCohortBackgroundAttributionVerdicts : AcceptanceCohortWorkflowTests
{
    private const string FailingTest = "Tests.BackgroundAttributedFailure";

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ChildPublishedFailure_ConsumingTickRoutesAndExcludesGuiltyMember()
    {
        using var fixture = new RoutingFixture();
        var receipt = fixture.SeedAttributedFailure();
        var guilty = fixture.PairGoals[0];
        var partner = fixture.PairGoals[1];
        Assert.All(fixture.Kernel.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
        Assert.Null(guilty.LatestAcceptanceFailure);
        var candidates = fixture.Kernel.Goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(
            goal.Id, fixture.Driver.ProjectGateReadyCandidate(goal, RoutingFixture.Policy))).ToArray();
        Assert.All(candidates, candidate =>
            Assert.IsType<GateReadyCandidateProjectionResult.Ready>(candidate.ProjectionResult));
        var before = ConductorSpeculativeAcceptanceCohortPlanner.Plan(candidates).FormatReceipt(0);
        Assert.Contains($"LandingPathOverlap(conflict={guilty.Id.Value[..8]}", before);

        var loop = fixture.CreateLoop();
        fixture.Tick(loop);

        Assert.Equal(GoalStatus.AcceptanceFailed, guilty.Status);
        var attribution = Assert.Single(guilty.LatestAcceptanceFailure!.CheckAttributions!);
        Assert.Contains($"reproduced={FailingTest}", attribution.Evidence);
        Assert.Contains(FailingTest, guilty.LatestAcceptanceFailure.FailedChecks);
        // The healthy partner advances to its own background gate while the guilty member routes.
        Assert.Equal(GoalStatus.Verifying, partner.Status);
        Assert.Null(partner.LatestAcceptanceFailure);
        var partnerAttempt = Assert.Single(fixture.Launched);
        Assert.Equal(partner.Id.Value, partnerAttempt.GoalId);
        var recorded = Assert.Single(VerdictLines(fixture.EventLines(), guilty));
        Assert.Contains("action=recorded reason=identity-match", recorded);
        Assert.Empty(VerdictLines(fixture.EventLines(), partner));
        Assert.Contains(fixture.EventLines(), line => line.StartsWith(
            "ACCEPTANCE_COHORT_CHILD_COMPLETED ", StringComparison.Ordinal) &&
            line.Contains("reason=child-result-published", StringComparison.Ordinal) &&
            line.Contains($"receipt={receipt.ReceiptId}", StringComparison.Ordinal));
        Assert.Equal(1, fixture.ConsumptionCount(receipt));
        AssertExcluded(fixture.EventLines(), guilty, expectedPlanCount: 1);
        Assert.Empty(fixture.Landings);
        Assert.DoesNotContain(fixture.Launched, attempt => attempt.GoalId == guilty.Id.Value);

        fixture.Tick(loop);

        AssertExcluded(fixture.EventLines(), guilty, expectedPlanCount: 2);
        Assert.Equal(GoalStatus.AcceptanceFailed, guilty.Status);
        Assert.Equal(GoalStatus.Verifying, partner.Status);
        Assert.Null(partner.LatestAcceptanceFailure);
        Assert.Same(partnerAttempt, Assert.Single(fixture.Launched));
        Assert.Single(VerdictLines(fixture.EventLines(), guilty));
        Assert.Equal(1, fixture.ConsumptionCount(receipt));
        Assert.Empty(fixture.Landings);
        Assert.DoesNotContain(fixture.Launched, attempt => attempt.GoalId == guilty.Id.Value);
        fixture.RebuildDriver();
        Assert.Empty(fixture.Driver.ConsumeTerminalFailedCohortReceipts());
        Assert.Single(VerdictLines(fixture.EventLines(), guilty));
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ChildPublishedFailure_OutsideScopedWalkIsCheckpointedOnConsumingTick()
    {
        using var fixture = new RoutingFixture();
        var receipt = fixture.SeedAttributedFailure();
        var guilty = fixture.PairGoals[0];
        var partner = fixture.PairGoals[1];
        var initialTimeline = guilty.Timeline.ToArray();
        var savedGoalIds = new HashSet<GoalId>();
        OrchestratorSnapshot? saved = null;
        var tickCompleted = false;

        fixture.CreateLoop().Run(fixture.Kernel, fixture.Driver, RoutingFixture.Policy,
            Path.Combine(fixture.Repo, "stop-does-not-exist"), maxIterations: 1,
            onlyGoalId: partner.Id.Value,
            onTick: _ => tickCompleted = true,
            checkpointGoalTick: (current, requested) =>
            {
                // A later detach checkpoint cannot satisfy the consuming-tick requirement.
                if (!tickCompleted)
                {
                    savedGoalIds.UnionWith(requested);
                    if (requested.Contains(guilty.Id))
                        saved = JsonSerializer.Deserialize<OrchestratorSnapshot>(
                            JsonSerializer.Serialize(current.ExportSnapshot()));
                }
                return requested.Select(goalId => new GoalSnapshotCheckpointResult(
                    goalId.Value, GoalSnapshotCheckpointDisposition.Durable, null, "state",
                    Path.Combine(fixture.Workspace.OrchestratorDirectory, "state.db"),
                    "fixture-checkpoint")).ToArray();
            });

        Assert.True(tickCompleted);
        Assert.Contains(guilty.Id, savedGoalIds);
        Assert.NotNull(saved);
        var restored = AgentOrchestratorKernel.FromSnapshot(saved).GetGoal(guilty.Id);
        Assert.Contains(FailingTest, restored.RetainedAcceptanceFailure!.FailedChecks);
        // Recording alone leaves Status and Timeline unchanged; routing A is outside this walk.
        Assert.Equal(GoalStatus.Verified, guilty.Status);
        Assert.Equal(initialTimeline, guilty.Timeline);
        Assert.Null(partner.LatestAcceptanceFailure);
        Assert.Contains("action=recorded reason=identity-match",
            Assert.Single(VerdictLines(fixture.EventLines(), guilty)));
        Assert.Equal(1, fixture.ConsumptionCount(receipt));
        Assert.DoesNotContain(fixture.Launched, attempt => attempt.GoalId == guilty.Id.Value);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData("candidate-changed")]
    [InlineData("main-changed")]
    [InlineData("not-attributed")]
    public void ConsumeReceipt_IdentityOrAttributionMismatchEmitsOneSkip(string reason)
    {
        using var fixture = new RoutingFixture();
        var receipt = fixture.SeedAttributedFailure(unrelatedTests: reason == "not-attributed");
        var guilty = fixture.PairGoals[0];
        if (reason == "candidate-changed")
            CreateWorktreeCandidate(fixture.Repo, guilty.Id, "moved-candidate.txt", "candidate advanced");
        if (reason == "main-changed")
        {
            File.WriteAllText(Path.Combine(fixture.Repo, "moved-main.txt"), "main advanced");
            RunGit(fixture.Repo, "add", "moved-main.txt");
            RunGit(fixture.Repo, "commit", "-m", "Main advanced after cohort");
        }
        var lines = new List<string>();
        fixture.Driver.CohortAttributionVerdictSink = lines.Add;

        var consumed = Assert.Single(fixture.Driver.ConsumeTerminalFailedCohortReceipts());

        Assert.Equal(receipt.ReceiptId, consumed.Receipt.ReceiptId);
        Assert.Empty(consumed.RecordedGoalIds);
        Assert.Contains($"action=skipped reason={reason}", Assert.Single(VerdictLines(lines, guilty)));
        Assert.Single(lines);
        Assert.All(fixture.PairGoals, goal =>
        {
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
        });
        Assert.Equal(1, fixture.ConsumptionCount(receipt));
        Assert.Empty(fixture.Driver.ConsumeTerminalFailedCohortReceipts());
        Assert.Single(lines);
    }

    private static IEnumerable<string> VerdictLines(IEnumerable<string> lines, Goal goal) =>
        lines.Where(line => line.StartsWith(
            $"ACCEPTANCE_COHORT_ATTRIBUTION_VERDICT goal={goal.Id.Value} ", StringComparison.Ordinal));

    private static void AssertExcluded(IEnumerable<string> eventLines, Goal guilty, int expectedPlanCount)
    {
        // Speculative plans are emitted to the conduct event log, not the tick summary accumulator.
        var plans = eventLines.Where(line =>
            line.StartsWith("SPECULATIVE_COHORT_PLAN ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(expectedPlanCount, plans.Length);
        Assert.All(plans, line =>
        {
            var members = line.Split(' ').Single(token => token.StartsWith("members=", StringComparison.Ordinal));
            Assert.DoesNotContain(guilty.Id.Value[..8], members);
            Assert.DoesNotContain($"LandingPathOverlap(conflict={guilty.Id.Value[..8]}", line);
        });
    }

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
        private string EventLogPath => Path.Combine(Repo, "background-attribution-conduct.jsonl");

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
                "src/Mcg.AgentOrchestrator.Infrastructure/RoutingA.cs" };
            var goals = paths.Select((path, index) =>
            {
                clock.UtcNow = DateTimeOffset.Parse("2026-10-06T00:00:00Z").AddMinutes(index);
                var goal = CreateCompletedGoal(Kernel, $"Routing member {index}", Repo);
                var candidate = CreateWorktreeCandidate(Repo, goal.Id, path, $"member {index}");
                if (index == 2)
                    candidate = CreateWorktreeCandidate(Repo, goal.Id, paths[1], "overlap partner B");
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
                eventSink: line => new ConductEventLogWriter(EventLogPath).Append("acceptance-cohort", null, line),
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
                Path.Combine(Workspace.OrchestratorDirectory, "acceptance-gate-attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, Repo,
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

        internal AcceptanceCohortReceipt SeedAttributedFailure(bool unrelatedTests = false)
        {
            var selection = ProjectSelection(Driver, PairGoals[0], PairGoals[1]);
            var identity = AcceptanceCohortIdentity.Create(selection.BindMembers(), selection.Members[0].MainRevision,
                new string('d', 40), "routing-manifest");
            var receipt = Store.SaveGateReceipt(new AcceptanceCohortReceipt("background-attribution", identity,
                AcceptanceCohortGateOutcome.Failed, DateTimeOffset.Parse("2026-10-06T00:00:00Z"), 0, [FailingTest], 1, []));
            var partitions = identity.Members.Select((member, ordinal) => new AcceptanceCohortPartitionReceipt(
                $"partition-{ordinal}", member.GoalId, ordinal, member.CandidateRevision, identity.ObservedMainRevision,
                new string('e', 40), identity.ManifestIdentity,
                ordinal == 0 ? AcceptanceCohortGateOutcome.Failed : AcceptanceCohortGateOutcome.Passed, 0, [])
            {
                FailedChecks = ordinal == 0 ? [FailingTest] : [],
                FailingTestIdentities = ordinal == 0 ? [unrelatedTests ? "Tests.Unrelated" : FailingTest] : []
            }).ToArray();
            var guilty = identity.Members[0];
            receipt = Store.SaveAttribution(identity.Value, AcceptanceCohortAttributionOutcome.FirstMemberFailed,
                partitions, ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
                PairGoals[1].Id,
                attributedMembers: [new AcceptanceCohortAttributedMember(
                    guilty.GoalId, 0, guilty.CandidateRevision, [FailingTest])]);
            Assert.Equal(PairGoals[0].Id, guilty.GoalId);
            Assert.Equal(2, Store.ReadPartitionReceipts(identity.Value).Count);
            var attempt = _grouped.Create("cohort", selection.Members, identity.ObservedMainRevision,
                identity.CombinedTreeRevision, identity.ManifestIdentity, identity.Value, Repo, Policy)
                with { OwnerProcessId = 7700, OwnerProcessStartedAt = DateTimeOffset.UnixEpoch, OwnerExecutablePath = "controlled-child" };
            ConductorGroupedGateAttemptCoordinator.Save(attempt);
            File.WriteAllText(attempt.ResultPath, JsonSerializer.Serialize(new
            {
                attempt.IdentityValue, Status = "completed", Verdict = "failed", receipt.ReceiptId
            }));
            File.WriteAllText(attempt.ExitCodePath, "0");
            Assert.Equal("Running", Assert.Single(_grouped.ReadAll()).Outcome);
            Assert.NotNull(_grouped.TryReconcileDead(attempt, "child-result-published").ReconciledAt);
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
