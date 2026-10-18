using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Kind = Mcg.AgentOrchestrator.App.Orchestration.ConductorBatchLoop.SoloAcceptanceAdmissionKind;

// Parallel-safe: in-memory goals and AsyncLocal probe scope only.
public sealed class ActivationAdmissionWaitingCensusTests
{
    [Xunit.Theory]
    [Xunit.InlineData("Admissible", "none", false, 1)]
    [Xunit.InlineData("Admissible", "held", false, 1)]
    [Xunit.InlineData("Admissible", "slot", false, 0)]
    [Xunit.InlineData("Capacity", "held", false, 0)]
    [Xunit.InlineData("Fairness", "held", false, 0)]
    [Xunit.InlineData("ResourceConflict", "held", false, 0)]
    [Xunit.InlineData("CandidateUnavailable", "held", false, 0)]
    [Xunit.InlineData("CandidateUnavailable", "none", false, 0)]
    [Xunit.InlineData("EngineCircuit", "held", false, 0)]
    [Xunit.InlineData("Completed", "held", false, 0)]
    [Xunit.InlineData("VerificationGate", "held", false, 0)]
    [Xunit.InlineData("SlotSettings", "held", false, 0)]
    [Xunit.InlineData(null, "held", false, 0)]
    [Xunit.InlineData(null, "slot", false, 0)]
    [Xunit.InlineData(null, "none", false, 1)]
    [Xunit.InlineData("Admissible", "none", true, 0)]
    public void Count_TickDecisionAndSlot_OnlyMissedAdmissionsWait(
        string? decision, string result, bool verifying, int expected)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = VerifiedGoal(kernel);
        if (verifying) Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "inherited gate"));
        var census = new ActivationAdmissionWaitingCensus();
        if (decision is not null) census.Record(goal.Id, Enum.Parse<Kind>(decision));
        var results = new Dictionary<string, ParallelLandingOutcome>();
        if (result != "none") results[goal.Id.Value] = Outcome(goal, result == "slot" ? 0 : null);

        Assert.Equal(expected, census.Count([goal], results));
    }

    [Xunit.Fact]
    public void Count_MixedDecisions_CountsOnlyAdmissibleGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var admissible = VerifiedGoal(kernel);
        var held = VerifiedGoal(kernel);
        var census = new ActivationAdmissionWaitingCensus();
        census.Record(admissible.Id, Kind.Admissible);
        census.Record(held.Id, Kind.Capacity);

        Assert.Equal(1, census.Count([admissible, held], new Dictionary<string, ParallelLandingOutcome>()));
    }

    [Xunit.Fact]
    public void Waiting_NextTick_ClearsDecisionsAndTakeConsumesLazyCount()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = VerifiedGoal(kernel);
        var results = new Dictionary<string, ParallelLandingOutcome>();
        using var scope = new ActivationAdmissionProbeScope(new MemoryStream());
        ActivationAdmissionProbeScope.Current.Value = scope;
        scope.Waiting(census => census.Count([goal], results));
        var admission = new ConductorBatchLoop.SoloAcceptanceAdmission(Kind.Capacity);
        Assert.Same(admission, ActivationAdmissionProbeScope.RecordSolo(goal.Id, admission));
        Assert.Equal(0, scope.Take(1).Waiting);

        scope.Waiting(census => census.Count([goal], results));
        Assert.Equal(1, scope.Take(2).Waiting);
        scope.Waiting(census => census.Count([goal], results));
        results[goal.Id.Value] = Outcome(goal, null);
        Assert.Equal(0, scope.Take(3).Waiting);
        results.Clear();
        Assert.Equal(0, scope.Take(4).Waiting);
    }

    [Xunit.Fact]
    public void RecordSolo_NoActiveScope_ReturnsSameDecision()
    {
        var previous = ActivationAdmissionProbeScope.Current.Value;
        try
        {
            ActivationAdmissionProbeScope.Current.Value = null;
            var admission = new ConductorBatchLoop.SoloAcceptanceAdmission(Kind.Admissible);
            Assert.Same(admission, ActivationAdmissionProbeScope.RecordSolo(VerifiedGoal(new()).Id, admission));
        }
        finally { ActivationAdmissionProbeScope.Current.Value = previous; }
    }

    [Xunit.Fact]
    public void Record_RepeatedGoal_LastDecisionWins()
    {
        var goal = VerifiedGoal(new());
        var census = new ActivationAdmissionWaitingCensus();
        census.Record(goal.Id, Kind.Capacity);
        census.Record(goal.Id, Kind.Admissible);
        Assert.Equal(1, census.Count([goal], new Dictionary<string, ParallelLandingOutcome>()));
    }

    private static Goal VerifiedGoal(AgentOrchestratorKernel kernel)
    {
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Probe census");
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UnixEpoch));
        kernel.RecordTaskVerification(goal.Id, task.Id, new("test.exe", "C:\\tmp", 0, "ok", "", DateTimeOffset.UnixEpoch));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        return goal;
    }

    private static ParallelLandingOutcome Outcome(Goal goal, int? slot) => new(
        new ConductorAdvanceResult(goal.Id.Value, goal.Id.Value[..8], "test",
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "fixture outcome")), slot);
}
