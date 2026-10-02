using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsParallelAcceptanceTransientRetryPriority : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsParallelAcceptanceTransientRetryPriority(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void RetryPendingVerifyingGoalTakesSlotBeforeTrainAndCohort()
    {
        using var fixture = new AdmissionFixture();
        var retry = fixture.SeedTransientRetry();
        fixture.AddTrainGoals();
        fixture.SoloGoals.Clear();

        var tick = fixture.RunTick();

        Assert.Empty(fixture.Trains);
        Assert.Empty(fixture.Cohorts);
        Assert.Equal([retry.Id], fixture.SoloGoals.ToArray());
        var attempts = fixture.ReadAttempts(retry);
        Assert.Equal(2, attempts.Length);
        Assert.Contains(attempts, attempt => attempt.AttemptId != fixture.SeededAttempt!.AttemptId);
        var line = Assert.Single(PriorityLines(tick));
        Assert.Equal($"ADMISSION tick=1 result=deferred reason=transient-retry-priority goal={retry.Id.Value[..8]}", line);
    }

    [Xunit.Fact]
    public void WithoutRetryPendingGoalTrainIsAdmittedAsToday()
    {
        using var fixture = new AdmissionFixture();
        var members = fixture.AddTrainGoals();

        var tick = fixture.RunTick();

        AssertTrainAdmitted(fixture, members, tick);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void LiveAttemptForVerifyingGoalDoesNotYieldOrStartSecondAttempt()
    {
        using var fixture = new AdmissionFixture(live: true);
        var retry = fixture.AddGoal("Live", "src/Mcg.AgentOrchestrator.App/Orchestration/Live.cs");
        fixture.RunTick();
        Assert.True(fixture.Entered.Wait(TestHangGuard.Bound, TestContext.Current.CancellationToken),
            "live solo acceptance did not enter its gate");
        Assert.Equal(GoalStatus.Verifying, retry.Status);
        var first = Assert.Single(fixture.ReadAttempts(retry));
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, first.Outcome);
        Assert.Null(first.ReconciledAt);
        Assert.True(File.Exists(first.HeartbeatPath), "live solo acceptance did not publish its heartbeat");
        fixture.AddTrainGoals();

        var tick = fixture.RunTick();

        Assert.Empty(PriorityLines(tick));
        Assert.Equal(first.AttemptId, Assert.Single(fixture.ReadAttempts(retry)).AttemptId);
        Assert.Equal([retry.Id], fixture.SoloGoals.ToArray());
    }

    [Xunit.Fact]
    public void VerifyingGoalWithoutAttemptOnFileDoesNotYield()
    {
        using var fixture = new AdmissionFixture();
        var verifying = fixture.AddGoal("No attempt", "src/Mcg.AgentOrchestrator.App/Orchestration/NoAttempt.cs");
        Assert.True(fixture.Kernel.BeginGoalAcceptanceVerification(verifying.Id, "test verification without an acceptance attempt"));
        Assert.Empty(fixture.ReadAttempts(verifying));
        var members = fixture.AddTrainGoals();

        var tick = fixture.RunTick();

        AssertTrainAdmitted(fixture, members, tick);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void RetryPendingGoalRejectedBySoloPathDoesNotYield()
    {
        using var fixture = new AdmissionFixture();
        var retry = fixture.SeedTransientRetry();
        fixture.UnavailableGoal = retry.Id;
        var members = fixture.AddTrainGoals();
        fixture.SoloGoals.Clear();

        var tick = fixture.RunTick();

        AssertTrainAdmitted(fixture, members, tick);
        Assert.Empty(fixture.SoloGoals);
        Assert.Single(fixture.ReadAttempts(retry));
        Assert.Contains(tick.ProgressLines!, line =>
            line.Contains($"reason=parallel-acceptance-candidate goal={retry.Id.Value[..8]}", StringComparison.Ordinal));
    }

    private static IEnumerable<string> PriorityLines(BatchTickSummary tick) =>
        tick.ProgressLines!.Where(line => line.Contains("reason=transient-retry-priority", StringComparison.Ordinal));

    private static void AssertTrainAdmitted(AdmissionFixture fixture, Goal[] members, BatchTickSummary tick)
    {
        Assert.Equal(members.Select(goal => goal.Id).OrderBy(id => id.Value),
            Assert.Single(fixture.Trains).OrderBy(id => id.Value));
        Assert.Empty(fixture.Cohorts);
        Assert.Empty(PriorityLines(tick));
    }

    private sealed class AdmissionFixture : IDisposable
    {
        private const string MainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly ConductorBatchLoopTestsParallelAcceptance.EnvVarScope _isolatedRoot =
            ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        private readonly string _attemptRoot = CreateTempDirectory("mcg-transient-retry-priority");
        private readonly Dictionary<GoalId, IReadOnlyList<string>> _paths = new();
        private readonly Action _waitForAttempts;
        private readonly ConductorDriver _driver;
        private readonly bool _live;
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal ConcurrentQueue<GoalId> SoloGoals { get; } = new();
        internal List<GoalId[]> Trains { get; } = new();
        internal List<GoalId[]> Cohorts { get; } = new();
        internal GoalId? UnavailableGoal { get; set; }
        internal ConductorParallelAcceptanceAttempt? SeededAttempt { get; private set; }
        internal ManualResetEventSlim Entered { get; } = new(false);
        private ManualResetEventSlim Release { get; } = new(false);

        internal AdmissionFixture(bool live = false)
        {
            _live = live;
            ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();
            ConductorParallelAcceptanceAttemptCoordinator coordinator;
            if (live)
            {
                coordinator = ConductorBatchLoopTestsParallelAcceptance.ThreadedAcceptanceAttemptCoordinator(
                    _attemptRoot, out var waitForAttempts);
                _waitForAttempts = waitForAttempts;
            }
            else
            {
                coordinator = new ConductorParallelAcceptanceAttemptCoordinator(_attemptRoot, runInline: true);
                _waitForAttempts = () => { };
            }
            var projector = new GateReadyCandidateProjector(
                id => new GateReadyCandidateRevisionPair(BranchRevision(id), MainRevision),
                id => new GateReadyLandingScopeObservation(true, _paths[id]),
                (_, _, _) => new GateReadyMergeTreeObservation(true));
            _driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    SoloGoals.Enqueue(goal.Id);
                    if (_live)
                    {
                        Entered.Set();
                        Release.Wait(TestContext.Current.CancellationToken);
                        return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                    }
                    throw new AcceptanceInfrastructureDeferredException(
                        "trusted-main-build-failed", 1, "test transient deferral");
                },
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: goal => _paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                resolveAcceptanceHeads: goal => goal.Id == UnavailableGoal
                    ? throw new InvalidOperationException("goal worktree missing")
                    : (BranchRevision(goal.Id), MainRevision),
                parallelAcceptanceAttemptCoordinator: coordinator,
                runMergeTrain: (selection, _, policy) =>
                {
                    var ids = selection.Members.Select(member => member.GoalId).ToArray();
                    Trains.Add(ids);
                    return new ConductorMergeTrainRunResult(null, MemberResults(ids, policy), [], "outcome=passed");
                },
                runAcceptanceCohort: (selection, _, policy) =>
                {
                    var ids = selection.Members.Select(member => member.GoalId).ToArray();
                    Cohorts.Add(ids);
                    return new ConductorAcceptanceCohortRunResult(null, MemberResults(ids, policy), "outcome=passed");
                });
        }

        internal Goal AddGoal(string name, string path)
        {
            var goal = CreateVerifiedSimpleGoal(Kernel, name);
            Kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                ["Worker checks output"], VerificationClass.TestVerifiable, [], []));
            _paths.Add(goal.Id, [path]);
            return goal;
        }

        internal Goal[] AddTrainGoals() =>
        [
            AddGoal("Train first", "tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs"),
            AddGoal("Train second", "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs"),
            AddGoal("Train third", "tests/Mcg.AgentOrchestrator.Dashboard.Tests/TrainThird.cs")
        ];

        internal Goal SeedTransientRetry()
        {
            var goal = AddGoal("Transient retry", "src/Mcg.AgentOrchestrator.App/Orchestration/Retry.cs");
            RunTick();
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            SeededAttempt = Assert.Single(ReadAttempts(goal));
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred, SeededAttempt.Outcome);
            Assert.Equal(1, SeededAttempt.TransientFailureCount);
            Assert.NotNull(SeededAttempt.ReconciledAt);
            return goal;
        }

        internal BatchTickSummary RunTick()
        {
            BatchTickSummary? tick = null;
            new ConductorBatchLoop().Run(Kernel, _driver,
                ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 1 }, NoStopPath(),
                maxIterations: 1, onTick: observed => tick = observed);
            return Assert.IsType<BatchTickSummary>(tick);
        }

        internal ConductorParallelAcceptanceAttempt[] ReadAttempts(Goal goal)
        {
            var directory = Path.Combine(_attemptRoot, goal.Id.Value);
            return !Directory.Exists(directory) ? [] : Directory.EnumerateFiles(directory, "*.attempt.json")
                .Select(path => JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                    File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();
        }

        private static string BranchRevision(GoalId id) => id.Value.PadRight(40, 'b')[..40];

        private static Dictionary<string, ConductorAdvanceResult> MemberResults(
            IReadOnlyList<GoalId> ids, ConductorAutonomyPolicy policy) => ids.ToDictionary(id => id.Value,
            id => new ConductorAdvanceResult(id.Value, id.Value[..8], policy.Name,
                new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "admitted grouped gate")),
            StringComparer.Ordinal);

        public void Dispose()
        {
            Release.Set();
            try { _waitForAttempts(); }
            finally
            {
                Entered.Dispose();
                Release.Dispose();
                TryDeleteDirectory(_attemptRoot);
                _isolatedRoot.Dispose();
                ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();
            }
        }
    }
}
