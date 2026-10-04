using Mcg.AgentOrchestrator.Core;

public sealed class DispatchStartPolicyTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return [new DispatchStartFacts("goal-123") { UnreconciledTaskIdPrefix = "abcdef12" },
            DispatchStartAction.Hold, 1, "exited-unapplied-process-record",
            "Dispatch start refused for task abcdef12: latest process record exited without an applied completion (exited-unapplied-process-record)."];
        yield return [new DispatchStartFacts("goal-123") { LeaseRecoveryStatus = "state-unavailable" },
            DispatchStartAction.Hold, 2, "evidence-lease-held",
            "Goal evidence mutation is held for goal-123; lease-recovery=state-unavailable."];
        yield return [new DispatchStartFacts("goal-123") { ConcurrentLeaseRefusal = "refused" },
            DispatchStartAction.Hold, 3, "evidence-lease-concurrent-mutation",
            "Goal evidence mutation is blocked by concurrent acceptance or replacement for goal-123."];
        yield return [new DispatchStartFacts("goal-123") { IntegrationCanDispatch = "refused", IntegrationMessage = "Main integration refused.\r\nConflict in service." },
            DispatchStartAction.Escalate, 4, "pre-dispatch-integration-refused", "Main integration refused.\r\nConflict in service."];
        yield return [new DispatchStartFacts("goal-123") { ReadOnlyIntegrationHoldReason = "Goal evidence mutation is held for goal-123; lease-recovery=state-unavailable." },
            DispatchStartAction.Hold, 5, "read-only-integration-hold",
            "Goal evidence mutation is held for goal-123; lease-recovery=state-unavailable."];
        yield return [new DispatchStartFacts("goal-123") { StartOutcomeCategory = "EmptyBatch", StartOutcomeReason = "No tasks in ready batch", ReadinessVerdict = "deferred", ReadinessReason = "Provider retry pending" },
            DispatchStartAction.Hold, 6, "provider-cooldown",
            "All assigned tasks deferred by provider cooldown; Provider retry pending. Will retry next tick."];
        yield return [new DispatchStartFacts("goal-123") { StartOutcomeCategory = "EmptyBatch", ReadinessVerdict = "ready", CancelledPredecessorBlocker = "task reviewer (Reviewer) blocked: predecessor developer is Cancelled, not Completed" },
            DispatchStartAction.Escalate, 7, "cancelled-predecessor",
            "STRUCTURAL_TASK_BLOCKER: task reviewer (Reviewer) blocked: predecessor developer is Cancelled, not Completed. Operator recovery is required; retrying cannot complete a cancelled predecessor."];
        yield return [new DispatchStartFacts("goal-123") { StartOutcomeCategory = "EmptyBatch", ReadinessVerdict = "blocked-with-candidates", ReadinessReason = "Missing profile", AssignedTasksBlockedReason = "Assigned tasks exist but no ready batch formed for goal goal-123; will retry next tick. Blockers: task developer (Developer) blocked: readiness gate returned false: Missing profile." },
            DispatchStartAction.Hold, 8, "assigned-tasks-blocked",
            "Assigned tasks exist but no ready batch formed for goal goal-123; will retry next tick. Blockers: task developer (Developer) blocked: readiness gate returned false: Missing profile."];
        yield return [new DispatchStartFacts("goal-123") { StartOutcomeCategory = "SpawnFailed", StartOutcomeReason = "Dispatched 1 task(s) but no processes started (spawn failed)" },
            DispatchStartAction.Escalate, 9, "dispatch-start-failed", "Dispatched 1 task(s) but no processes started (spawn failed)"];
        yield return [new DispatchStartFacts("goal-123") { StartOutcomeCategory = "EmptyBatch", StartOutcomeReason = "No assigned dispatch candidates", ReadinessVerdict = "blocked-without-candidates" },
            DispatchStartAction.Escalate, 9, "dispatch-start-failed", "No assigned dispatch candidates"];
        yield return [new DispatchStartFacts("goal-123"), DispatchStartAction.Proceed, 0, "proceed", "Dispatch start may proceed."];
        yield return [new DispatchStartFacts("goal-123") { StartOutcomeCategory = "Started" },
            DispatchStartAction.Proceed, 0, "proceed", "Dispatch start may proceed."];
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(Cases))]
    public void EachRung_PreservesOriginalReasonAndRecordsEvidence(
        DispatchStartFacts facts, DispatchStartAction action, int rung, string evidence, string reason)
    {
        var decision = DispatchStartPolicy.Evaluate(facts);

        Assert.Equal(action, decision.Action);
        Assert.Equal(rung, decision.DiscriminatingRung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
        var record = decision.ToRecord();
        Assert.Equal("dispatch-start", record.Stage);
        Assert.Equal(action.ToString(), record.Action);
        Assert.Equal(rung, record.Rung);
        Assert.Equal(evidence, record.DiscriminatingEvidence);
        Assert.Equal(reason, record.Reason);
    }

    [Xunit.Fact]
    public void EarlierRefusals_PrecedeLaterObservations()
    {
        var facts = new DispatchStartFacts("goal-123")
        {
            UnreconciledTaskIdPrefix = "abcdef12", LeaseRecoveryStatus = "held",
            ConcurrentLeaseRefusal = "refused", IntegrationCanDispatch = "refused",
            IntegrationMessage = "Integration refused", ReadOnlyIntegrationHoldReason = "Read-only held",
            StartOutcomeCategory = "EmptyBatch", ReadinessVerdict = "deferred", ReadinessReason = "Cooldown",
            CancelledPredecessorBlocker = "Cancelled predecessor", AssignedTasksBlockedReason = "Assigned blocked"
        };
        Assert.Equal(1, DispatchStartPolicy.Evaluate(facts).DiscriminatingRung);
        facts = facts with { UnreconciledTaskIdPrefix = "" };
        Assert.Equal(2, DispatchStartPolicy.Evaluate(facts).DiscriminatingRung);
        facts = facts with { LeaseRecoveryStatus = "" };
        Assert.Equal(3, DispatchStartPolicy.Evaluate(facts).DiscriminatingRung);
        facts = facts with { ConcurrentLeaseRefusal = "" };
        Assert.Equal(4, DispatchStartPolicy.Evaluate(facts).DiscriminatingRung);
        facts = facts with { IntegrationCanDispatch = "allowed" };
        Assert.Equal(5, DispatchStartPolicy.Evaluate(facts).DiscriminatingRung);
        facts = facts with { ReadOnlyIntegrationHoldReason = "" };
        Assert.Equal(6, DispatchStartPolicy.Evaluate(facts).DiscriminatingRung);
        facts = facts with { ReadinessVerdict = "blocked-without-candidates" };
        Assert.Equal(7, DispatchStartPolicy.Evaluate(facts).DiscriminatingRung);
        facts = facts with { CancelledPredecessorBlocker = "", ReadinessVerdict = "ready" };
        Assert.Equal(8, DispatchStartPolicy.Evaluate(facts).DiscriminatingRung);
    }

    [Xunit.Fact]
    public void RecordedFacts_HaveFixedNamesAndEmptyUnreadValues()
    {
        var facts = new DispatchStartFacts("goal-123").ToRecordedFacts();
        Assert.Equal(new[]
        {
            "goalId", "unreconciledTaskIdPrefix", "leaseRecoveryStatus", "concurrentLeaseRefusal",
            "integrationCanDispatch", "integrationMessage", "readOnlyIntegrationHoldReason",
            "startOutcomeCategory", "startOutcomeReason", "readinessVerdict", "readinessReason",
            "cancelledPredecessorBlocker", "assignedTasksBlockedReason"
        }, facts.Select(fact => fact.Name));
        Assert.Equal("goal-123", facts[0].Value);
        Assert.All(facts.Skip(1), fact => Assert.Equal("", fact.Value));
        var restored = DispatchStartFacts.FromRecordedFacts(facts.Reverse().ToArray()).ToRecordedFacts();
        for (var index = 0; index < facts.Count; index++)
        {
            Assert.Equal(facts[index].Name, restored[index].Name);
            Assert.Equal(facts[index].Value, restored[index].Value);
        }
    }

    [Xunit.Fact]
    public void RecordedFacts_MissingDuplicateOrExtraNames_FailLoudly()
    {
        var facts = new DispatchStartFacts("goal-123").ToRecordedFacts();
        Assert.Throws<InvalidOperationException>(() => DispatchStartFacts.FromRecordedFacts(facts.Skip(1).ToArray()));
        Assert.Throws<InvalidOperationException>(() => DispatchStartFacts.FromRecordedFacts(facts.Append(new("extra", "")).ToArray()));
        Assert.Throws<InvalidOperationException>(() => DispatchStartFacts.FromRecordedFacts(
            facts.Select(fact => fact.Name == "goalId" ? fact with { Name = "unknown" } : fact).ToArray()));
        Assert.Throws<InvalidOperationException>(() => DispatchStartFacts.FromRecordedFacts(
            facts.Select(fact => fact.Name == "goalId" ? fact with { Name = "readinessReason" } : fact).ToArray()));
    }

    [Xunit.Theory]
    [Xunit.InlineData("concurrentLeaseRefusal")]
    [Xunit.InlineData("integrationCanDispatch")]
    [Xunit.InlineData("startOutcomeCategory")]
    [Xunit.InlineData("readinessVerdict")]
    public void RecordedFacts_InvalidCategory_FailsLoudly(string name)
    {
        var facts = new DispatchStartFacts("goal-123").ToRecordedFacts();
        Assert.Throws<InvalidOperationException>(() => DispatchStartFacts.FromRecordedFacts(
            facts.Select(fact => fact.Name == name ? fact with { Value = "invalid" } : fact).ToArray()));
    }

    [Xunit.Fact]
    public void DeferredCategory_RemainsOwnedByExistingHoldPolicy() =>
        Assert.Throws<InvalidOperationException>(() => DispatchStartPolicy.Evaluate(
            new DispatchStartFacts("goal-123") { StartOutcomeCategory = "Deferred", StartOutcomeReason = "Spec refinement pending" }));
}
