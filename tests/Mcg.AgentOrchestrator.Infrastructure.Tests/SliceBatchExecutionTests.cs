using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

public sealed class SliceBatchExecutionTests
{
    [Xunit.Fact]
    public void Plan_TwoSlices_RecordsStreamReviewPipelineOnlyForChildren()
    {
        var (kernel, workspace, agents, providers) = CreateContext(TwoSliceBatchJson);
        var parent = CreateBatch(kernel, workspace, agents, providers);
        var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();

        Xunit.Assert.Equal(2, children.Length);
        Xunit.Assert.Equal([AgentRole.Reviewer],
            parent.Tasks.Select(task => task.RequiredRole));
        Xunit.Assert.Contains(parent.Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.StartsWith("Intake pipeline decision (override): whole-goal-review;", StringComparison.Ordinal));
        Xunit.Assert.All(children, child =>
        {
            Xunit.Assert.Equal([AgentRole.Developer, AgentRole.Reviewer],
                child.Tasks.Select(task => task.RequiredRole));
            Xunit.Assert.Contains(child.Timeline, item =>
                item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.StartsWith("Intake pipeline decision (override): developer-stream-reviewer;", StringComparison.Ordinal));
            Xunit.Assert.All(child.Tasks, task => Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status));
        });

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var requested = GoalObjectivePlanner.Build("Implement src/Other.cs.", GoalIntakePipeline.FiveRole);
        Xunit.Assert.All(restored.Goals.Where(goal => goal.SliceBatchParentId == parent.Id), child =>
        {
            var mismatch = Xunit.Assert.Throws<InvalidOperationException>(() =>
                GoalLifecycleCommands.EnsureRequestedPipelineMatchesPersistedGoal(requested, child));
            Xunit.Assert.Contains("persisted workflow='developer-stream-reviewer'", mismatch.Message, StringComparison.Ordinal);
        });
        var parentMismatch = Xunit.Assert.Throws<InvalidOperationException>(() =>
            GoalLifecycleCommands.EnsureRequestedPipelineMatchesPersistedGoal(requested, restored.GetGoal(parent.Id)));
        Xunit.Assert.Contains("orderedRoles=[Reviewer]", parentMismatch.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlanCreatesDispatchableChildrenAndNonExecutingParent()
    {
        var (kernel, workspace, agents, providers) = CreateContext(DisjointSliceBatchJson);
        var parent = CreateBatch(kernel, workspace, agents, providers);
        var children = kernel.Goals
            .Where(goal => goal.SliceBatchParentId == parent.Id)
            .ToArray();
        var workspaceCreations = new List<GoalId>();
        var dispatches = new List<GoalId>();
        var driver = CreateDriver(kernel, workspaceCreations, dispatches);

        driver.BeginTick();
        var parentResult = driver.AdvanceOnce(parent, ConductorAutonomyPolicy.Conservative);
        var childResults = children
            .Select(child => driver.AdvanceOnce(child, ConductorAutonomyPolicy.Conservative))
            .ToArray();

        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(parentResult.Outcome);
        Xunit.Assert.Contains("does not execute worker tasks", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.All(childResults, result =>
            Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome));
        Xunit.Assert.Equal(children.Select(child => child.Id), dispatches);
        Xunit.Assert.Empty(workspaceCreations);
        Xunit.Assert.DoesNotContain(parent.Id, dispatches);
    }

    [Xunit.Fact]
    public void PlanCreatesCollidingChildrenThatSequenceWithRecordedReason()
    {
        var (kernel, workspace, agents, providers) = CreateContext(CollidingSliceBatchJson);
        var parent = CreateBatch(kernel, workspace, agents, providers);
        var children = kernel.Goals
            .Where(goal => goal.SliceBatchParentId == parent.Id)
            .ToArray();
        var workspaceCreations = new List<GoalId>();
        var dispatches = new List<GoalId>();
        var driver = CreateDriver(kernel, workspaceCreations, dispatches);

        driver.BeginTick();
        var firstResult = driver.AdvanceOnce(children[0], ConductorAutonomyPolicy.Conservative);
        var secondResult = driver.AdvanceOnce(children[1], ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(firstResult.Outcome);
        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(secondResult.Outcome);
        Xunit.Assert.Contains("ownership:shared-infrastructure:core/application", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains(children[1].Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision && item.Message == held.Reason);
        Xunit.Assert.Equal([children[0].Id], dispatches);
        Xunit.Assert.Empty(workspaceCreations);
    }

    [Xunit.Fact]
    public void SliceBatchParentDoesNotAccumulateAStall()
    {
        var (kernel, workspace, agents, providers) = CreateContext(DisjointSliceBatchJson);
        var parent = CreateBatch(kernel, workspace, agents, providers);
        var workspaceCreations = new List<GoalId>();
        var dispatches = new List<GoalId>();
        var driver = CreateDriver(kernel, workspaceCreations, dispatches);
        var root = CreateTempDirectory();
        var logPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
        var stopPath = Path.Combine(root, "stop.txt");
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var writer = new ConductEventLogWriter(logPath, utcNow: () => now);

        new ConductorBatchLoop(conductEventLogWriter: writer, utcNow: () => now).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            stopPath,
            maxIterations: 1,
            onlyGoalId: parent.Id.Value,
            goalStallThreshold: TimeSpan.FromMinutes(10));

        now = now.AddMinutes(11);
        new ConductorBatchLoop(conductEventLogWriter: writer, utcNow: () => now).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            stopPath,
            maxIterations: 1,
            onlyGoalId: parent.Id.Value,
            goalStallThreshold: TimeSpan.FromMinutes(10));

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Xunit.Assert.DoesNotContain(records, record =>
            record.EventKind == "goal-stalled" && record.GoalId == parent.Id.Value[..8]);
        Xunit.Assert.Null(parent.CurrentHold?.StalledAt);
        Xunit.Assert.Empty(workspaceCreations);
        Xunit.Assert.Empty(dispatches);
    }

    [Xunit.Fact]
    public void OrdinaryGoalWithParentGuardDispatchesNormally()
    {
        var (kernel, _, agents, _) = CreateContext(DisjointSliceBatchJson);
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            agents,
            "Implement an ordinary goal");
        var workspaceCreations = new List<GoalId>();
        var dispatches = new List<GoalId>();
        var driver = CreateDriver(kernel, workspaceCreations, dispatches);

        driver.BeginTick();
        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Xunit.Assert.Equal([goal.Id], dispatches);
        Xunit.Assert.Empty(workspaceCreations);
    }

    internal static Goal CreateBatch(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers)
    {
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["plan", "Implement three slices", "--slice-batch", "--confirm-plan"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        return Xunit.Assert.Single(kernel.Goals.Where(goal => goal.SliceBatchParentId is null));
    }

    internal static ConductorDriver CreateDriver(
        AgentOrchestratorKernel kernel,
        ICollection<GoalId> workspaceCreations,
        ICollection<GoalId> dispatches)
    {
        var driver = new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: () => dispatches.Count,
            createWorkspace: goal =>
            {
                workspaceCreations.Add(goal.Id);
                return "unused";
            },
            dispatchAndStart: goal =>
            {
                dispatches.Add(goal.Id);
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
        driver.SliceBatchParentExecutionGuard = new SliceBatchParentExecutionGuard(() => kernel.Goals);
        driver.SliceBatchAdmissionEvaluator = new SliceBatchAdmissionEvaluator(
            () => kernel.Goals,
            _ => [],
            kernel.RecordGoalPolicyDecision);
        return driver;
    }

    internal static (
        AgentOrchestratorKernel Kernel,
        OrchestratorWorkspace Workspace,
        IReadOnlyList<AgentDefinition> Agents,
        IModelProviderRegistry Providers) CreateContext(string plannerOutput)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-slice-batch-execution", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var planner = new AgentDefinition(
            AgentId.New(),
            "Test-Planner",
            AgentRole.Planner,
            new ModelProfile("Fake", "fake-plan-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var developer = new AgentDefinition(
            AgentId.New(),
            "Test-Developer",
            AgentRole.Developer,
            new ModelProfile("Fake", "fake-dev-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var reviewer = new AgentDefinition(
            AgentId.New(), "Test-Reviewer", AgentRole.Reviewer,
            new ModelProfile("Fake", "fake-review-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var streamReviewer = reviewer with { Id = AgentId.New(), Name = "Test-Stream-Reviewer" };
        IReadOnlyList<AgentDefinition> agents = [planner, developer, reviewer, streamReviewer];
        IModelProviderRegistry providers = new InMemoryModelProviderRegistry([
            new FakeSmokeProvider(plannerOutput, providerName: "Fake")
        ]);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        return (kernel, workspace, agents, providers);
    }

    private const string TwoSliceBatchJson = """
        ```json
        [{"id":"g1","objective":"Implement feature A.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureA/A.cs","dependsOn":[]},{"id":"g2","objective":"Implement feature B.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureB/B.cs","dependsOn":[]}]
        ```
        """;

    internal const string DisjointSliceBatchJson = """
        ```json
        [{"id":"g1","objective":"Implement feature A.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureA/A.cs","dependsOn":[]},{"id":"g2","objective":"Implement feature B.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureB/B.cs","dependsOn":[]},{"id":"g3","objective":"Implement feature C.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureC/C.cs","dependsOn":[]}]
        ```
        """;

    private const string CollidingSliceBatchJson = """
        ```json
        [{"id":"g1","objective":"Implement feature A.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/Mcg.AgentOrchestrator.Core/Application/AlphaService.cs","dependsOn":[]},{"id":"g2","objective":"Implement feature B.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/Mcg.AgentOrchestrator.Core/Application/BetaService.cs","dependsOn":[]},{"id":"g3","objective":"Implement feature C.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/Mcg.AgentOrchestrator.App/Orchestration/GammaSlice.cs","dependsOn":[]}]
        ```
        """;
}
