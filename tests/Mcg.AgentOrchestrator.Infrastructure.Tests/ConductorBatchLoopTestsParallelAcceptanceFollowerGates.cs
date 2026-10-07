using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsParallelAcceptanceFollowerGates(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void DisabledMatchesAbsentPropertyAndNeverStartsFollower()
    {
        var off = RunScenario(false, 2);
        var absent = RunScenario(false, 2, omitProperty: true);
        Assert.Equal(0, off.Starts);
        Assert.Equal(0, absent.Starts);
        Assert.Equal(off.FollowerStatus, absent.FollowerStatus);
        Assert.Equal(off.FollowerHold, absent.FollowerHold);
        Assert.DoesNotContain(off.Lines, line => line.StartsWith("FOLLOWER_GATE ", StringComparison.Ordinal));
    }

    [Fact]
    public void WidthOneNeverStartsFollower()
    {
        var result = RunScenario(true, 1);
        Assert.Equal(0, result.Starts);
        Assert.Contains(result.Lines, line => line.Contains("reason=NoSecondSlot", StringComparison.Ordinal));
        Assert.False(result.FollowerSoloStarted);
    }

    [Fact]
    public void WidthTwoStartsOneOverlappingFollowerAndHoldsIt()
    {
        var result = RunScenario(true, 2, repeatTick: true);
        Assert.Equal(1, result.Starts);
        Assert.Contains(result.Lines, line => line.Contains("reason=follower-gate-running", StringComparison.Ordinal));
        Assert.Contains("follower-gate-running", result.FollowerHold);
        Assert.False(result.FollowerSoloStarted);
        Assert.Equal(GoalStatus.Verifying, result.LeaderStatus);
    }

    [Fact]
    public void ConflictLeavesFollowerForUnchangedSoloAdmission()
    {
        var off = RunScenario(false, 2);
        var conflict = RunScenario(true, 2, conflict: true);
        Assert.Equal(1, conflict.Starts);
        Assert.Equal(off.FollowerStatus, conflict.FollowerStatus);
        Assert.Equal(off.FollowerHold, conflict.FollowerHold);
        Assert.False(conflict.FollowerSoloStarted);
        Assert.Contains(conflict.Lines, line => line.Contains("reason=Conflict", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FollowerGateRunOutcome.Passed)]
    [InlineData(FollowerGateRunOutcome.Failed)]
    public void CompletedFollowerWaitsForPendingLeader(FollowerGateRunOutcome outcome)
    {
        var result = RunScenario(true, 2, newestOutcome: outcome);
        Assert.Equal(0, result.Starts);
        Assert.Contains("follower-awaiting-leader", result.FollowerHold);
        Assert.Contains(result.Lines, line => line.Contains("reason=follower-awaiting-leader", StringComparison.Ordinal));
        Assert.False(result.FollowerSoloStarted);
    }

    [Fact]
    public void MovedLeaderProjectionCannotStartFollower()
    {
        var result = RunScenario(true, 2, moveLeader: true);
        Assert.Equal(0, result.Starts);
        Assert.Contains(result.Lines, line => line.Contains("reason=leader-projection-mismatch", StringComparison.Ordinal));
        Assert.False(result.FollowerSoloStarted);
    }

    private sealed record TickResult(int Starts, GoalStatus LeaderStatus, GoalStatus FollowerStatus,
        string FollowerHold, bool FollowerSoloStarted, IReadOnlyList<string> Lines);

    private TickResult RunScenario(bool enabled, int width, bool omitProperty = false, bool conflict = false,
        FollowerGateRunOutcome? newestOutcome = null, bool repeatTick = false, bool moveLeader = false)
    {
        using var isolatedRoot = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var kernel = new AgentOrchestratorKernel();
        var leader = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/Shared.cs leader");
        var follower = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/Shared.cs follower");
        const string main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var leaderHead = new string('b', 40);
        var followerHead = new string('c', 40);
        string[] paths = ["src/Mcg.AgentOrchestrator.App/Orchestration/Shared.cs"];
        var projector = new GateReadyCandidateProjector(
            id => new(id == leader.Id ? leaderHead : followerHead, main),
            _ => new(true, paths), (_, _, _) => new(true));
        var root = CreateTempDirectory("mcg-follower-admission");
        Action waitForAttempts = () => { };
        try
        {
            var coordinator = ConductorBatchLoopTestsParallelAcceptance.ThreadedAcceptanceAttemptCoordinator(root, out waitForAttempts);
            var soloFollower = false;
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    if (goal.Id == follower.Id) soloFollower = true;
                    entered.Set();
                    Assert.True(release.Wait(TestHangGuard.Bound), "leader acceptance release did not happen");
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                }, classifyRisk: _ => ChangeRiskTier.DocsOnly, getLandingFileScopes: _ => paths,
                isVerificationGateSatisfied: _ => true, gateReadyCandidateProjector: projector,
                resolveAcceptanceHeads: goal => (goal.Id == leader.Id ? leaderHead : followerHead, main),
                parallelAcceptanceAttemptCoordinator: coordinator);
            var policy = ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = width, FollowerGatesEnabled = enabled };
            if (omitProperty)
            {
                var json = JsonNode.Parse(policy.ToJson())!.AsObject();
                Assert.True(json.Remove("followerGatesEnabled"));
                policy = ConductorAutonomyPolicy.ParseJson(json.ToJsonString());
            }
            var candidate = Assert.IsType<ConductorParallelAcceptanceCandidate>(driver.TryBuildParallelAcceptanceCandidate(leader, policy, 0, out _));
            var started = coordinator.Evaluate(candidate, policy, driver.RunParallelLandingAcceptance);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            Assert.True(entered.Wait(TestHangGuard.Bound), "leader acceptance entered event did not happen");
            kernel.BeginGoalAcceptanceVerification(leader.Id, "leader solo gate running");
            if (moveLeader) leaderHead = new string('d', 40);
            var starts = 0;
            driver.FollowerGateStartOverride = (a, b, _) =>
            {
                Assert.Equal(leader.Id, a.GoalId);
                Assert.Equal(follower.Id, b.GoalId);
                Assert.Equal(a.LandingPaths, b.LandingPaths);
                starts++;
                return conflict ? new FollowerGateStartOutcome.Conflict(paths) : new FollowerGateStartOutcome.Started(null!);
            };
            driver.FollowerGateHoldOverride = id =>
            {
                if (id != follower.Id || newestOutcome is null) return null;
                var binding = new FollowerGateReceipt(leader.Id, leaderHead, new string('e', 40), main,
                    follower.Id, followerHead, new string('f', 40), "plan");
                return new(new("receipt", FollowerGateIdentity.Create(binding), binding, newestOutcome.Value,
                    DateTimeOffset.UnixEpoch, [], 0, [], null), FollowerLeaderGateStatus.Pending, FollowerLiveBaseState.LeaderPending);
            };
            var lines = new List<string>();
            var loop = new ConductorBatchLoop();
            loop.Run(kernel, driver, policy, NoStopPath(), maxIterations: 1,
                onTick: tick => lines.AddRange(tick.ProgressLines ?? []));
            var hold = (follower.CurrentHold?.Blocker ?? "")
                .Replace(leader.Id.Value, "leader").Replace(leader.Id.Value[..8], "leader")
                .Replace(follower.Id.Value, "follower").Replace(follower.Id.Value[..8], "follower");
            if (repeatTick)
                loop.Run(kernel, driver, policy, NoStopPath(), maxIterations: 1,
                    onTick: tick => lines.AddRange(tick.ProgressLines ?? []));
            return new(starts, leader.Status, follower.Status, hold, soloFollower, lines);
        }
        finally
        {
            release.Set();
            waitForAttempts();
            TryDeleteDirectory(root);
        }
    }
}
