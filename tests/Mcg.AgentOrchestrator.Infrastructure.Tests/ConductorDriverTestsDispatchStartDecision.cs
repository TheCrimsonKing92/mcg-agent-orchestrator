using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsDispatchStartDecision
{
    [Xunit.Fact]
    public void ExitedUnappliedProcess_HoldsWithExactReasonAndDecisionAfterReconciliation()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        var completedAt = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
            12345, "test.exe", @"C:\tmp", @"C:\tmp\stdout", @"C:\tmp\stderr", @"C:\tmp\exit",
            StartedAt: completedAt.AddMinutes(-1), CompletedAt: completedAt, ExitCode: 0));
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = snapshot.Goals.Single();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with { Tasks = [goalSnapshot.Tasks.Single() with { Status = WorkTaskStatus.Assigned }] }]
        });
        goal = kernel.GetGoal(goal.Id);
        task = goal.Tasks.Single();
        var effects = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => throw new InvalidOperationException("Unreconciled completion must prevent dispatch."),
            recordTaskNote: (goalId, taskId, message) =>
            {
                effects.Add("note");
                kernel.RecordTaskNote(goalId, taskId, message);
            },
            reconcileExitedDispatch: (_, _) => { effects.Add("reconcile"); return false; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(new[] { "reconcile", "note" }, effects);
        var expectedReason = $"Dispatch start refused for task {task.Id.Value[..8]}: latest process record exited without an applied completion (exited-unapplied-process-record).";
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        Assert.Equal(expectedReason, held.Reason);
        var decision = AssertDecision(held.Decision, "Hold", 1, "exited-unapplied-process-record", expectedReason);
        AssertFact(decision, "goalId", goal.Id.Value);
        AssertFact(decision, "unreconciledTaskIdPrefix", task.Id.Value[..8]);
        AssertFact(decision, "startOutcomeCategory", "");
        var note = Assert.Single(goal.Timeline, item => item.Kind == ProgressKind.TaskNote);
        Assert.Equal(task.Id, note.TaskId);
        Assert.Equal(expectedReason, note.Message);
        AssertPayloadDecision(held, decision, "Held", null);
    }

    [Xunit.Fact]
    public void EmptyBatch_ProviderCooldown_HoldsWithExactReasonAndDecision()
    {
        var (_, goal) = SimpleGoal();
        const string readinessReason = "OpenAI retry pending";
        var effects = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { effects.Add("start"); return DispatchStartOutcome.EmptyBatch("No tasks in ready batch"); },
            evaluateReadiness: _ =>
            {
                effects.Add("readiness");
                return new DispatchReadinessDeferred(new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero), readinessReason);
            },
            writeEscalation: (_, _, _) => throw new InvalidOperationException("Cooldown must hold rather than escalate."));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(new[] { "start", "readiness" }, effects);
        const string expectedReason = "All assigned tasks deferred by provider cooldown; OpenAI retry pending. Will retry next tick.";
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        Assert.Equal(expectedReason, held.Reason);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        var decision = AssertDecision(held.Decision, "Hold", 6, "provider-cooldown", expectedReason);
        AssertFact(decision, "goalId", goal.Id.Value);
        AssertFact(decision, "startOutcomeCategory", "EmptyBatch");
        AssertFact(decision, "startOutcomeReason", "No tasks in ready batch");
        AssertFact(decision, "readinessVerdict", "deferred");
        AssertFact(decision, "readinessReason", readinessReason);
        AssertFact(decision, "cancelledPredecessorBlocker", "");
        AssertFact(decision, "assignedTasksBlockedReason", "");
        AssertPayloadDecision(held, decision, "Held", null);
    }

    [Xunit.Fact]
    public void EmptyBatch_CancelledPredecessor_EscalatesWithExactReasonAndCopiesDecision()
    {
        var developerId = TaskId.New().Value;
        var reviewerId = TaskId.New().Value;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("goal-cancelled-predecessor", "Escalate terminal task dependencies", GoalStatus.Active,
                [new TaskSnapshot(developerId, "Cancelled implementation.", AgentRole.Developer, WorkTaskStatus.Cancelled, null, null, null, [], null, null),
                 new TaskSnapshot(reviewerId, "Review waits forever.", AgentRole.Reviewer, WorkTaskStatus.Assigned, null, null, null, [], null, null)], [])], []));
        var goal = kernel.Goals.Single();
        var effects = new List<string>();
        string? escalationReason = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { effects.Add("start"); return DispatchStartOutcome.EmptyBatch("No tasks in ready batch"); },
            evaluateReadiness: _ => { effects.Add("readiness"); return new DispatchReadinessReady(); },
            writeEscalation: (_, _, reason) => { effects.Add("escalate"); escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(new[] { "start", "readiness", "escalate" }, effects);
        var blocker = $"task {reviewerId} (Reviewer) blocked: predecessor {developerId} is Cancelled, not Completed";
        var expectedReason = $"STRUCTURAL_TASK_BLOCKER: {blocker}. Operator recovery is required; retrying cannot complete a cancelled predecessor.";
        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, escalated.State);
        Assert.Equal(expectedReason, escalated.Reason);
        Assert.Equal(expectedReason, escalationReason);
        var decision = AssertDecision(escalated.Decision, "Escalate", 7, "cancelled-predecessor", expectedReason);
        AssertFact(decision, "goalId", goal.Id.Value);
        AssertFact(decision, "startOutcomeCategory", "EmptyBatch");
        AssertFact(decision, "readinessVerdict", "ready");
        AssertFact(decision, "cancelledPredecessorBlocker", blocker);
        AssertFact(decision, "assignedTasksBlockedReason", "");
        AssertPayloadDecision(escalated, decision, "Escalated", "Unspecified");
    }

    [Xunit.Fact]
    public void DeferredStart_KeepsReasonOwnerAndRecordsDecision()
    {
        var (_, goal) = SimpleGoal();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => DispatchStartOutcome.Deferred("Spec refinement pending") with { HoldOwner = ConductorHoldOwner.DurableOutbox },
            evaluateReadiness: _ => throw new InvalidOperationException("Deferred start must not evaluate readiness."));

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);

        Assert.Equal("Spec refinement pending", held.Reason);
        Assert.Equal(ConductorHoldOwner.DurableOutbox, held.Owner);
        var decision = AssertDecision(held.Decision, "Hold", 10, "start-deferred", "Spec refinement pending");
        AssertFact(decision, "startOutcomeCategory", "Deferred");
        AssertPayloadDecision(held, decision, "Held", null);
        Assert.Same(decision, VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(held).Decision);
    }

    [Xunit.Fact]
    public void Started_RecordsExecutedPayloadDecision()
    {
        var (_, goal) = SimpleGoal();
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal("Subscription dispatch started", executed.Description);
        var decision = AssertDecision(executed.Decision, "Proceed", 0, "proceed", "Dispatch start may proceed.");
        AssertFact(decision, "startOutcomeCategory", "Started");
        AssertPayloadDecision(executed, decision, "Executed", null);
    }

    private static PolicyDecisionRecord AssertDecision(PolicyDecisionRecord? value, string action, int rung, string evidence, string reason)
    {
        var decision = Assert.IsType<PolicyDecisionRecord>(value);
        Assert.Equal("dispatch-start", decision.Stage);
        Assert.Equal(action, decision.Action);
        Assert.Equal(rung, decision.Rung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(13, decision.Facts.Count);
        var replay = DispatchStartPolicy.Evaluate(DispatchStartFacts.FromRecordedFacts(decision.Facts));
        Assert.Equal(action, replay.Action.ToString());
        Assert.Equal(rung, replay.DiscriminatingRung);
        Assert.Equal(evidence, replay.DiscriminatingEvidence);
        Assert.Equal(reason, replay.Reason);
        return decision;
    }

    private static void AssertFact(PolicyDecisionRecord decision, string name, string value) =>
        Assert.Equal(value, Assert.Single(decision.Facts, fact => fact.Name == name).Value);

    private static void AssertPayloadDecision(ConductorAdvanceOutcome outcome, PolicyDecisionRecord expected, string outcomeKind, string? escalationKind)
    {
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(outcome);
        Assert.Equal(outcomeKind, payload.OutcomeKind);
        Assert.Equal("WorkspaceReady", payload.LifecycleState);
        Assert.Equal(escalationKind, payload.EscalationKind);
        var actual = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal(expected.Stage, actual.Stage);
        Assert.Equal(expected.Action, actual.Action);
        Assert.Equal(expected.Rung, actual.Rung);
        Assert.Equal(expected.DiscriminatingEvidence, actual.DiscriminatingEvidence);
        Assert.Equal(expected.Reason, actual.Reason);
        Assert.Equal(expected.Facts.Count, actual.Facts.Count);
        for (var index = 0; index < expected.Facts.Count; index++)
        {
            Assert.Equal(expected.Facts[index].Name, actual.Facts[index].Name);
            Assert.Equal(expected.Facts[index].Value, actual.Facts[index].Value);
        }
    }
}
