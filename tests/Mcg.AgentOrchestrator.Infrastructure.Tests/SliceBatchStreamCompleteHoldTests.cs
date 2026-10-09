using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static SliceBatchExecutionTests;

// Parallel-safe: each case owns its workspace and attempt files; all worker and landing I/O is seamed.
public sealed class SliceBatchStreamCompleteHoldTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void AdvanceOnce_CompletedChild_HoldsWithParentDecision(bool verifying)
    {
        var (kernel, workspace, agents, providers) = CreateContext(DisjointSliceBatchJson);
        try
        {
            var parent = CreateBatch(kernel, workspace, agents, providers);
            var child = kernel.Goals.First(goal => goal.SliceBatchParentId == parent.Id);
            var workspaceCreations = new List<GoalId>();
            var dispatches = new List<GoalId>();
            var driver = CreateDriver(kernel, workspaceCreations, dispatches);

            driver.BeginTick();
            Assert.Null(SliceBatchParentExecutionGuard.TryDescribeStreamCompleteHold(child));
            Assert.IsType<ConductorAdvanceOutcome.Executed>(
                driver.AdvanceOnce(child, ConductorAutonomyPolicy.Conservative).Outcome);
            Assert.Equal(new[] { child.Id }, dispatches);

            CompleteTasks(kernel, child);
            Assert.Equal(GoalStatus.Verified, child.Status);
            if (verifying)
                Assert.True(kernel.BeginGoalAcceptanceVerification(child.Id, "Test existing Verifying child."));
            var status = child.Status;

            driver.BeginTick();
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
                driver.AdvanceOnce(child, ConductorAutonomyPolicy.Conservative).Outcome);
            Assert.Equal(verifying ? GoalLifecycleState.Verifying : GoalLifecycleState.Verified, held.State);
            Assert.Contains(parent.Id.Value, held.Reason, StringComparison.Ordinal);
            var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
            Assert.Equal("lifecycle-entry", decision.Stage);
            Assert.Equal("Hold", decision.Action);
            Assert.Equal(1, decision.Rung);
            Assert.Equal("stream-complete-hold", decision.DiscriminatingEvidence);
            Assert.Equal(held.Reason, decision.Reason);
            Assert.Equal(held.Reason, LifecycleEntryFacts.FromRecordedFacts(decision.Facts).StreamCompleteHold);
            Assert.Equal(status, child.Status);
            Assert.Equal(new[] { child.Id }, dispatches);
            Assert.Empty(workspaceCreations);
        }
        finally
        {
            Directory.Delete(workspace.RootDirectory, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ParallelAcceptance_ExcludesCompleteChild_OrdinaryGoalStillLands(bool verifying)
    {
        var (kernel, workspace, agents, providers) = CreateContext(DisjointSliceBatchJson);
        try
        {
            var parent = CreateBatch(kernel, workspace, agents, providers);
            var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
            var child = children[0];
            var incompleteSibling = children[1];
            CompleteTasks(kernel, child);
            Assert.Equal(GoalStatus.Verified, child.Status);
            if (verifying)
                Assert.True(kernel.BeginGoalAcceptanceVerification(child.Id, "Test existing Verifying child."));

            var ordinary = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel, agents, "Implement src/Ordinary/Control.cs");
            CompleteTasks(kernel, ordinary);
            Assert.Null(ordinary.SliceBatchParentId);
            Assert.Equal(GoalStatus.Verified, ordinary.Status);
            Assert.False(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(incompleteSibling));
            Assert.Null(SliceBatchParentExecutionGuard.TryDescribeStreamCompleteHold(incompleteSibling));
            Assert.Null(SliceBatchParentExecutionGuard.TryDescribeStreamCompleteHold(ordinary));

            var slots = new List<(GoalId Goal, int? Slot)>();
            var reservations = new List<GoalId>();
            var landings = new List<GoalId>();
            var dispatches = new List<GoalId>();
            var attemptRoot = Path.Combine(workspace.RootDirectory, "attempts");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true,
                acquireStableSlotLease: (_, candidate) =>
                {
                    reservations.Add(candidate.Goal.Id);
                    return null;
                });
            var driver = CreateParallelDriver(kernel, dispatches, slots, landings, coordinator);
            Assert.True(driver.ParallelAcceptanceEnabled);

            new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative,
                Path.Combine(workspace.RootDirectory, "stop.txt"), maxIterations: 1);

            var slot = Assert.Single(slots);
            Assert.Equal(ordinary.Id, slot.Goal);
            Assert.NotNull(slot.Slot);
            Assert.Equal(new[] { ordinary.Id }, landings);
            Assert.Contains(ordinary.Id, reservations);
            Assert.DoesNotContain(child.Id, reservations);
            Assert.Empty(coordinator.GetUnreconciledAttempts([child.Id.Value]));
            Assert.False(Directory.Exists(Path.Combine(attemptRoot, child.Id.Value)),
                "A stream-complete child must not create even a reconciled acceptance attempt.");
            Assert.Contains(incompleteSibling.Id, dispatches);
            Assert.DoesNotContain(child.Id, dispatches);
            Assert.Equal(verifying ? GoalStatus.Verifying : GoalStatus.Verified, child.Status);
        }
        finally
        {
            Directory.Delete(workspace.RootDirectory, recursive: true);
        }
    }

    private static void CompleteTasks(AgentOrchestratorKernel kernel, Goal goal)
    {
        var completedAt = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        foreach (var task in goal.Tasks)
        {
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("test-worker", "test.exe", "unused", completedAt));
            kernel.RecordTaskVerification(goal.Id, task.Id,
                new TaskVerificationRecord("test.exe", "unused", 0, "ok", "", completedAt));
        }
    }

    private static ConductorDriver CreateParallelDriver(
        AgentOrchestratorKernel kernel,
        ICollection<GoalId> dispatches,
        ICollection<(GoalId Goal, int? Slot)> slots,
        ICollection<GoalId> landings,
        ConductorParallelAcceptanceAttemptCoordinator coordinator)
    {
        var driver = new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: () => dispatches.Count,
            createWorkspace: _ => throw new InvalidOperationException("Test workspace already exists."),
            dispatchAndStart: goal =>
            {
                dispatches.Add(goal.Id);
                return DispatchStartOutcome.Started();
            },
            startRecordedDispatches: null,
            buildServerShutdown: null,
            runAcceptanceVerification: _ => throw new InvalidOperationException("Expected acceptance with a slot."),
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordTaskNote: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "Current.", [], null),
            land: (goal, _) =>
            {
                landings.Add(goal.Id);
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8],
                    new LandingDecision.Promote(), "integration", true, "Landed.");
            },
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("Clean.", null, [], null),
            writeEscalation: (_, _, reason) => throw new InvalidOperationException(reason),
            classifyChangeRisk: _ => null,
            runAcceptanceVerificationWithSlot: (goal, slot) =>
            {
                slots.Add((goal.Id, slot));
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            parallelAcceptanceAttemptCoordinator: coordinator);
        driver.SliceBatchParentExecutionGuard = new SliceBatchParentExecutionGuard(() => kernel.Goals);
        driver.SliceBatchAdmissionEvaluator = new SliceBatchAdmissionEvaluator(
            () => kernel.Goals, _ => [], kernel.RecordGoalPolicyDecision);
        return driver;
    }
}
