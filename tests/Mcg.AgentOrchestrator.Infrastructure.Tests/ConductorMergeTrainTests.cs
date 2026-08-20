using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorMergeTrainTests
{
    [Fact]
    public void Selector_takes_three_disjoint_ready_goals_in_oldest_first_order()
    {
        var first = Ready("11111111111111111111111111111111", "src/First.cs", "resource:first");
        var second = Ready("22222222222222222222222222222222", "src/Second.cs", "resource:second");
        var third = Ready("33333333333333333333333333333333", "src/Third.cs", "resource:third");

        var selection = Assert.IsType<ConductorMergeTrainSelection>(
            ConductorMergeTrainSelector.Select([first, second, third]));

        Assert.Equal([first.GoalId, second.GoalId, third.GoalId], selection.Members.Select(member => member.GoalId));
    }

    [Fact]
    public void Selector_skips_overlapping_member_without_reordering_later_member()
    {
        var first = Ready("11111111111111111111111111111111", "src/Shared", "resource:first");
        var overlapping = Ready("22222222222222222222222222222222", "src/Shared/File.cs", "resource:second");
        var third = Ready("33333333333333333333333333333333", "tests/Third.cs", "resource:third");

        var selection = Assert.IsType<ConductorMergeTrainSelection>(
            ConductorMergeTrainSelector.Select([first, overlapping, third]));

        Assert.Equal([first.GoalId, third.GoalId], selection.Members.Select(member => member.GoalId));
    }

    [Fact]
    public void Selector_does_not_replace_solo_acceptance()
    {
        var only = Ready("11111111111111111111111111111111", "src/Only.cs", "resource:only");

        Assert.Null(ConductorMergeTrainSelector.Select([only]));
    }

    private static ConductorSpeculativeAcceptanceCandidate Ready(string id, string path, string resource)
    {
        var goalId = new GoalId(id);
        return new ConductorSpeculativeAcceptanceCandidate(
            goalId,
            new GateReadyCandidateProjectionResult.Ready(new GateReadyCandidateProjection(
                goalId,
                GoalLifecycleState.Verified,
                GateReadyVerificationState.Satisfied,
                ChangeRiskTier.Behavior,
                ConductorTransitionDecision.Auto,
                [path],
                [resource],
                new GateReadyMergeEvidence(
                    new string('b', 40),
                    new string('a', 40),
                    GateReadyMergeStatus.Clean,
                    GateReadyMergeReason.NoConflictsDetected))));
    }
}
