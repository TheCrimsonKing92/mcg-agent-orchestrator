using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsFallbackAcceptanceWidthAdmission : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsFallbackAcceptanceWidthAdmission(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact]
    public void DocsCompletion_OccupiedWidthOne_HoldsWithoutLaunching()
        => AssertCompletionTick(width: 1, withOccupant: true, expectLaunch: false);

    [Xunit.Fact]
    public void DocsCompletion_EmptyWidthOne_LaunchesAttempt()
        => AssertCompletionTick(width: 1, withOccupant: false, expectLaunch: true);

    [Xunit.Fact]
    public void DocsCompletion_OneOccupantAtWidthTwo_LaunchesAttempt()
        => AssertCompletionTick(width: 2, withOccupant: true, expectLaunch: true);

    [Xunit.Fact]
    public void OwnRunningAttempt_WidthOne_ObservesWithoutLaunchingAgain()
    {
        using var fixture = new AdmissionFixture();
        var goal = fixture.CreateGoal();
        PassVerification(fixture.Kernel, goal, goal.Tasks.Single());
        fixture.Driver.BeginTick(fixture.Kernel, 1);
        var first = fixture.Driver.AdvanceOnce(goal, Width(1));
        var attempt = Assert.Single(fixture.Attempts(goal));
        Assert.Equal(GoalLifecycleState.Verifying, Held(first).State);

        var second = fixture.Driver.AdvanceOnce(goal, Width(1));

        Assert.Equal(GoalLifecycleState.Verifying, Held(second).State);
        Assert.Equal($"Acceptance verification running in background; attempt={attempt.AttemptId}.",
            Held(second).Reason);
        Assert.Equal(1, fixture.LaunchCount(goal));
        Assert.Equal(attempt.AttemptId, Assert.Single(fixture.Attempts(goal)).AttemptId);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running,
            Assert.Single(fixture.Attempts(goal)).Outcome);
    }

    [Xunit.Fact]
    public void MissingAttempt_OccupiedWidthOne_PreservesVerifyingHoldState()
    {
        using var fixture = new AdmissionFixture();
        var occupant = fixture.StartOccupant();
        var goal = fixture.CreateGoal();
        PassVerification(fixture.Kernel, goal, goal.Tasks.Single());
        fixture.Kernel.BeginGoalAcceptanceVerification(goal.Id, "Previously tracked attempt is missing");
        fixture.Driver.BeginTick(fixture.Kernel, 1);

        var result = fixture.Driver.AdvanceOnce(goal, Width(1));

        Assert.Equal(GoalLifecycleState.Verifying, Held(result).State);
        Assert.Equal(WidthReason(occupant), Held(result).Reason);
        Assert.Equal(GoalStatus.Verifying, goal.Status);
        Assert.Empty(fixture.Attempts(goal));
        Assert.Equal(0, fixture.LaunchCount(goal));
    }

    [Xunit.Fact]
    public void TwoDocsCompletions_SameTickWidthOne_FreshCensusAdmitsOnlyOne()
    {
        using var fixture = new AdmissionFixture();
        var goals = new[] { fixture.CreateGoal(), fixture.CreateGoal() };
        BatchTickSummary? tick = null;
        var completed = new HashSet<GoalId>();
        var loop = new ConductorBatchLoop(refreshGoalDispatchesBeforeAdvance: (kernel, goal) =>
        {
            Assert.NotEqual(GoalStatus.Verified, goal.Status);
            PassVerification(kernel, goal, goal.Tasks.Single());
            completed.Add(goal.Id);
            return null;
        });

        loop.Run(fixture.Kernel, fixture.Driver, Width(1), NoStopPath(),
            maxIterations: 1, onTick: current => tick = current);

        Assert.Equal(2, completed.Count);
        var admitted = Assert.Single(goals.Where(goal => fixture.LaunchCount(goal) == 1));
        var held = Assert.Single(goals.Where(goal => fixture.LaunchCount(goal) == 0));
        Assert.Equal(GoalStatus.Verifying, admitted.Status);
        Assert.Equal(GoalStatus.Verified, held.Status);
        Assert.Single(fixture.Attempts(admitted));
        Assert.Empty(fixture.Attempts(held));
        Assert.Contains(tick!.ProgressLines!, line =>
            line.Contains($"GOAL goal={held.Id.Value[..8]} result=held state=Verified", StringComparison.Ordinal) &&
            line.Contains("acceptance_width_1_reached", StringComparison.Ordinal) &&
            line.Contains($"goal:{admitted.Id.Value[..8]}", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void CensusUnavailable_NewAttempt_HoldsWithoutLaunching()
    {
        using var fixture = new AdmissionFixture();
        var goal = fixture.CreateGoal();
        PassVerification(fixture.Kernel, goal, goal.Tasks.Single());
        fixture.Driver.BeginTick(fixture.Kernel, 1);
        using var failingProbe = GateLoadContextProbe.PushLiveGateOccupantProbe(
            () => throw new IOException("fixture census unreadable"));

        var result = fixture.Driver.AdvanceOnce(goal, Width(1));

        Assert.Equal(GoalLifecycleState.Verified, Held(result).State);
        Assert.Equal("live acceptance census unavailable: fixture_census_unreadable; retry on next conduct tick",
            Held(result).Reason);
        Assert.Empty(fixture.Attempts(goal));
        Assert.Equal(0, fixture.LaunchCount(goal));
    }

    private static ConductorAutonomyPolicy Width(int width)
        => ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = width };

    private static ConductorAdvanceOutcome.Held Held(ConductorAdvanceResult result)
        => Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);

    private static string WidthReason(Goal occupant)
        => $"acceptance width 1 reached; live acceptance occupants goal:{occupant.Id.Value[..8]}; retry on next conduct tick";

    private static void AssertCompletionTick(int width, bool withOccupant, bool expectLaunch)
    {
        using var fixture = new AdmissionFixture();
        var occupant = withOccupant ? fixture.StartOccupant() : null;
        var goal = fixture.CreateGoal();
        var refreshed = false;
        BatchTickSummary? tick = null;
        var loop = new ConductorBatchLoop(refreshGoalDispatchesBeforeAdvance: (kernel, current) =>
        {
            Assert.Equal(goal.Id, current.Id);
            Assert.NotEqual(GoalStatus.Verified, current.Status);
            PassVerification(kernel, current, current.Tasks.Single());
            Assert.Equal(GoalStatus.Verified, current.Status);
            refreshed = true;
            return null;
        });

        loop.Run(fixture.Kernel, fixture.Driver, Width(width), NoStopPath(),
            maxIterations: 1, onlyGoalId: goal.Id.Value, onTick: current => tick = current);

        Assert.True(refreshed);
        Assert.NotNull(tick);
        if (expectLaunch)
        {
            Assert.Equal(1, fixture.LaunchCount(goal));
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running,
                Assert.Single(fixture.Attempts(goal)).Outcome);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Contains(tick.ProgressLines!, line =>
                line.Contains($"GOAL goal={goal.Id.Value[..8]} result=held state=Verifying", StringComparison.Ordinal) &&
                line.Contains("Acceptance_verification_running_in_background", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(0, fixture.LaunchCount(goal));
            Assert.Empty(fixture.Attempts(goal));
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Contains(tick.ProgressLines!, line =>
                line.Contains($"GOAL goal={goal.Id.Value[..8]} result=held state=Verified", StringComparison.Ordinal) &&
                line.Contains("acceptance_width_1_reached", StringComparison.Ordinal) &&
                line.Contains($"goal:{occupant!.Id.Value[..8]}", StringComparison.Ordinal));
            // The driver exposes the unsanitized reason through the same decision on another advance.
            Assert.Equal(WidthReason(occupant!), Held(fixture.Driver.AdvanceOnce(goal, Width(width))).Reason);
        }

        if (occupant is not null)
        {
            Assert.Equal(1, fixture.LaunchCount(occupant));
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running,
                Assert.Single(fixture.Attempts(occupant)).Outcome);
        }
    }

    private sealed class AdmissionFixture : IDisposable
    {
        private readonly string _attemptRoot = CreateTempDirectory("mcg-fallback-width-admission");
        private readonly IDisposable _probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        private readonly Dictionary<string, int> _launchCounts = new(StringComparer.Ordinal);
        private int _nextProcessId = 9800;

        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }
        internal ConductorDriver Driver { get; }

        internal AdmissionFixture()
        {
            Coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                _attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: launch =>
                {
                    var id = launch.Attempt.GoalId;
                    _launchCounts[id] = _launchCounts.GetValueOrDefault(id) + 1;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(++_nextProcessId);
                });
            Driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getLandingFileScopes: _ => ["docs/test-audit/critical-lanes-2026-10.md"],
                parallelAcceptanceAttemptCoordinator: Coordinator);
        }

        internal Goal CreateGoal() => GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            Kernel, DefaultAgents(), "Update docs/test-audit/critical-lanes-2026-10.md");

        internal Goal StartOccupant()
        {
            var goal = CreateGoal();
            PassVerification(Kernel, goal, goal.Tasks.Single());
            Driver.BeginTick(Kernel, 0);
            Assert.Equal(GoalLifecycleState.Verifying, Held(Driver.AdvanceOnce(goal, Width(1))).State);
            Assert.Single(Coordinator.GetCapacityReservingAttempts([goal.Id.Value]));
            return goal;
        }

        internal int LaunchCount(Goal goal) => _launchCounts.GetValueOrDefault(goal.Id.Value);

        internal IReadOnlyList<ConductorParallelAcceptanceAttempt> Attempts(Goal goal)
            => Coordinator.GetUnreconciledAttempts([goal.Id.Value]);

        public void Dispose()
        {
            _probe.Dispose();
            TryDeleteDirectory(_attemptRoot);
        }
    }
}
