using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class GroupedAdmissionCandidateFilterTests
{
    [Fact]
    public void RemovesDependentAndKeepsDependencyAndUnrelatedInOrder()
    {
        var kernel = new AgentOrchestratorKernel();
        var s = kernel.CreateGoal("S");
        var t = kernel.CreateGoal("T");
        var u = kernel.CreateGoal("U");
        kernel.SetGoalDependency(s.Id, t.Id);
        Assert.Equal(new[] { t, u }, Filter(kernel, [s, t, u]));
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == t.Id.Value ? goal with { Status = GoalStatus.Completed } : goal).ToArray()
        });
        var goals = new[] { s, t, u }.Select(goal => kernel.GetGoal(goal.Id)).ToArray();
        Assert.Equal(goals, Filter(kernel, goals));
    }

    [Fact]
    public void FollowsTransitiveEdgesAndLiveGroupedMembers()
    {
        var kernel = new AgentOrchestratorKernel();
        var s = kernel.CreateGoal("S");
        var m = kernel.CreateGoal("M");
        var t = kernel.CreateGoal("T");
        kernel.SetGoalDependency(s.Id, m.Id);
        kernel.SetGoalDependency(m.Id, t.Id);
        Assert.Equal(new[] { t }, Filter(kernel, [s, t]));
        Assert.Empty(Filter(kernel, [s], new HashSet<string> { t.Id.Value }));
        kernel.ClearGoalDependencies(s.Id);
        Assert.Equal(new[] { t, s }, Filter(kernel, [t, s]));
    }

    private static Goal[] Filter(AgentOrchestratorKernel kernel, Goal[] goals, IReadOnlySet<string>? live = null)
    {
        var candidates = goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(goal.Id,
            new GateReadyCandidateProjectionResult.Ready(new(goal.Id, GoalLifecycleState.Verified,
                GateReadyVerificationState.Satisfied, ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto,
                [$"tests/Mcg.AgentOrchestrator.Core.Tests/{goal.Id.Value}.cs"], [$"ownership:{goal.Id.Value}"],
                new(new string('a', 40), new string('b', 40), GateReadyMergeStatus.Clean,
                    GateReadyMergeReason.NoConflictsDetected))))).ToArray();
        return GroupedAdmissionCandidateFilter.Filter(candidates, kernel.Goals, live ?? new HashSet<string>())
            .Select(candidate => kernel.GetGoal(candidate.GoalId)).ToArray();
    }
}
