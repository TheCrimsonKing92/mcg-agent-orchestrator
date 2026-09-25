using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLiveAcceptanceRunClass : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsLiveAcceptanceRunClass(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void FocusedHeartbeatDoesNotOccupyAcceptanceWidth()
    {
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            [new GateLoadContextProbe.LiveGateOccupant(4101, "active-goal", 0, TimeSpan.Zero,
                RunClass: GateHeartbeatRunClass.FocusedEvidence)]);

        var census = Census(GateLoadContextProbe.CaptureLiveGateOccupants());
        var decision = ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, width: 1);

        Assert.True(decision.IsAdmitted, decision.Reason);
        Assert.Equal("live acceptance occupants none", census.Describe());
    }

    [Xunit.Fact]
    public void VerifiedGoalStartsWithFocusedHeartbeatFromActiveGoal()
    {
        using var isolatedRoot = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, DefaultAgents(), "Active focused evidence goal");
        var verified = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/FocusedAdmission.cs");
        var attemptRoot = CreateTempDirectory("mcg-focused-heartbeat-admission");
        Action waitForAttempts = () => { };

        try
        {
            using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
                [new GateLoadContextProbe.LiveGateOccupant(4102, active.Id.Value, 0, TimeSpan.Zero,
                    RunClass: GateHeartbeatRunClass.FocusedEvidence)]);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    if (goal.Id == verified.Id) started.Set();
                    release.Wait(TestContext.Current.CancellationToken);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: goal =>
                    [$"src/Mcg.AgentOrchestrator.App/Orchestration/{goal.Id.Value[..8]}.cs"],
                parallelAcceptanceAttemptCoordinator:
                    ConductorBatchLoopTestsParallelAcceptance.ThreadedAcceptanceAttemptCoordinator(
                        attemptRoot, out waitForAttempts));

            BatchTickSummary? tick = null;
            new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 },
                NoStopPath(), maxIterations: 1, onTick: current => tick = current);

            Assert.True(started.Wait(TimeSpan.FromSeconds(5)),
                string.Join(Environment.NewLine, tick?.ProgressLines ?? []));
            Assert.DoesNotContain(tick!.ProgressLines!, line =>
                line.Contains("acceptance_width_1_reached", StringComparison.Ordinal));
        }
        finally
        {
            release.Set();
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(GateHeartbeatRunClass.Acceptance)]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("unknown-run-class")]
    public void AcceptanceAndUnreadableRunClassesHoldWidth(string? runClass)
    {
        const string goalId = "abcdef12-predecessor-goal";
        var gate = new GateLoadContextProbe.LiveGateOccupant(
            4103, goalId, 0, TimeSpan.Zero, RunClass: runClass);
        var census = Census([gate]);

        Assert.False(ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, 1).IsAdmitted);
        Assert.Equal(
            "acceptance width 1 reached; live acceptance occupants goal:abcdef12; retry on next conduct tick",
            ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, 1).Reason);
    }

    [Xunit.Fact]
    public void AdoptedAttemptAndHeartbeatCountOnce()
    {
        const string goalId = "abcdef12-predecessor-goal";
        var attempt = new ConductorParallelAcceptanceAttempt(
            "attempt-1", goalId, "abcdef12", 0, "branch", "main",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 4104,
            ConductorParallelAcceptanceAttemptOutcome.Running,
            "stdout", "stderr", "exit", "heartbeat", "result", "metadata");
        var gate = new GateLoadContextProbe.LiveGateOccupant(
            4104, goalId, 0, TimeSpan.Zero, RunClass: GateHeartbeatRunClass.Acceptance);
        var census = ConductorBatchLoop.BuildLiveAcceptanceCensus(
            [attempt], new HashSet<string>([attempt.AttemptId], StringComparer.Ordinal),
            new ConductorAcceptanceCapacitySnapshot([]), [gate]);

        Assert.Equal(1, census.OccupiedCount);
        Assert.Equal(
            "acceptance width 1 reached; live acceptance occupants goal:abcdef12; retry on next conduct tick",
            ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, 1).Reason);
    }

    [Xunit.Fact]
    public void CountedHeartbeatWinsWhenSharedProcessAlsoHasFocusedHeartbeat()
    {
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            [
                new GateLoadContextProbe.LiveGateOccupant(4105, "focused-goal", 0, TimeSpan.Zero,
                    RunClass: GateHeartbeatRunClass.FocusedEvidence),
                new GateLoadContextProbe.LiveGateOccupant(4105, "gate-goal", 1, TimeSpan.Zero,
                    RunClass: GateHeartbeatRunClass.Acceptance)
            ]);
        var census = Census(GateLoadContextProbe.CaptureLiveGateOccupants());

        Assert.Equal(1, census.OccupiedCount);
        Assert.Equal("live acceptance occupants goal:gate-goa", census.Describe());
    }

    private static ConductorBatchLoop.LiveAcceptanceCensus Census(
        IReadOnlyList<GateLoadContextProbe.LiveGateOccupant> gates) =>
        ConductorBatchLoop.BuildLiveAcceptanceCensus(
            [], new HashSet<string>(StringComparer.Ordinal),
            new ConductorAcceptanceCapacitySnapshot([]), gates);
}
