using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsShardPermitLaneClass : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task LaneClassFollowsExecutionOwnerForAllRunKinds()
    {
        var root = Path.Combine(Path.GetTempPath(), "gate-lane-class-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = new GoalAcceptanceVerifier(
                (string[] _, string _, TimeSpan _, CancellationToken _) =>
                    throw new InvalidOperationException("No command should run in the lane-class test"),
                TimeProvider.System);
            GateShardLaneClass Lane(IAcceptanceRunExecutionContext context) =>
                new GoalAcceptanceVerifier(source, context).ShardPermitLaneClassForTests;
            Assert.Equal(GateShardLaneClass.Gate, source.ShardPermitLaneClassForTests);

            await using var perGoal = AcceptanceExecutionOwners.CreateAttempt(root,
                options: new AcceptanceRunExecutionOptions(ResultsPrefix: Path.Combine(root, "per-goal")));
            Assert.Equal(GateShardLaneClass.Gate, Lane((IAcceptanceRunExecutionContext)perGoal));

            await using var cohort = AcceptanceExecutionOwners.CreateAttempt(root,
                options: new AcceptanceRunExecutionOptions(ResultsPrefix: Path.Combine(root, "cohort"),
                    OwnerProtectedCohortMembers: [new AcceptanceOwnerProtectedCohortMember(
                        new GoalId("abcdef12abcdef12abcdef12abcdef12"), "candidate")]));
            Assert.Equal(GateShardLaneClass.Gate, Lane((IAcceptanceRunExecutionContext)cohort));

            await using var canary = AcceptanceExecutionOwners.CreateAttempt(root,
                options: new AcceptanceRunExecutionOptions(ResultsPrefix: Path.Combine(root, "canary")));
            Assert.Equal(GateShardLaneClass.Gate, Lane((IAcceptanceRunExecutionContext)canary));

            await using var focused = AcceptanceExecutionOwners.CreateFocusedVerification(root,
                options: new AcceptanceRunExecutionOptions(ResultsPrefix: Path.Combine(root, "focused")));
            Assert.Equal(GateShardLaneClass.Evidence, Lane((IAcceptanceRunExecutionContext)focused));
            Assert.Equal(GateShardLaneClass.Evidence,
                Lane(new AcceptanceRunExecutionContextView(
                    (IAcceptanceRunExecutionContext)focused, Path.Combine(root, "baseline-arm"))));
        }
        finally
        {
            try { DeleteDirectoryWithRetry(root); } catch { }
        }
    }
}
