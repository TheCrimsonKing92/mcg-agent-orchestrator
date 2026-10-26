using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using HeartbeatFixture = AttributionOccupancyHeartbeatTests.HeartbeatFixture;
using Scenario = ActivationRemoteLaneTailAdoptionScenarioTests.Scenario;

// Parallel-safe: fixture-owned git repositories, storage, clocks and scripted supervisor processes.
public sealed class FocusedAttributionActivationScenarioTests : AcceptanceCohortWorkflowTests
{
    private static readonly string[] Identities = ["Tests.First.Fails", "Tests.Second.Fails"];

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task RunAcceptanceCohort_AdvancingFocusedChecks_KeepSuccessorHealthy()
    {
        var (samples, verdicts) = RunCohort(stopOutput: false);
        Assert.Equal(18, samples.Count);
        Assert.All(samples, sample =>
        {
            Assert.Equal(1, sample.Occupied);
            Assert.True(sample.LiveGates >= 1);
        });
        Assert.All(verdicts, verdict => Assert.Equal(ActivationAdmissionHealthState.Healthy, verdict.State));

        await using var scenario = new Scenario();
        await scenario.StartAsync();
        foreach (var sample in samples)
        {
            if (sample.Tick > 1) scenario.Clock.Advance(TimeSpan.FromSeconds(30));
            await scenario.HeldTickAsync(sample.Tick, "admitted", waiting: 1, sample: sample);
        }
        scenario.Clock.Advance(TimeSpan.FromSeconds(30));
        await scenario.HeldTickAsync(19, "terminal", waiting: 1, sample: new(19, false, 1, 0, 0));
        scenario.Clock.Advance(TimeSpan.FromSeconds(30));
        scenario.Tick(20, "released", waiting: 1, sample: new(20, false, 1, 0, 0));
        await scenario.AdoptedAsync();
        Assert.Contains(scenario.Lines, line => line.StartsWith("ACTIVATION_ADOPTED ", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Lines, line => line.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal));
        await scenario.FinishAsync();
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task RunAcceptanceCohort_RepeatedFocusedProgress_StillRevertsStalledSuccessor()
    {
        var (samples, verdicts) = RunCohort(stopOutput: true);
        Assert.Equal(20, samples.Count);
        Assert.All(samples, sample =>
        {
            Assert.Equal(1, sample.Occupied);
            Assert.Equal(sample.Tick <= 6 ? 1 : 0, sample.LiveGates);
        });
        Assert.Contains(verdicts, verdict => verdict.State == ActivationAdmissionHealthState.Tripped
            && verdict.Reason == ConductorActivationRevertReason.AdmissionStalled);

        await using var scenario = new Scenario();
        await scenario.StartAsync();
        var reverted = false;
        foreach (var sample in samples)
        {
            if (sample.Tick > 1) scenario.Clock.Advance(TimeSpan.FromSeconds(30));
            if (!await scenario.ObserveAsync(sample)) { reverted = true; break; }
        }
        Assert.True(reverted, "Stopped focused output never produced AdmissionStalled.");
        Assert.Contains(scenario.Lines, line => line.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal)
            && line.Contains("reason=AdmissionStalled", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Lines, line => line.StartsWith("ACTIVATION_ADOPTED ", StringComparison.Ordinal));
    }

    [Fact]
    public void Census_AttributionMember_IsLiveWithoutDuplicatingCohortOccupancy()
    {
        using var fixture = new HeartbeatFixture();
        using var probe = ReadHeartbeatFiles(fixture.Storage, fixture.Clock);
        var baseline = Census(HeartbeatFixture.Cohort, HeartbeatFixture.Goal, "peer", []);
        using var writer = fixture.Start();
        var occupants = GateLoadContextProbe.CaptureLiveGateOccupants(fixture.Storage, fixture.Clock.GetUtcNow());
        Assert.Single(occupants);
        Assert.Equal(1, occupants.Count(occupant => occupant.CountsAsAcceptanceOccupant));
        Assert.Equal(1, baseline.OccupiedCount);
        Assert.Equal(baseline.OccupiedCount,
            Census(HeartbeatFixture.Cohort, HeartbeatFixture.Goal, "peer", occupants).OccupiedCount);

        GateHeartbeatArtifacts.Write(fixture.Path, fixture.Snapshot() with { RunClass = GateHeartbeatRunClass.FocusedEvidence });
        var focused = GateLoadContextProbe.CaptureLiveGateOccupants(fixture.Storage, fixture.Clock.GetUtcNow());
        Assert.Single(focused);
        Assert.Equal(0, focused.Count(occupant => occupant.CountsAsAcceptanceOccupant));
    }

    private (List<ActivationAdmissionSample> Samples, List<ActivationAdmissionHealthVerdict> Verdicts)
        RunCohort(bool stopOutput)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "Attribution first member", repo);
            var second = CreateCompletedGoal(kernel, "Attribution second member", repo);
            CreateWorktreeCandidate(repo, first.Id, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            CreateWorktreeCandidate(repo, second.Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var hooks = CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks;
            var storage = Assert.IsType<DotnetBuildStorageRoot>(hooks.BuildStorageRoot);
            var clock = new ManualRemoteLaneClock();
            var tracker = new ActivationAdmissionHealthTracker();
            var samples = new List<ActivationAdmissionSample>();
            var verdicts = new List<ActivationAdmissionHealthVerdict>();
            string? cohortId = null;
            var verifier = new FocusedVerifier(repo, first.Id, (goalId, owner) =>
            {
                // Read the file before stepping so the active root has the real cohort identity.
                var snapshot = Assert.Single(AttributionSnapshots(storage));
                Assert.Equal(goalId.Value, snapshot.GoalId);
                cohortId ??= snapshot.RunId;
                Assert.Equal(cohortId, snapshot.RunId);
                var memberZero = goalId == first.Id;
                var steps = stopOutput ? (memberZero ? 20 : 0) : (memberZero ? 10 : 8);
                var started = clock.GetUtcNow();
                var progressAt = started;
                for (var step = 1; step <= steps; step++)
                {
                    clock.Advance(TimeSpan.FromSeconds(30));
                    if (!stopOutput || step <= 2) progressAt = clock.GetUtcNow();
                    owner.ReportProgress(new(goalId.Value, "test", $"focused-{step}", 0,
                        Environment.ProcessId, Environment.ProcessId, started, clock.GetUtcNow(), progressAt,
                        clock.GetUtcNow() - started, step, ""));
                    var occupants = GateLoadContextProbe.CaptureLiveGateOccupants(storage, clock.GetUtcNow());
                    var sample = new ActivationAdmissionSample(samples.Count + 1, false, 1,
                        Census(cohortId!, first.Id.Value, second.Id.Value, occupants).OccupiedCount,
                        occupants.Count(occupant => occupant.CountsAsAcceptanceOccupant));
                    samples.Add(sample);
                    verdicts.Add(tracker.Observe(sample, clock.GetUtcNow()));
                }
            });
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: hooks, utcNow: clock.GetUtcNow);
            var leases = new List<DotnetBuildEnvironmentLease>();
            driver.CohortPartitionStableSlotLeaseSource = (_, _) =>
            {
                Assert.All(leases, lease => Assert.False(lease.IsExecutionLockHeld));
                var lease = AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(repo);
                leases.Add(lease);
                return lease;
            };
            using var probe = ReadHeartbeatFiles(storage, clock);
            var result = driver.RunAcceptanceCohort(ProjectSelection(driver, first, second),
                [first, second], ConductorAutonomyPolicy.Permissive);
            Assert.Equal(1, verifier.FullRuns);
            Assert.Equal(new[] { first.Id, second.Id }, verifier.FocusedGoals);
            Assert.Equal(AcceptanceCohortAttributionSources.FocusedPass, result.Receipt?.AttributionSource);
            Assert.Equal(AcceptanceCohortAttributionOutcome.FirstMemberFailed, result.Receipt?.Attribution);
            Assert.Equal(result.Receipt?.Identity.Value, cohortId);
            Assert.Equal(2, leases.Count);
            Assert.All(leases, lease => Assert.False(lease.IsExecutionLockHeld));
            Assert.Empty(AttributionSnapshots(storage));
            AssertNoCohortWorkspaces(repo);
            return (samples, verdicts);
        }
        finally { DeleteDirectory(repo); }
    }

    private static GateHeartbeatSnapshot[] AttributionSnapshots(DotnetBuildStorageRoot storage) =>
        Directory.Exists(storage.RootPath)
            ? Directory.GetFiles(storage.RootPath, "*.json", SearchOption.AllDirectories)
                .Select(GateHeartbeatArtifacts.TryReadSnapshot)
                .OfType<GateHeartbeatSnapshot>().Where(snapshot => snapshot.Phase == "cohort-attribution").ToArray() : [];

    private static IDisposable ReadHeartbeatFiles(DotnetBuildStorageRoot storage, ManualRemoteLaneClock clock) =>
        GateLoadContextProbe.PushLiveGateOccupantProbe(() => GateLoadContextProbe.ReadLiveGateOccupants(storage, clock.GetUtcNow()));

    private static ConductorBatchLoop.LiveAcceptanceCensus Census(string cohort, string first, string second,
        IReadOnlyList<GateLoadContextProbe.LiveGateOccupant> occupants) =>
        ConductorBatchLoop.BuildLiveAcceptanceCensus([], new HashSet<string>(),
            new([new(cohort, new HashSet<string> { first, second })]), occupants);

    private static AcceptanceVerificationResult CohortFailure(string repo) =>
        new(false, false, 1, "cohort red", Checks:
        [new AcceptanceCheckResult("cohort", false, 1, "cohort red", FailingTestIdentities: Identities,
            TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")],
            TestResultPaths: [WriteFailingTrx(repo, "cohort-attribution-red.trx")]);

    private sealed class FocusedVerifier(string repo, GoalId reproducer,
        Action<GoalId, IAcceptanceRunExecutionContext> runSteps) : IGoalAcceptanceVerifier
    {
        internal int FullRuns;
        internal readonly List<GoalId> FocusedGoals = [];
        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            FullRuns++;
            Assert.Null(goalId);
            return Task.FromResult(CohortFailure(repo));
        }
        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false)
        {
            Assert.NotNull(goalId);
            Assert.Equal(0, stableSlotIndex);
            Assert.True(stableSlotLease!.IsExecutionLockHeld);
            Assert.False(runBaselineArm);
            FocusedGoals.Add(goalId);
            runSteps(goalId, Assert.IsAssignableFrom<IAcceptanceRunExecutionContext>(executionOwner));
            var passed = goalId != reproducer;
            var check = new AcceptanceCheckResult("focused", passed, passed ? 0 : 1, null,
                FailingTestIdentities: passed ? [] : Identities, ExecutedTestCount: 2);
            return Task.FromResult(new FocusedEvidenceRunResult(request, true, passed, "controlled focused result", [check]));
        }
    }
}
