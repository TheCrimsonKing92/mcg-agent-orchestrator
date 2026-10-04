using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsLifecycleEntryDecision
{
    [Xunit.Fact]
    public void ActiveSliceBatchParent_HoldsAtWorkspaceReady_WithOriginalOwnerAndDecision()
    {
        var (kernel, workspace, agents, providers) = SliceBatchExecutionTests.CreateContext(SliceBatchExecutionTests.DisjointSliceBatchJson);
        var parent = SliceBatchExecutionTests.CreateBatch(kernel, workspace, agents, providers);
        kernel.ActivateGoal(parent.Id, agents);
        Assert.Equal(GoalStatus.Active, parent.Status);
        var creations = new List<GoalId>();
        var dispatches = new List<GoalId>();
        var driver = SliceBatchExecutionTests.CreateDriver(kernel, creations, dispatches);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, GoalLifecycle.ResolveState(parent, driver.GetFacts(parent)));
        // The parent hold must precede even a missing policy transition entry.
        var policy = ConductorAutonomyPolicy.Conservative with { TransitionMap = new Dictionary<GoalLifecycleState, ConductorTransitionDecision>() };
        driver.BeginTick();

        var result = driver.AdvanceOnce(parent, policy);

        var reason = $"Slice-batch parent {parent.Id.Value[..8]} owns child goals and does not execute worker tasks.";
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        Assert.Equal(reason, held.Reason);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        Assert.Null(held.StableIdentity);
        Assert.Null(held.TypedReason);
        Assert.Empty(creations);
        Assert.Empty(dispatches);
        var decision = AssertLifecycleDecision(held.Decision, "Hold", 1, "slice-batch-parent-hold", reason,
            GoalLifecycleState.WorkspaceReady, "Conservative");
        AssertFact(decision, "slice-batch-parent-hold", reason);
        AssertFact(decision, "awaiting-clarification-reason", "");
        AssertFact(decision, "terminal-escalation-reason", "");
        AssertFact(decision, "transition-decision", "");
        AssertPayloadDecision(held, decision, "Held", "WorkspaceReady", null);
    }

    [Xunit.Theory]
    [Xunit.InlineData("injected clarification reason", "injected clarification reason")]
    [Xunit.InlineData(null, "Goal is in AwaitingClarification state; operator action required")]
    public void AwaitingClarification_EscalatesWithBuilderOrFallback_AndDecision(string? builderReason, string reason)
    {
        var (_, goal) = SimpleGoal();
        var effects = new List<string>();
        var writes = new List<(Goal Goal, GoalLifecycleState State, string Reason)>();
        var driver = CreateLifecycleDriver(
            _ => new GoalLifecycleFacts(WorkspaceExists: true, HasOpenClarification: true),
            clarify: _ => { effects.Add("clarify"); return builderReason; },
            resolve: (_, state) => { Assert.Equal(GoalLifecycleState.AwaitingClarification, state); effects.Add("resolve"); },
            escalate: (g, state, message) => { effects.Add("escalate"); writes.Add((g, state, message)); });
        var policy = ConductorAutonomyPolicy.Conservative with { TransitionMap = new Dictionary<GoalLifecycleState, ConductorTransitionDecision>() };

        var result = driver.AdvanceOnce(goal, policy);

        Assert.Equal(new[] { "resolve", "clarify", "escalate" }, effects);
        var write = Assert.Single(writes);
        Assert.Same(goal, write.Goal);
        Assert.Equal(GoalLifecycleState.AwaitingClarification, write.State);
        Assert.Equal(reason, write.Reason);
        var escalated = AssertEscalated(result, GoalLifecycleState.AwaitingClarification, reason);
        var decision = AssertLifecycleDecision(escalated.Decision, "Escalate", 3, "awaiting-clarification", reason,
            GoalLifecycleState.AwaitingClarification, "Conservative");
        AssertFact(decision, "slice-batch-parent-hold", "");
        AssertFact(decision, "awaiting-clarification-reason", builderReason ?? "");
        AssertFact(decision, "terminal-escalation-reason", "");
        AssertFact(decision, "transition-decision", "");
        AssertPayloadDecision(escalated, decision, "Escalated", "AwaitingClarification", "Unspecified");
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, GoalLifecycleState.AwaitingHumanInput, "Goal is in AwaitingHumanInput state; operator action required")]
    [Xunit.InlineData(true, GoalLifecycleState.Blocked, "Goal is in Blocked state; operator action required")]
    public void TerminalState_EscalatesWithOriginalReasonAndDecision(bool blocked, GoalLifecycleState state, string reason)
    {
        var (kernel, goal) = SimpleGoal();
        if (!blocked)
        {
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Worker started.");
            kernel.RequestHumanInput(goal.Id, task.Id, "Which repository should I target?");
        }
        var writes = new List<(GoalLifecycleState State, string Reason)>();
        var driver = CreateLifecycleDriver(_ => new GoalLifecycleFacts(IsBlocked: blocked),
            escalate: (_, observedState, message) => writes.Add((observedState, message)));
        var policy = ConductorAutonomyPolicy.Permissive with { TransitionMap = new Dictionary<GoalLifecycleState, ConductorTransitionDecision>() };

        var result = driver.AdvanceOnce(goal, policy);

        var write = Assert.Single(writes);
        Assert.Equal(state, write.State);
        Assert.Equal(reason, write.Reason);
        var escalated = AssertEscalated(result, state, reason);
        var decision = AssertLifecycleDecision(escalated.Decision, "Escalate", 4, "terminal-escalation", reason, state, "Permissive");
        AssertFact(decision, "slice-batch-parent-hold", "");
        AssertFact(decision, "awaiting-clarification-reason", "");
        AssertFact(decision, "terminal-escalation-reason", reason);
        AssertFact(decision, "transition-decision", "");
        AssertPayloadDecision(escalated, decision, "Escalated", state.ToString(), "Unspecified");
    }

    [Xunit.Fact]
    public void ManualPolicy_EscalatesWithOriginalReasonAndDecision()
    {
        var (_, goal) = SimpleGoal();
        var writes = new List<string>();
        var driver = CreateLifecycleDriver(_ => new GoalLifecycleFacts(WorkspaceExists: true),
            escalate: (_, state, reason) => { Assert.Equal(GoalLifecycleState.WorkspaceReady, state); writes.Add(reason); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Manual);

        const string reason = "Policy 'Manual' requires manual review at WorkspaceReady";
        Assert.Equal(reason, Assert.Single(writes));
        var escalated = AssertEscalated(result, GoalLifecycleState.WorkspaceReady, reason);
        var decision = AssertLifecycleDecision(escalated.Decision, "Escalate", 5, "policy-manual-review", reason,
            GoalLifecycleState.WorkspaceReady, "Manual");
        AssertFact(decision, "slice-batch-parent-hold", "");
        AssertFact(decision, "awaiting-clarification-reason", "");
        AssertFact(decision, "terminal-escalation-reason", "");
        AssertFact(decision, "transition-decision", "Escalate");
        AssertPayloadDecision(escalated, decision, "Escalated", "WorkspaceReady", "Unspecified");
    }

    [Xunit.Fact]
    public void WorkspaceCreateLeaseUnavailable_HoldsWithOriginalOwnerAndCreatedDecision()
    {
        var (_, goal) = SimpleGoal();
        var reason = $"GOAL_OPERATION_BLOCKED goal={goal.Id.Value} operation=conductor:workspace-create reason=concurrent-acceptance-or-replacement";
        var creates = 0;
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None,
            createWorkspace: _ => { creates++; throw new ConductorDriver.EvidenceMutationLeaseUnavailableException(reason); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, creates);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Created, held.State);
        Assert.Equal(reason, held.Reason);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        Assert.Null(held.StableIdentity);
        Assert.Null(held.TypedReason);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("created", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(1, decision.Rung);
        Assert.Equal("workspace-create-evidence-lease", decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(2, decision.Facts.Count);
        AssertFact(decision, "lease-unavailable-message", reason);
        AssertFact(decision, "created-path", "");
        var replay = CreatedStagePolicy.Evaluate(CreatedStageFacts.FromRecordedFacts(decision.Facts));
        Assert.Equal(CreatedStageAction.Hold, replay.Action);
        Assert.Equal(decision.Rung, replay.DiscriminatingRung);
        Assert.Equal(decision.DiscriminatingEvidence, replay.DiscriminatingEvidence);
        Assert.Equal(reason, replay.Reason);
        AssertPayloadDecision(held, decision, "Held", "Created", null);
    }

    [Xunit.Fact]
    public void WorkspaceCreated_ExecutesWithOriginalDescriptionAndNoDecision()
    {
        var (_, goal) = SimpleGoal();
        var creates = 0;
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None,
            createWorkspace: _ => { creates++; return @"C:\tmp\workspace"; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, creates);
        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Created, executed.FromState);
        Assert.Equal(@"Workspace created: C:\tmp\workspace", executed.Description);
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(executed);
        Assert.Equal("Executed", payload.OutcomeKind);
        Assert.Equal("Created", payload.LifecycleState);
        Assert.Null(payload.Decision);
        Assert.Null(payload.EscalationKind);
    }

    [Xunit.Fact]
    public void WorkspaceCreateOtherException_StillPropagates()
    {
        var (_, goal) = SimpleGoal();
        var failure = new InvalidOperationException("workspace creation failed");
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None, createWorkspace: _ => throw failure);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative)));
    }

    [Xunit.Fact]
    public void CleanedUp_ResolvesParkedWaitBeforeDone_AndCarriesNoDecision()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var resolved = new List<GoalLifecycleState>();
        var driver = CreateLifecycleDriver(_ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true),
            resolve: (_, state) => resolved.Add(state));
        var policy = ConductorAutonomyPolicy.Manual with { TransitionMap = new Dictionary<GoalLifecycleState, ConductorTransitionDecision>() };

        var done = Assert.IsType<ConductorAdvanceOutcome.Done>(driver.AdvanceOnce(goal, policy).Outcome);

        Assert.Equal(GoalLifecycleState.CleanedUp, done.State);
        Assert.Equal(GoalLifecycleState.CleanedUp, Assert.Single(resolved));
        Assert.Null(VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(done).Decision);
    }

    private static ConductorAdvanceOutcome.Escalated AssertEscalated(ConductorAdvanceResult result, GoalLifecycleState state, string reason)
    {
        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(state, escalated.State);
        Assert.Equal(reason, escalated.Reason);
        Assert.Null(escalated.Kind);
        return escalated;
    }

    private static PolicyDecisionRecord AssertLifecycleDecision(PolicyDecisionRecord? value, string action, int rung,
        string evidence, string reason, GoalLifecycleState state, string policyName)
    {
        var decision = Assert.IsType<PolicyDecisionRecord>(value);
        Assert.Equal("lifecycle-entry", decision.Stage);
        Assert.Equal(action, decision.Action);
        Assert.Equal(rung, decision.Rung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(7, decision.Facts.Count);
        AssertFact(decision, "resolved-state", state.ToString());
        AssertFact(decision, "state-is-failed", "false");
        AssertFact(decision, "policy-name", policyName);
        var replay = LifecycleEntryPolicy.Evaluate(LifecycleEntryFacts.FromRecordedFacts(decision.Facts));
        Assert.Equal(action, replay.Action.ToString());
        Assert.Equal(rung, replay.DiscriminatingRung);
        Assert.Equal(evidence, replay.DiscriminatingEvidence);
        Assert.Equal(reason, replay.Reason);
        return decision;
    }

    private static void AssertFact(PolicyDecisionRecord decision, string name, string value) =>
        Assert.Equal(value, Assert.Single(decision.Facts, fact => fact.Name == name).Value);

    private static void AssertPayloadDecision(ConductorAdvanceOutcome outcome, PolicyDecisionRecord expected,
        string outcomeKind, string state, string? escalationKind)
    {
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(outcome);
        Assert.Equal(outcomeKind, payload.OutcomeKind);
        Assert.Equal(state, payload.LifecycleState);
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

    private static ConductorDriver CreateLifecycleDriver(Func<Goal, GoalLifecycleFacts> facts,
        Func<Goal, string?>? clarify = null, Action<Goal, GoalLifecycleState>? resolve = null,
        Action<Goal, GoalLifecycleState, string>? escalate = null) => new(
        getFacts: facts,
        getRunningPaidWorkerCount: () => 0,
        createWorkspace: _ => throw new InvalidOperationException("Unexpected workspace creation"),
        dispatchAndStart: _ => throw new InvalidOperationException("Unexpected dispatch"),
        startRecordedDispatches: null,
        buildServerShutdown: null,
        runAcceptanceVerification: _ => throw new InvalidOperationException("Unexpected acceptance"),
        runAdvisorySemanticAcceptance: null,
        retryTask: null,
        recordTaskNote: null,
        recordCriterionRetryFeedback: null,
        clearCriterionRetryFeedback: null,
        rebaseOntoMain: _ => throw new InvalidOperationException("Unexpected rebase"),
        land: (_, _) => throw new InvalidOperationException("Unexpected landing"),
        afterSuccessfulLanding: null,
        record: _ => throw new InvalidOperationException("Unexpected record"),
        cleanup: _ => throw new InvalidOperationException("Unexpected cleanup"),
        writeEscalation: escalate ?? ((_, _, _) => throw new InvalidOperationException("Unexpected escalation")),
        classifyChangeRisk: _ => null,
        tryBuildAwaitingClarificationEscalationReason: clarify ?? (_ => throw new InvalidOperationException("Unexpected clarification builder")),
        resolveParkedWaitEscalations: resolve);
}
