using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: in-memory kernels and injected effects; no real process is started.
public sealed class ExitedDispatchReconciliationDriverEquivalenceTests
{
    [Xunit.Fact]
    public void AdvanceOnce_TwoExitedTasks_ReconcilesInOrderBeforeHeldNote()
    {
        var (kernel, goal) = ExitedGoal(taskCount: 2, applied: false);
        Assert.All(goal.Tasks, task =>
        {
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.True(DispatchProcessCompletionState.IsExitedWithoutAppliedCompletion(task, task.LastProcess!));
        });
        var effects = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                effects.Add("start");
                return DispatchStartOutcome.Started();
            },
            recordTaskNote: (goalId, taskId, message) =>
            {
                effects.Add("note");
                kernel.RecordTaskNote(goalId, taskId, message);
            },
            reconcileExitedDispatch: (_, id) =>
            {
                effects.Add($"reconcile({id.Value})");
                return false;
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(new[] { $"reconcile({goal.Tasks[0].Id.Value})",
            $"reconcile({goal.Tasks[1].Id.Value})", "note" }, effects);
        var task = goal.Tasks[0];
        var expectedReason = $"Dispatch start refused for task {task.Id.Value[..8]}: latest process record exited without an applied completion (exited-unapplied-process-record).";
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        Assert.Equal(expectedReason, held.Reason);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("dispatch-start", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(1, decision.Rung);
        Assert.Equal("exited-unapplied-process-record", decision.DiscriminatingEvidence);
        Assert.Equal(expectedReason, decision.Reason);
        var expectedFacts = new DispatchStartFacts(goal.Id.Value)
            { UnreconciledTaskIdPrefix = task.Id.Value[..8] }.ToRecordedFacts();
        Assert.Equal(13, decision.Facts.Count);
        Assert.Equal(expectedFacts, decision.Facts);
        var replay = DispatchStartPolicy.Evaluate(DispatchStartFacts.FromRecordedFacts(decision.Facts)).ToRecord();
        Assert.Equal(decision.Stage, replay.Stage);
        Assert.Equal(decision.Action, replay.Action);
        Assert.Equal(decision.Rung, replay.Rung);
        Assert.Equal(decision.DiscriminatingEvidence, replay.DiscriminatingEvidence);
        Assert.Equal(decision.Reason, replay.Reason);
        Assert.Equal(decision.Facts, replay.Facts);
        var note = Assert.Single(goal.Timeline, item => item.Kind == ProgressKind.TaskNote);
        Assert.Equal(task.Id, note.TaskId);
        Assert.Equal(expectedReason, note.Message);
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(held);
        Assert.Equal("Held", payload.OutcomeKind);
        Assert.Equal("WorkspaceReady", payload.LifecycleState);
        Assert.Null(payload.EscalationKind);
        Assert.Same(decision, payload.Decision);
    }

    [Xunit.Fact]
    public void AdvanceOnce_AppliedExitedTask_SkipsReconcileAndStartsDispatch()
    {
        var (_, goal) = ExitedGoal(taskCount: 1, applied: true);
        var task = goal.Tasks.Single();
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.NotNull(task.LastProcess!.CompletedAt);
        Assert.True(DispatchProcessCompletionState.HasAlreadyBeenApplied(task, task.LastProcess));
        Assert.False(DispatchProcessCompletionState.IsExitedWithoutAppliedCompletion(task, task.LastProcess));
        var effects = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: current =>
            {
                Assert.Same(goal, current);
                effects.Add("start");
                return DispatchStartOutcome.Started();
            },
            recordTaskNote: (_, _, _) => effects.Add("note"),
            reconcileExitedDispatch: (_, _) => { effects.Add("reconcile"); return true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(new[] { "start" }, effects);
        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal("Subscription dispatch started", executed.Description);
        var decision = Assert.IsType<PolicyDecisionRecord>(executed.Decision);
        Assert.Equal("Proceed", decision.Action);
        Assert.Equal("Started", Assert.Single(decision.Facts,
            fact => fact.Name == "startOutcomeCategory").Value);
        Assert.Empty(Assert.Single(decision.Facts,
            fact => fact.Name == "unreconciledTaskIdPrefix").Value);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) ExitedGoal(int taskCount, bool applied)
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
        var taskSnapshot = goalSnapshot.Tasks.Single() with { Status = WorkTaskStatus.Assigned };
        if (applied)
            taskSnapshot = taskSnapshot with
            {
                VerificationHistory = [new TaskVerificationSnapshot(
                    "test.exe", @"C:\tmp", 0, "", "", completedAt)]
            };
        var tasks = Enumerable.Range(0, taskCount).Select(index => index == 0
            ? taskSnapshot
            : taskSnapshot with { Id = TaskId.New().Value }).ToArray();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with { Tasks = tasks }]
        });
        return (kernel, kernel.GetGoal(goal.Id));
    }
}
