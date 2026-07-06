using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Tests for goal dependency edges: DAG model, cycle/self rejection,
/// conductor hold/unblock, and failed-predecessor propagation.
/// </summary>
public sealed class GoalDependencyTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<AgentDefinition> DefaultAgents() => AgentCatalog.Default().Agents;

    // Put a simple-goal's single task into Completed+Verified state so goal.Status == Completed.
    private static void PassVerification(AgentOrchestratorKernel kernel, Goal goal)
    {
        var task = goal.Tasks.Single();
        var dispatch = new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        var verification = new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", "", DateTimeOffset.UtcNow);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
    }

    private static string NoStopPath() =>
        Path.Combine(Path.GetTempPath(), $"conduct-stop-{Guid.NewGuid():N}.txt");

    // ── Model: self-dependency rejected ──────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDependency_SelfDependency_Rejected")]
    public void GoalDependency_SelfDependency_Rejected()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Test");

        var ex = Assert.ThrowsAny<InvalidOperationException>(() => kernel.SetGoalDependency(goal.Id, goal.Id));
        AssertEx.Contains(ex.Message, msg => msg.Contains("cannot depend on itself", StringComparison.Ordinal));
    }

    // ── Model: direct cycle rejected ─────────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDependency_DirectCycle_Rejected")]
    public void GoalDependency_DirectCycle_Rejected()
    {
        var kernel = new AgentOrchestratorKernel();
        var a = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "A");
        var b = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "B");

        kernel.SetGoalDependency(b.Id, a.Id); // B → A

        var ex = Assert.ThrowsAny<InvalidOperationException>(() => kernel.SetGoalDependency(a.Id, b.Id));
        AssertEx.Contains(ex.Message, msg => msg.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    // ── Model: transitive cycle rejected ─────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDependency_TransitiveCycle_Rejected")]
    public void GoalDependency_TransitiveCycle_Rejected()
    {
        var kernel = new AgentOrchestratorKernel();
        var a = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "A");
        var b = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "B");
        var c = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "C");

        kernel.SetGoalDependency(b.Id, a.Id); // B → A
        kernel.SetGoalDependency(c.Id, b.Id); // C → B → A

        // A → C would create A → C → B → A
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => kernel.SetGoalDependency(a.Id, c.Id));
        AssertEx.Contains(ex.Message, msg => msg.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    // ── Model: snapshot round-trip ────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDependency_SnapshotRoundTrip")]
    public void GoalDependency_SnapshotRoundTrip()
    {
        var kernel = new AgentOrchestratorKernel();
        var a = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "A");
        var b = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "B");

        kernel.SetGoalDependency(b.Id, a.Id);

        var snapshot = kernel.ExportSnapshot();
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot);

        var restoredB = restored.Goals.Single(g => g.Id == b.Id);
        Assert.Equal(1, restoredB.DependsOn.Count);
        Assert.True(restoredB.DependsOn.Contains(a.Id));
    }

    // ── Conductor: two independent goals advance concurrently ─────────────────

    [Xunit.Fact(DisplayName = "GoalDependency_IndependentGoals_AdvanceConcurrently")]
    public void GoalDependency_IndependentGoals_AdvanceConcurrently()
    {
        var kernel = new AgentOrchestratorKernel();
        var a = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "A");
        var b = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "B");
        // No dependencies: A and B are fully independent.

        var workspacesCreated = new HashSet<string>(StringComparer.Ordinal);

        var driver = new ConductorDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: g => { workspacesCreated.Add(g.Id.Value); return "/tmp/ws"; },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            startRecordedDispatches: null,
            buildServerShutdown: null,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "OK", [], null),
            land: (g, _) => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"),
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null),
            writeEscalation: (_, _, _) => { },
            classifyChangeRisk: _ => null);

        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 2);

        // Both goals must advance (workspace created) within 2 ticks — no holds due to deps.
        Assert.True(workspacesCreated.Contains(a.Id.Value));
        Assert.True(workspacesCreated.Contains(b.Id.Value));
        Assert.Equal(0, summary.Held);
        Assert.Equal(0, summary.Escalated);
    }

    // ── Conductor: B depends on A; B held until A is Done ────────────────────
    //
    // A is pre-set to Verified (via PassVerification) and requires 3 more driver
    // advances to reach CleanedUp: Verified→land→Merged, Merged→record→Recorded,
    // Recorded→cleanup→CleanedUp. Then CleanedUp→Done on the fourth advance.
    // B is held each tick until A is in completedGoals. Ticks 1–3 always hold B
    // (A is Executed but not Done), making the 'held ≥ 3' assertion ordering-independent.

    [Xunit.Fact(DisplayName = "GoalDependency_Chain_BHeldUntilADone")]
    public void GoalDependency_Chain_BHeldUntilADone()
    {
        var kernel = new AgentOrchestratorKernel();
        var a = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "A");
        var b = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "B");

        PassVerification(kernel, a); // a.Status = Verified

        kernel.SetGoalDependency(b.Id, a.Id); // B depends on A

        // Per-goal post-completion fact flags for A
        var aMerged = false;
        var aRecorded = false;
        var aCleaned = false;
        var bWorkspaceCreated = false;

        var driver = new ConductorDriver(
            getFacts: g =>
            {
                if (g.Id == a.Id)
                {
                    // A is Completed; progress through Verified→Merged→Recorded→CleanedUp
                    if (aCleaned)  return new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true);
                    if (aRecorded) return new GoalLifecycleFacts(IsMerged: true, IsRecorded: true);
                    if (aMerged)   return new GoalLifecycleFacts(IsMerged: true);
                    return GoalLifecycleFacts.None; // Completed + no facts → Verified state
                }
                return GoalLifecycleFacts.None; // B: Active, no workspace → Created
            },
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: g =>
            {
                if (g.Id == b.Id) bWorkspaceCreated = true;
                return "/tmp/ws";
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            startRecordedDispatches: null,
            buildServerShutdown: null,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "OK", [], null),
            land: (g, _) =>
            {
                if (g.Id == a.Id) aMerged = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            afterSuccessfulLanding: null,
            record: g => { if (g.Id == a.Id) aRecorded = true; },
            cleanup: g =>
            {
                if (g.Id == a.Id) aCleaned = true;
                return new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null);
            },
            writeEscalation: (_, _, _) => { },
            classifyChangeRisk: _ => null);

        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 10);

        // Ticks 1–3: A advances (Executed), B held regardless of ordering.
        Assert.True(summary.Held >= 3);
        Assert.True(bWorkspaceCreated);
        Assert.True(kernel.IsKnownCompletedDependencyGoal(a.Id));
        Assert.Equal(0, summary.Escalated);
    }

    // ── Conductor: dependent of escalated goal stays excluded ─────────────────
    //
    // A escalates on its first advance (spawn failure). B depends on A.
    // The dep-check prevents B from ever calling AdvanceOnce and creating a workspace.

    [Xunit.Fact(DisplayName = "GoalDependency_EscalatedPredecessor_DependentNeverRuns")]
    public void GoalDependency_EscalatedPredecessor_DependentNeverRuns()
    {
        var kernel = new AgentOrchestratorKernel();
        var a = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "A");
        var b = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "B");

        kernel.SetGoalDependency(b.Id, a.Id); // B depends on A

        var bWorkspaceCreated = false;

        // A: WorkspaceExists=true → WorkspaceReady → SpawnFailed (retry also fails) → Escalated.
        // SpawnFailed triggers a retry + buildServerShutdown before escalating (unlike EmptyBatch).
        // B: WorkspaceExists=false → Created → would createWorkspace if dep check allowed it.
        var driver = new ConductorDriver(
            getFacts: g => g.Id == a.Id ? new GoalLifecycleFacts(WorkspaceExists: true) : GoalLifecycleFacts.None,
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: g =>
            {
                if (g.Id == b.Id) bWorkspaceCreated = true;
                return "/tmp/ws";
            },
            dispatchAndStart: g => g.Id == a.Id // A spawn-fails → escalated after retry
                ? DispatchStartOutcome.SpawnFailed("Spawn failed for A")
                : DispatchStartOutcome.Started(),
            startRecordedDispatches: null,
            buildServerShutdown: () => { }, // required when SpawnFailed triggers retry + shutdown
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "OK", [], null),
            land: (g, _) => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"),
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null),
            writeEscalation: (_, _, _) => { },
            classifyChangeRisk: _ => null);

        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 5);

        Assert.False(bWorkspaceCreated);
        // A escalates (SpawnFailed). B may be held or dep-escalated depending on tick ordering,
        // but it never reaches createWorkspace.
        Assert.True(summary.Escalated >= 1);
    }
}
