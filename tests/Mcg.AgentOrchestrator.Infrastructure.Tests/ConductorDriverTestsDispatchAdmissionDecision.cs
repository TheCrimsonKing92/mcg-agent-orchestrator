using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsDispatchAdmissionDecision
{
    [Xunit.Fact]
    public void WorkspaceReady_ClampedCapacity_AttachesDecisionAndBuildsPayload()
    {
        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            MaxConcurrentPaidWorkers = ConductorBatchLoop.WorkerAdmissionCapacity + 3
        };
        var dispatchCalled = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorBatchLoop.WorkerAdmissionCapacity,
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); },
            hasGateReadyGoal: () => false);
        ConductorAdvanceResult? result = null;

        var output = AsyncLocalConsoleRouter.Capture(() => result = driver.AdvanceOnce(goal, policy));

        Assert.False(dispatchCalled);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result!.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("dispatch-admission", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(1, decision.Rung);
        Assert.Equal("worker-admission-capacity", decision.DiscriminatingEvidence);
        Assert.Equal("At worker admission capacity (9/9); configured cap 12 is clamped; will advance when a slot opens", decision.Reason);
        Assert.Equal(decision.Reason, held.Reason);
        Assert.Collection(decision.Facts,
            fact => AssertFact(fact, "running", "9"),
            fact => AssertFact(fact, "effectiveCap", "9"),
            fact => AssertFact(fact, "configuredCap", "12"),
            fact => AssertFact(fact, "admissionCapacity", "9"),
            fact => AssertFact(fact, "reservedGateSlots", "0"),
            fact => AssertFact(fact, "policyMaxConcurrentPaidWorkers", "12"),
            fact => AssertFact(fact, "sliceBatchAdmission", "not-evaluated"),
            fact => AssertFact(fact, "sliceBatchReason", ""));
        Assert.Contains($"ADMISSION goal={goal.Id.Value[..8]} result=deferred reason=worker-admission-capacity cap=9 running=9 configuredCap=12 admissionCapacity=9", output, StringComparison.Ordinal);

        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(held);
        Assert.Equal("Held", payload.OutcomeKind);
        Assert.Equal("WorkspaceReady", payload.LifecycleState);
        Assert.Null(payload.EscalationKind);
        AssertDecisionFields(decision, Assert.IsType<PolicyDecisionRecord>(payload.Decision));
    }

    [Xunit.Fact]
    public void AtWorkerCap_DoesNotEvaluateSliceAdmission()
    {
        var kernel = new AgentOrchestratorKernel();
        var parent = kernel.CreateGoal("Slice parent.");
        var goal = CreateSlice(kernel, parent.Id, "AlphaService");
        var policy = ConductorAutonomyPolicy.Conservative with { MaxConcurrentPaidWorkers = 1 };
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 1,
            dispatchAndStart: _ => throw new InvalidOperationException("Worker cap must prevent dispatch."),
            hasGateReadyGoal: () => false);
        Assert.Equal(parent.Id, goal.SliceBatchParentId);
        driver.SliceBatchAdmissionEvaluator = new SliceBatchAdmissionEvaluator(
            () => throw new InvalidOperationException("Worker cap must prevent slice sibling reads."),
            _ => throw new InvalidOperationException("Worker cap must prevent slice scope reads."),
            (_, _) => throw new InvalidOperationException("Worker cap must prevent slice store writes."));

        var result = driver.AdvanceOnce(goal, policy);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal(1, decision.Rung);
        Assert.Equal("worker-cap", decision.DiscriminatingEvidence);
        Assert.Equal("At worker cap (1/1); will advance when a slot opens", held.Reason);
    }

    [Xunit.Fact]
    public void SliceRefusal_AttachesRungTwoDecision_WithoutAddingStoreWrites()
    {
        var kernel = new AgentOrchestratorKernel();
        var parent = kernel.CreateGoal("Slice parent.");
        var first = CreateSlice(kernel, parent.Id, "AlphaService");
        var second = CreateSlice(kernel, parent.Id, "BetaService");
        var starts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => starts,
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); },
            hasGateReadyGoal: () => false);
        driver.SliceBatchAdmissionEvaluator = new SliceBatchAdmissionEvaluator(
            () => kernel.Goals, _ => [], kernel.RecordGoalPolicyDecision);
        driver.BeginTick();
        Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(first, ConductorAutonomyPolicy.Conservative).Outcome);

        var result = driver.AdvanceOnce(second, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, starts);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("dispatch-admission", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(2, decision.Rung);
        Assert.Equal("slice-batch-admission", decision.DiscriminatingEvidence);
        Assert.Equal($"Slice-batch sibling {first.Id.Value[..8]} occupies colliding scope ownership:shared-infrastructure:core/application.", held.Reason);
        Assert.Equal(held.Reason, decision.Reason);
        AssertFact(Assert.Single(decision.Facts, fact => fact.Name == "sliceBatchAdmission"), "sliceBatchAdmission", "refused");
        AssertFact(Assert.Single(decision.Facts, fact => fact.Name == "sliceBatchReason"), "sliceBatchReason", held.Reason);
        var storedEvent = Assert.Single(second.Timeline, item => item.Kind == ProgressKind.GoalPolicyDecision);
        Assert.Equal(held.Reason, storedEvent.Message);
        Assert.Null(storedEvent.TickOutcome);
        AssertDecisionFields(decision, Assert.IsType<PolicyDecisionRecord>(VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(held).Decision));
    }

    private static Goal CreateSlice(AgentOrchestratorKernel kernel, GoalId parentId, string service)
    {
        var objective = $"Change src/Mcg.AgentOrchestrator.Core/Application/{service}.cs.";
        var goal = kernel.CreateGoal(objective, [new TaskSpec(TaskId.New(), objective, AgentRole.Developer)], parentId);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return goal;
    }

    private static void AssertFact(PolicyDecisionFact fact, string name, string value)
    {
        Assert.Equal(name, fact.Name);
        Assert.Equal(value, fact.Value);
    }

    private static void AssertDecisionFields(PolicyDecisionRecord expected, PolicyDecisionRecord actual)
    {
        Assert.Equal(expected.Stage, actual.Stage);
        Assert.Equal(expected.Action, actual.Action);
        Assert.Equal(expected.Rung, actual.Rung);
        Assert.Equal(expected.DiscriminatingEvidence, actual.DiscriminatingEvidence);
        Assert.Equal(expected.Reason, actual.Reason);
        Assert.Equal(expected.Facts.Count, actual.Facts.Count);
        for (var index = 0; index < expected.Facts.Count; index++)
            AssertFact(actual.Facts[index], expected.Facts[index].Name, expected.Facts[index].Value);
    }
}
