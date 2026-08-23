using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SliceBatchAdmissionTests
{
    [Xunit.Fact]
    public void DisjointSiblings_AdvanceInOneTick_BothDispatch()
    {
        var (kernel, first, second) = CreateBatch(
            "Change src/Mcg.AgentOrchestrator.App/Orchestration/AlphaSlice.cs.",
            "Change src/Mcg.AgentOrchestrator.App/Orchestration/BetaSlice.cs.");
        var starts = 0;
        var driver = CreateDriver(() => starts, _ => starts++);
        driver.SliceBatchAdmissionEvaluator = CreateEvaluator(kernel);

        driver.BeginTick();
        var firstResult = driver.AdvanceOnce(first, ConductorAutonomyPolicy.Conservative);
        var secondResult = driver.AdvanceOnce(second, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(firstResult.Outcome);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(secondResult.Outcome);
        Xunit.Assert.Equal(2, starts);
    }

    [Xunit.Fact]
    public void CollidingSiblings_AdvanceInOneTick_HoldsWithDurableReason()
    {
        var (kernel, first, second) = CreateBatch(
            "Change src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs.",
            "Change src/Mcg.AgentOrchestrator.Core/Application/BetaService.cs.");
        var starts = 0;
        var driver = CreateDriver(() => starts, _ => starts++);
        driver.SliceBatchAdmissionEvaluator = CreateEvaluator(kernel);

        driver.BeginTick();
        var firstResult = driver.AdvanceOnce(first, ConductorAutonomyPolicy.Conservative);
        var secondResult = driver.AdvanceOnce(second, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(firstResult.Outcome);
        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(secondResult.Outcome);
        Xunit.Assert.Contains(first.Id.Value[..8], held.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains("ownership:shared-infrastructure:core/application", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains(second.Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision && item.Message == held.Reason);
        Xunit.Assert.Equal(1, starts);
    }

    [Xunit.Fact]
    public void TerminalSibling_PreviouslyAdmitted_ReleasesRemainingSibling()
    {
        var (kernel, first, second) = CreateBatch(
            "Change src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs.",
            "Change src/Mcg.AgentOrchestrator.Core/Application/BetaService.cs.");
        var starts = 0;
        var driver = CreateDriver(() => starts, _ => starts++);
        driver.SliceBatchAdmissionEvaluator = CreateEvaluator(kernel);

        driver.BeginTick();
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(
            driver.AdvanceOnce(first, ConductorAutonomyPolicy.Conservative).Outcome);
        kernel.CancelGoal(first.Id, "Synthetic terminal sibling for admission coverage.");

        var secondResult = driver.AdvanceOnce(second, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(secondResult.Outcome);
        Xunit.Assert.Equal(2, starts);
    }

    [Xunit.Fact]
    public void PreviouslyDispatchedCollidingSiblings_UseStableTieBreakWithoutMutualHold()
    {
        var (kernel, first, second) = CreateBatch(
            "Change src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs.",
            "Change src/Mcg.AgentOrchestrator.Core/Application/BetaService.cs.");
        RecordPreviousProcess(kernel, first, 101);
        RecordPreviousProcess(kernel, second, 102);
        var ordered = new[] { first, second }
            .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var evaluator = CreateEvaluator(kernel);

        evaluator.BeginTick();
        var held = evaluator.Evaluate(ordered[1]);
        var winner = evaluator.Evaluate(ordered[0]);
        var winnerAgain = evaluator.Evaluate(ordered[0]);

        Xunit.Assert.False(held.IsAllowed);
        Xunit.Assert.Contains(ordered[0].Id.Value[..8], held.Reason, StringComparison.Ordinal);
        Xunit.Assert.True(winner.IsAllowed);
        Xunit.Assert.True(winnerAgain.IsAllowed);

        kernel.CancelGoal(ordered[0].Id, "Synthetic terminal winner for stable-order coverage.");

        var released = evaluator.Evaluate(ordered[1]);

        Xunit.Assert.True(released.IsAllowed);
    }

    [Xunit.Fact]
    public void PreviouslyDispatchedHigherIdSibling_HoldsNeverDispatchedLowerIdSibling()
    {
        var (kernel, first, second) = CreateBatch(
            "Change src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs.",
            "Change src/Mcg.AgentOrchestrator.Core/Application/BetaService.cs.");
        var ordered = new[] { first, second }
            .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .ToArray();
        RecordPreviousProcess(kernel, ordered[1], 101);
        var evaluator = CreateEvaluator(kernel);

        evaluator.BeginTick();
        var held = evaluator.Evaluate(ordered[0]);

        Xunit.Assert.False(held.IsAllowed);
        Xunit.Assert.Contains(ordered[1].Id.Value[..8], held.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void OrdinaryGoal_EvaluatorConfigured_SkipsSliceWork()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateActiveGoal(kernel, "Change src/Mcg.AgentOrchestrator.App/Orchestration/Ordinary.cs.");
        var siblingReads = 0;
        var observedReads = 0;
        var starts = 0;
        var evaluator = new SliceBatchAdmissionEvaluator(
            () =>
            {
                siblingReads++;
                return kernel.Goals;
            },
            _ =>
            {
                observedReads++;
                return [];
            },
            kernel.RecordGoalPolicyDecision);
        var driver = CreateDriver(() => starts, _ => starts++);
        driver.SliceBatchAdmissionEvaluator = evaluator;

        driver.BeginTick();
        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Xunit.Assert.Equal(0, siblingReads);
        Xunit.Assert.Equal(0, observedReads);
        Xunit.Assert.Equal(1, starts);
    }

    [Xunit.Fact]
    public void UndeclaredChange_ObservedBeforeSiblingAdmission_RecordsAndWidensScope()
    {
        var (kernel, first, second) = CreateBatch(
            "Change src/Mcg.AgentOrchestrator.App/Orchestration/DeclaredSlice.cs.",
            "Change src/Mcg.AgentOrchestrator.Core/Application/BetaService.cs.");
        var observed = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [first.Id] = ["src/Mcg.AgentOrchestrator.Core/Application/UndeclaredService.cs"]
        };
        var starts = 0;
        var driver = CreateDriver(() => starts, _ => starts++);
        driver.SliceBatchAdmissionEvaluator = new SliceBatchAdmissionEvaluator(
            () => kernel.Goals,
            goal => observed.GetValueOrDefault(goal.Id, []),
            kernel.RecordGoalPolicyDecision);

        driver.BeginTick();
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(
            driver.AdvanceOnce(first, ConductorAutonomyPolicy.Conservative).Outcome);
        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(second, ConductorAutonomyPolicy.Conservative).Outcome);

        Xunit.Assert.Contains("ownership:shared-infrastructure:core/application", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains(first.Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.Contains("slice-scope-divergence", StringComparison.Ordinal) &&
            item.Message.Contains("UndeclaredService.cs", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ObservedPathReadFailure_RecordsUnavailablePolicyDecision()
    {
        var (kernel, first, _) = CreateBatch(
            "Change src/Mcg.AgentOrchestrator.App/Orchestration/AlphaSlice.cs.",
            "Change src/Mcg.AgentOrchestrator.App/Orchestration/BetaSlice.cs.");
        var evaluator = new SliceBatchAdmissionEvaluator(
            () => kernel.Goals,
            _ => throw new InvalidOperationException("Synthetic changed-path read failure."),
            kernel.RecordGoalPolicyDecision);

        evaluator.BeginTick();
        var decision = evaluator.Evaluate(first);

        Xunit.Assert.True(decision.IsAllowed);
        Xunit.Assert.Contains(first.Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.Contains("slice-scope-observation-unavailable", StringComparison.Ordinal) &&
            item.Message.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    private static SliceBatchAdmissionEvaluator CreateEvaluator(AgentOrchestratorKernel kernel) =>
        new(() => kernel.Goals, _ => [], kernel.RecordGoalPolicyDecision);

    private static void RecordPreviousProcess(AgentOrchestratorKernel kernel, Goal goal, int processId)
    {
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UnixEpoch));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
            processId,
            "test.exe",
            "C:\\tmp",
            "C:\\tmp\\stdout",
            "C:\\tmp\\stderr",
            "C:\\tmp\\exit",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            0));
    }

    private static (AgentOrchestratorKernel Kernel, Goal First, Goal Second) CreateBatch(
        string firstObjective,
        string secondObjective)
    {
        var kernel = new AgentOrchestratorKernel();
        var parent = kernel.CreateGoal("Slice-batch parent.");
        var first = CreateActiveGoal(kernel, firstObjective, parent.Id);
        var second = CreateActiveGoal(kernel, secondObjective, parent.Id);
        return (kernel, first, second);
    }

    private static Goal CreateActiveGoal(
        AgentOrchestratorKernel kernel,
        string objective,
        GoalId? parentId = null)
    {
        var task = new TaskSpec(TaskId.New(), objective, AgentRole.Developer);
        var goal = kernel.CreateGoal(objective, [task], parentId);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return goal;
    }

    private static ConductorDriver CreateDriver(Func<int> runningCount, Action<Goal> dispatchStarted) =>
        new(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: runningCount,
            createWorkspace: _ => "unused",
            dispatchAndStart: goal =>
            {
                dispatchStarted(goal);
                return DispatchStartOutcome.Started();
            },
            startRecordedDispatches: null,
            buildServerShutdown: null,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordTaskNote: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                "goal/test",
                "Current.",
                [],
                null),
            land: (goal, _) => new LandingResult(
                goal.Id.Value,
                goal.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "Landed."),
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("Clean.", null, [], null),
            writeEscalation: (_, _, _) => { },
            classifyChangeRisk: _ => null);
}
