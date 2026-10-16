using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Simulation;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class GateSchedulingTrainParityTests
{
    [Theory]
    [InlineData("path")]
    [InlineData("resource")]
    [InlineData("two")]
    public void Greedy_composition_matches_conductor_on_compatible_projections(string scenario)
    {
        var projections = "ABCD".Select(c => Ready(c, $"src/{c}.cs", $"resource:{c}")).ToArray();
        if (scenario == "path") projections[1] = Ready('B', "src/A.cs", "resource:B");
        if (scenario == "resource") projections[1] = Ready('B', "src/B.cs", "resource:A");
        if (scenario == "two") projections = projections[..2];
        var conductor = ConductorMergeTrainSelector.Select(projections.Select(p =>
            new ConductorSpeculativeAcceptanceCandidate(p.GoalId, new GateReadyCandidateProjectionResult.Ready(p))).ToArray());
        var replay = GateSchedulingSimulator.ComposeTrain(projections.Select(p => new RecordedGateMember(
            p.GoalId.Value, p.CandidateRevision, p.LandingPaths, p.ResourceKeys)).ToArray(), 3);
        if (scenario == "two") { Assert.Null(conductor); Assert.Empty(replay); }
        else
        {
            Assert.NotNull(conductor);
            Assert.Equal(conductor.Members.Select(p => p.GoalId.Value), replay.Select(m => m.GoalId));
            Assert.Equal("ACD".Select(BoardSimulationFixture.Id), replay.Select(m => m.GoalId));
        }
    }

    private static GateReadyCandidateProjection Ready(char goal, string path, string resource) => new(
        new GoalId(BoardSimulationFixture.Id(goal)), GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied,
        ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, [path], [resource],
        new GateReadyMergeEvidence(new string('b', 40), new string('a', 40), GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));
}
