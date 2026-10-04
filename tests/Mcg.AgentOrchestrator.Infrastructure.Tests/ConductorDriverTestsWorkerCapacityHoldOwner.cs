using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: each case owns its kernel and driver; no real worker is spawned.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsWorkerCapacityHoldOwner
{
    [Xunit.Theory]
    [Xunit.InlineData(1, 1, false, 1, "worker-cap",
        "At worker cap (1/1); will advance when a slot opens")]
    [Xunit.InlineData(12, 9, false, 9, "worker-admission-capacity",
        "At worker admission capacity (9/9); configured cap 12 is clamped; will advance when a slot opens")]
    [Xunit.InlineData(9, 8, true, 8, "reserved-gate-slot",
        "At worker cap (8/8) with a gate-ready goal reserving a stable slot; will advance when a slot opens")]
    public void WorkspaceReady_RungOneHold_OwnsCapacityAndPreservesDecision(
        int configuredCap, int running, bool gateReady, int effectiveCap, string evidence, string reason)
    {
        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative with { MaxConcurrentPaidWorkers = configuredCap };
        var dispatchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => running,
            dispatchAndStart: _ => { dispatchCalls++; return DispatchStartOutcome.Started(); },
            hasGateReadyGoal: () => gateReady,
            getWorkerAdmissionCapacity: () => 9);
        ConductorAdvanceResult? result = null;

        var output = AsyncLocalConsoleRouter.Capture(() => result = driver.AdvanceOnce(goal, policy));

        Assert.Equal(0, dispatchCalls);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result!.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        Assert.Equal(ConductorHoldOwner.WorkerCapacity, held.Owner);
        Assert.Equal(reason, held.Reason);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("dispatch-admission", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(1, decision.Rung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
        Assert.Collection(decision.Facts,
            fact => AssertFact(fact, "running", running.ToString()),
            fact => AssertFact(fact, "effectiveCap", effectiveCap.ToString()),
            fact => AssertFact(fact, "configuredCap", configuredCap.ToString()),
            fact => AssertFact(fact, "admissionCapacity", "9"),
            fact => AssertFact(fact, "reservedGateSlots", gateReady ? "1" : "0"),
            fact => AssertFact(fact, "policyMaxConcurrentPaidWorkers", configuredCap.ToString()),
            fact => AssertFact(fact, "sliceBatchAdmission", "not-evaluated"),
            fact => AssertFact(fact, "sliceBatchReason", ""));
        if (effectiveCap < configuredCap)
            Assert.Contains($"ADMISSION goal={goal.Id.Value[..8]} result=deferred reason={evidence} " +
                $"cap={effectiveCap} running={running} configuredCap={configuredCap} admissionCapacity=9", output,
                StringComparison.Ordinal);
        else
            Assert.DoesNotContain("ADMISSION ", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void WorkspaceReady_SliceBatchRefusal_RemainsOwnerless()
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
        Assert.IsType<ConductorAdvanceOutcome.Executed>(
            driver.AdvanceOnce(first, ConductorAutonomyPolicy.Conservative).Outcome);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(second, ConductorAutonomyPolicy.Conservative).Outcome);

        Assert.Equal(1, starts);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        Assert.Equal($"Slice-batch sibling {first.Id.Value[..8]} occupies colliding scope ownership:shared-infrastructure:core/application.", held.Reason);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("dispatch-admission", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(2, decision.Rung);
        Assert.Equal("slice-batch-admission", decision.DiscriminatingEvidence);
        Assert.Equal(held.Reason, decision.Reason);
        AssertFact(Assert.Single(decision.Facts, fact => fact.Name == "sliceBatchAdmission"), "sliceBatchAdmission", "refused");
        AssertFact(Assert.Single(decision.Facts, fact => fact.Name == "sliceBatchReason"), "sliceBatchReason", held.Reason);
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
}
