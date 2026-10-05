using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsReloadedSweepPersistence : ConductorBatchLoopTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    public ConductorBatchLoopTestsReloadedSweepPersistence(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void ReloadedSweepCompletion_HeldAdvance_PersistsOnceAcrossTwoTicks()
    {
        var (kernel, goal, task) = DispatchedGoal();
        var stored = StoredSnapshot(kernel);
        var events = new RecordingEvents();
        kernel.SetEventWriter(events);
        var sweepTicks = 0;
        var completedTicks = 0;
        var tickOpen = false;
        var writtenTicks = new List<int>();
        var loop = new ConductorBatchLoop(utcNow: () => Now, sweep: current =>
        {
            current.RefreshTrackedGoals(stored);
            sweepTicks++;
            tickOpen = true;
            if (current.GetTask(goal.Id, task.Id).Status != WorkTaskStatus.Completed)
            {
                current.RecordTaskProcessRefreshed(goal.Id, task.Id,
                    current.GetTask(goal.Id, task.Id).LastProcess! with { CompletedAt = Now, ExitCode = 0 },
                    new TaskVerificationRecord("fixture-worker", "C:\\fixture", 0, "ok", "", Now));
            }
        });

        var result = loop.Run(kernel, HeldDriver(), ConductorAutonomyPolicy.Permissive,
            NoStopPath(), maxIterations: 2, watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false,
            onTick: _ =>
            {
                completedTicks++;
                tickOpen = false;
            },
            persistGoalTick: (current, ids) =>
            {
                // Stop/detach checkpoints cannot stand in for the required same-tick write.
                if (!tickOpen || !ids.Contains(goal.Id)) return;
                stored = StoredSnapshot(current);
                writtenTicks.Add(sweepTicks);
            });

        Assert.Equal(2, result.Ticks);
        Assert.Equal(2, result.Held);
        Assert.Equal(2, completedTicks);
        Assert.Equal(2, sweepTicks);
        Assert.Equal(1, Assert.Single(writtenTicks));
        Assert.Single(events.Timeline.Where(evt =>
            evt.GoalId == goal.Id && evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
        var durable = AgentOrchestratorKernel.FromSnapshot(stored).GetGoal(goal.Id);
        Assert.Single(durable.Timeline.Where(evt =>
            evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
        Assert.Equal(WorkTaskStatus.Completed, durable.Tasks.Single(candidate => candidate.Id == task.Id).Status);
    }

    [Xunit.Fact]
    public void InterruptedDispatchRequeue_HeldAdvance_PersistsInTheSameTick()
    {
        var (kernel, goal, task) = DispatchedGoal();
        kernel.RecordTaskProcessCancelled(goal.Id, task.Id, task.LastProcess! with
        {
            CompletedAt = Now,
            WasCancelled = true,
            WasCancelledByConductor = true
        });
        var stored = StoredSnapshot(kernel);
        var recoveryCalls = 0;
        var writtenIds = new List<GoalId>();
        var tickCompleted = false;
        var loop = new ConductorBatchLoop(utcNow: () => Now,
            recoverInterruptedDispatches: current =>
            {
                Assert.True(current.GetTask(goal.Id, task.Id).LastProcess!.WasCancelledByConductor);
                current.RequeueInterruptedDispatch(goal.Id, task.Id, "fixture auto-requeue",
                    RetryCause.ProviderInterruption, "fixture-dispatch");
                recoveryCalls++;
            });

        var result = loop.Run(kernel, HeldDriver(), ConductorAutonomyPolicy.Permissive,
            NoStopPath(), maxIterations: 1, watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false,
            onTick: _ => tickCompleted = true,
            persistGoalTick: (current, ids) =>
            {
                if (tickCompleted) return;
                writtenIds.AddRange(ids);
                stored = StoredSnapshot(current);
            });

        Assert.True(tickCompleted);
        Assert.Equal(1, result.Held);
        Assert.Equal(1, recoveryCalls);
        Assert.Equal(goal.Id, Assert.Single(writtenIds));
        var durable = AgentOrchestratorKernel.FromSnapshot(stored).GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Assigned, durable.Status);
        Assert.Equal(RetryCause.ProviderInterruption, durable.PendingRetryCause);
        Assert.Equal("fixture-dispatch", durable.InterruptedDispatchRecoveryId);
        Assert.Null(durable.LastDispatch);
        Assert.Null(durable.LastProcess);
    }

    [Xunit.Fact(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void UnchangedHeldGoal_ThreeTicks_WritesNoTickSnapshot()
    {
        var (kernel, goal) = SimpleGoal();
        var writtenIds = new List<GoalId>();
        var completedTicks = 0;
        var sweepTicks = 0;
        var tickOpen = false;
        var loop = new ConductorBatchLoop(utcNow: () => Now, sweep: _ =>
        {
            sweepTicks++;
            tickOpen = true;
        });

        var result = loop.Run(kernel, HeldDriver(), ConductorAutonomyPolicy.Permissive,
            NoStopPath(), maxIterations: 3, watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false,
            onTick: _ =>
            {
                completedTicks++;
                tickOpen = false;
            },
            persistGoalTick: (_, ids) =>
            {
                if (tickOpen) writtenIds.AddRange(ids);
            });

        Assert.Equal(3, result.Ticks);
        Assert.Equal(3, result.Held);
        Assert.Equal(3, completedTicks);
        Assert.Equal(3, sweepTicks);
        Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("Batch loop tick", StringComparison.Ordinal));
        Assert.Empty(writtenIds);
    }

    private static ConductorDriver HeldDriver() => MakeDriver(
        getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
        dispatchAndStart: _ => ConductorFindingEvidenceLoopFixture.HeldDispatch(), utcNow: () => Now);

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) DispatchedGoal()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock(Now));
        var task = new TaskSpec(TaskId.New(), "Dispatched developer", AgentRole.Developer);
        var goal = kernel.CreateGoal("Hold after dispatch reconciliation",
            [task, new TaskSpec(TaskId.New(), "Pending tester", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("fixture-worker", "fixture-worker", "C:\\fixture", Now.AddMinutes(-1)));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(4242, "fixture-worker", "C:\\fixture", "out.log", "err.log", "exit.log",
                Now.AddMinutes(-1), null, null, WasCancelled: false));
        return (kernel, goal, task);
    }

    private static OrchestratorSnapshot StoredSnapshot(AgentOrchestratorKernel kernel) =>
        JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(kernel.ExportSnapshot()))!;

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;

    private sealed class RecordingEvents : IGoalLifecycleEventWriter
    {
        public List<ProgressEvent> Timeline { get; } = [];
        public void AppendTimelineEvent(ProgressEvent progressEvent) => Timeline.Add(progressEvent);
        public void AppendGoalCreated(GoalId goalId, string objective) { }
        public void AppendClarificationNeeded(GoalId goalId, string clarificationId) { }
        public void AppendStaleClarificationDetected(GoalId goalId, IReadOnlyList<string> staleTopicKeys, string recoveryCommand) { }
        public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) { }
        public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) { }
        public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures) { }
        public void AppendAcceptanceCriterionWaived(GoalId goalId, string criterion, string actor, DateTimeOffset recordedAt,
            string reason, string capturedAcceptanceCriteriaHash) { }
        public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) { }
        public void AppendGoalLandedFromAncestry(GoalId goalId, string goalBranch, string branchTip, string mainSha) { }
        public void AppendGoalLandedFromMergeEvidence(GoalId goalId, string goalBranch, string integrateSha, string mainSha) { }
        public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) { }
        public void AppendCleanedUp(GoalId goalId) { }
        public void AppendProgressiveReviewGlanceReceipt(GoalId goalId, TaskId taskId, string trigger, string inputsHash,
            string verdict, string note, int inputTokens, int outputTokens, int totalTokens, TimeSpan wallTime,
            string? model, string? profile) { }
        public void AppendProgressiveReviewGlanceSummary(GoalId goalId, int totalGlances, int onTrack, int concern,
            int fundamentalMisdirection, int invalid, int totalTokens) { }
    }
}
