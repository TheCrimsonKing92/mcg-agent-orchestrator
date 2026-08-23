using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalDagPlanTests
{
    // ── Parse: valid fenced JSON ──────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDagPlan_Parse_ValidFencedJson_ReturnsNodes")]
    public void GoalDagPlan_Parse_ValidFencedJson_ReturnsNodes()
    {
        var json = """
            ```json
            [{"id":"g1","objective":"Set up data model","dependsOn":[]},{"id":"g2","objective":"Implement service","dependsOn":["g1"]}]
            ```
            """;
        var plan = GoalDagDecompositionPlanner.Parse("Build the system", json);

        Assert.True(plan.IsValid);
        Assert.True(plan.Nodes.Count == 2);
        Assert.True(plan.Nodes[0].Id == "g1");
        Assert.True(plan.Nodes[1].Id == "g2");
        Assert.True(plan.Nodes[1].DependsOn.Contains("g1"));
    }

    // ── Parse: missing fenced block ───────────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDagPlan_Parse_NoFencedJson_ReturnsError")]
    public void GoalDagPlan_Parse_NoFencedJson_ReturnsError()
    {
        var plan = GoalDagDecompositionPlanner.Parse("Build the system", "Here is my plan: step 1, step 2.");

        Assert.False(plan.IsValid);
        Assert.True(plan.ValidationErrors.Any(e => e.Contains("fenced JSON", StringComparison.OrdinalIgnoreCase)));
    }

    // ── Parse: self-dependency rejected ──────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDagPlan_Parse_SelfDependency_ReturnsError")]
    public void GoalDagPlan_Parse_SelfDependency_ReturnsError()
    {
        var json = """
            ```json
            [{"id":"g1","objective":"Do something","dependsOn":["g1"]}]
            ```
            """;
        var plan = GoalDagDecompositionPlanner.Parse("Build the system", json);

        Assert.False(plan.IsValid);
        Assert.True(plan.ValidationErrors.Any(e => e.Contains("g1", StringComparison.Ordinal) &&
            e.Contains("itself", StringComparison.OrdinalIgnoreCase)));
    }

    // ── Parse: cycle rejected ─────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDagPlan_Parse_Cycle_ReturnsError")]
    public void GoalDagPlan_Parse_Cycle_ReturnsError()
    {
        var json = """
            ```json
            [{"id":"g1","objective":"A","dependsOn":["g2"]},{"id":"g2","objective":"B","dependsOn":["g1"]}]
            ```
            """;
        var plan = GoalDagDecompositionPlanner.Parse("Build the system", json);

        Assert.False(plan.IsValid);
        Assert.True(plan.ValidationErrors.Any(e => e.Contains("cycle", StringComparison.OrdinalIgnoreCase)));
    }

    // ── Parse: unresolved reference rejected ─────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalDagPlan_Parse_UnresolvedReference_ReturnsError")]
    public void GoalDagPlan_Parse_UnresolvedReference_ReturnsError()
    {
        var json = """
            ```json
            [{"id":"g1","objective":"Do something","dependsOn":["g99"]}]
            ```
            """;
        var plan = GoalDagDecompositionPlanner.Parse("Build the system", json);

        Assert.False(plan.IsValid);
        Assert.True(plan.ValidationErrors.Any(e => e.Contains("g99", StringComparison.Ordinal)));
    }

    // ── CLI: plan preview does not create goals ───────────────────────────────

    [Xunit.Fact(DisplayName = "Cli_Plan_Preview_ShowsNodesWithoutCreatingGoals")]
    public void Cli_Plan_Preview_ShowsNodesWithoutCreatingGoals()
    {
        var (kernel, workspace, agents, providers) = BuildTestContext(TwoNodeJson);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["plan", "Implement data model then service layer"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Assert.True(kernel.Goals.Count == 0);
        Assert.True(output.Contains("g1", StringComparison.Ordinal));
        Assert.True(output.Contains("g2", StringComparison.Ordinal));
    }

    // ── CLI: confirm-plan creates two goals with dependency edge ─────────────

    [Xunit.Fact(DisplayName = "Cli_Plan_ConfirmPlan_TwoNodeChain_CreatesGoalsWithDependency")]
    public void Cli_Plan_ConfirmPlan_TwoNodeChain_CreatesGoalsWithDependency()
    {
        var (kernel, workspace, agents, providers) = BuildTestContext(TwoNodeJson);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["plan", "Implement data model then service layer", "--confirm-plan"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Assert.True(kernel.Goals.Count == 2);

        var g1 = kernel.Goals.Single(g => g.Objective.Contains("data model", StringComparison.OrdinalIgnoreCase));
        var g2 = kernel.Goals.Single(g => g.Objective.Contains("service", StringComparison.OrdinalIgnoreCase));
        Assert.True(g2.DependsOn.Contains(g1.Id));
    }

    // ── CLI: confirm-plan with cycle throws before creating any goals ─────────

    [Xunit.Fact(DisplayName = "Cli_Plan_ConfirmPlan_CyclePlan_ThrowsInvalidOperation")]
    public void Cli_Plan_ConfirmPlan_CyclePlan_ThrowsInvalidOperation()
    {
        var cycleJson = """
            ```json
            [{"id":"g1","objective":"A","dependsOn":["g2"]},{"id":"g2","objective":"B","dependsOn":["g1"]}]
            ```
            """;
        var (kernel, workspace, agents, providers) = BuildTestContext(cycleJson);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Assert.ThrowsAny<InvalidOperationException>(() =>
            CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["plan", "Implement data model then service layer", "--confirm-plan"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));

        Assert.True(kernel.Goals.Count == 0);
        Assert.True(ex.Message.Contains("validation error", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_Confirm_CreatesDormantParentAndActiveChildren")]
    public void CliPlanSliceBatchConfirmCreatesDormantParentAndActiveChildren()
    {
        var (kernel, workspace, agents, providers) = BuildSliceBatchTestContext(ThreeSliceBatchJson);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["plan", "Implement three disjoint feature slices", "--slice-batch", "--confirm-plan"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Assert.Equal(4, kernel.Goals.Count);
        var parent = Assert.Single(kernel.Goals.Where(goal => goal.SliceBatchParentId is null));
        Assert.Equal(parent.Id, currentGoal!.Id);
        Assert.Equal([AgentRole.Developer, AgentRole.Reviewer], parent.Tasks.Select(task => task.RequiredRole));

        var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
        Assert.Equal(3, children.Length);
        Assert.Equal(GoalStatus.Draft, parent.Status);
        Assert.All(parent.Tasks, task => Assert.Null(task.AssignedAgentId));
        Assert.All(kernel.Goals, goal =>
        {
            Assert.Empty(goal.DependsOn);
            Assert.True(GoalRefinementWorkCoordinator.HasPendingWork(goal));
        });
        Assert.All(children, child =>
        {
            Assert.Equal(GoalStatus.Active, child.Status);
            var task = Assert.Single(child.Tasks);
            Assert.Equal(AgentRole.Developer, task.RequiredRole);
            Assert.NotNull(task.AssignedAgentId);
        });

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        Assert.All(
            restored.Goals.Where(goal => goal.Id != parent.Id),
            child => Assert.Equal(parent.Id, child.SliceBatchParentId));

        var intents = children.Select(child =>
        {
            var task = Assert.Single(child.Tasks);
            var scope = GoalFileScopeInference.ForScheduling(child, task);
            Assert.Equal(RepositoryScopeConfidence.Precise, scope.Confidence);
            return new ParallelExecutionIntent(
                task.Id.Value,
                child.Id.Value,
                scope.Includes,
                ScopeConfidence: scope.Confidence);
        }).ToArray();
        var executionPlan = ParallelExecutionPlanner.Build(intents);
        Assert.Equal(3, Assert.Single(executionPlan.Batches).IntentIds.Count);
        Assert.All(executionPlan.Decisions, decision =>
        {
            Assert.Equal(1, decision.BatchNumber);
            Assert.Equal(ParallelExecutionDisposition.Concurrent, decision.Disposition);
        });

        Assert.Contains($"parent {parent.Id.Value}", output, StringComparison.Ordinal);
        Assert.All(children, child => Assert.Contains(child.Id.Value, output, StringComparison.Ordinal));
        Assert.Contains("child execution is enabled", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("each child currently runs its own acceptance gate", output, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void CliPlanSliceBatchWithoutDeveloperRejectsAtomically()
    {
        var (kernel, workspace, configuredAgents, providers) = BuildSliceBatchTestContext(ThreeSliceBatchJson);
        IReadOnlyList<AgentDefinition> agents = configuredAgents
            .Where(agent => agent.Role != AgentRole.Developer)
            .ToArray();
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["plan", "Implement three disjoint feature slices", "--slice-batch", "--confirm-plan"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));

        Assert.Empty(kernel.Goals);
        Assert.Contains("available Developer agent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No goal was created", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_Preview_IsDormantAndDoesNotMutate")]
    public void CliPlanSliceBatchPreviewIsDormantAndDoesNotMutate()
    {
        var (kernel, workspace, configuredAgents, providers) = BuildSliceBatchTestContext(ThreeSliceBatchJson);
        IReadOnlyList<AgentDefinition> agents = configuredAgents
            .Where(agent => agent.Role != AgentRole.Developer)
            .ToArray();
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["plan", "Implement three disjoint feature slices", "--slice-batch"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Assert.Empty(kernel.Goals);
        Assert.Contains("Dormant slice-batch intake preview", output, StringComparison.Ordinal);
        Assert.Contains("--slice-batch --confirm-plan", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_InvalidNodeCount_RejectedAtomically")]
    public void CliPlanSliceBatchInvalidNodeCountRejectedAtomically()
    {
        var (exception, kernel) = ConfirmInvalidSliceBatch(
            SliceBatchJson(SliceObjective("only", "src/FeatureA/A.cs")));

        Assert.Empty(kernel.Goals);
        Assert.Contains("found 1", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_DependencyEdge_RejectedAtomically")]
    public void CliPlanSliceBatchDependencyEdgeRejectedAtomically()
    {
        var json = SliceBatchJson(
            SliceObjective("g1", "src/FeatureA/A.cs"),
            SliceObjective("g2", "src/FeatureB/B.cs", "g1"));
        var (exception, kernel) = ConfirmInvalidSliceBatch(json);

        Assert.Empty(kernel.Goals);
        Assert.Contains("g2 -> g1", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_MissingScope_RejectedAtomically")]
    public void CliPlanSliceBatchMissingScopeRejectedAtomically()
    {
        var json = SliceBatchJson(
            "{\"id\":\"missing\",\"objective\":\"Implement the missing slice\",\"dependsOn\":[]}",
            SliceObjective("precise", "src/FeatureB/B.cs"));
        var (exception, kernel) = ConfirmInvalidSliceBatch(json);

        Assert.Empty(kernel.Goals);
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Target files/scopes:", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_NonPreciseScope_RejectedAtomically")]
    public void CliPlanSliceBatchNonPreciseScopeRejectedAtomically()
    {
        var nonPrecise = "{\"id\":\"unknown\",\"objective\":\"Implement uncertain scope.\\n\\nTarget files/scopes:\\nScope confidence: unknown\\nIncludes:\\n- src/FeatureA/A.cs\",\"dependsOn\":[]}";
        var json = SliceBatchJson(nonPrecise, SliceObjective("precise", "src/FeatureB/B.cs"));
        var (exception, kernel) = ConfirmInvalidSliceBatch(json);

        Assert.Empty(kernel.Goals);
        Assert.Contains("unknown", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Scope confidence: precise", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_Plan_SliceBatch_OverlappingScopes_RejectedAtomically")]
    public void CliPlanSliceBatchOverlappingScopesRejectedAtomically()
    {
        var json = SliceBatchJson(
            SliceObjective("directory", "src/FeatureA"),
            SliceObjective("file", "src/FeatureA/A.cs"));
        var (exception, kernel) = ConfirmInvalidSliceBatch(json);

        Assert.Empty(kernel.Goals);
        Assert.Contains("directory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("file", exception.Message, StringComparison.Ordinal);
        Assert.Contains("src/FeatureA/A.cs", exception.Message, StringComparison.Ordinal);
    }

    // ── Best-of-N selection ──────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "BestOfN_AllValid_PicksFewestNodes")]
    public void BestOfN_AllValid_PicksFewestNodes()
    {
        var threeNodePlan = GoalDagDecompositionPlanner.Parse("dir", """
            ```json
            [{"id":"g1","objective":"A","dependsOn":[]},{"id":"g2","objective":"B","dependsOn":["g1"]},{"id":"g3","objective":"C","dependsOn":["g2"]}]
            ```
            """);
        var twoNodePlan = GoalDagDecompositionPlanner.Parse("dir", """
            ```json
            [{"id":"g1","objective":"A","dependsOn":[]},{"id":"g2","objective":"B","dependsOn":["g1"]}]
            ```
            """);

        var selected = GoalDagDecompositionPlanner.SelectBestOfN([threeNodePlan, twoNodePlan]);

        Assert.True(selected.IsValid);
        Assert.True(selected.Nodes.Count == 2);
    }

    [Xunit.Fact(DisplayName = "BestOfN_FirstInvalidSecondValid_PicksValid")]
    public void BestOfN_FirstInvalidSecondValid_PicksValid()
    {
        var invalidPlan = GoalDagDecompositionPlanner.Parse("dir", "no fenced json here");
        var validPlan = GoalDagDecompositionPlanner.Parse("dir", """
            ```json
            [{"id":"g1","objective":"A","dependsOn":[]}]
            ```
            """);

        var selected = GoalDagDecompositionPlanner.SelectBestOfN([invalidPlan, validPlan]);

        Assert.True(selected.IsValid);
        Assert.True(selected.Nodes.Count == 1);
    }

    [Xunit.Fact(DisplayName = "BestOfN_NoneValid_ReturnsFirstErrors")]
    public void BestOfN_NoneValid_ReturnsFirstErrors()
    {
        var invalid1 = GoalDagDecompositionPlanner.Parse("dir", "no json here");
        var invalid2 = GoalDagDecompositionPlanner.Parse("dir", "also no json");

        var selected = GoalDagDecompositionPlanner.SelectBestOfN([invalid1, invalid2]);

        Assert.False(selected.IsValid);
        Assert.True(selected.ValidationErrors.Count > 0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string TwoNodeJson = """
        ```json
        [{"id":"g1","objective":"Implement the data model","dependsOn":[]},{"id":"g2","objective":"Implement the service layer","dependsOn":["g1"]}]
        ```
        """;

    private const string ThreeSliceBatchJson = """
        ```json
        [{"id":"g1","objective":"Implement feature A.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureA/A.cs","dependsOn":[]},{"id":"g2","objective":"Implement feature B.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureB/B.cs","dependsOn":[]},{"id":"g3","objective":"Implement feature C.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureC/C.cs","dependsOn":[]}]
        ```
        """;

    private static (InvalidOperationException exception, AgentOrchestratorKernel kernel) ConfirmInvalidSliceBatch(string plannerOutput)
    {
        var (kernel, workspace, agents, providers) = BuildSliceBatchTestContext(plannerOutput);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["plan", "Implement file-disjoint slices", "--slice-batch", "--confirm-plan"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));
        return (exception, kernel);
    }

    private static string SliceBatchJson(params string[] nodes) =>
        $"```json\n[{string.Join(',', nodes)}]\n```";

    private static string SliceObjective(string id, string path, string? dependency = null)
    {
        var dependencies = dependency is null ? "[]" : $"[\"{dependency}\"]";
        return $"{{\"id\":\"{id}\",\"objective\":\"Implement {id}.\\n\\nTarget files/scopes:\\nScope confidence: precise\\nIncludes:\\n- {path}\",\"dependsOn\":{dependencies}}}";
    }

    private static (AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents, IModelProviderRegistry providers)
        BuildTestContext(string plannerOutput)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();

        var plannerAgent = new AgentDefinition(
            AgentId.New(),
            "Test-Planner",
            AgentRole.Planner,
            new ModelProfile("Fake", "fake-plan-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);

        var developerAgent = new AgentDefinition(
            AgentId.New(),
            "Test-Developer",
            AgentRole.Developer,
            new ModelProfile("Fake", "fake-dev-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);

        IReadOnlyList<AgentDefinition> agents = [plannerAgent, developerAgent];
        var provider = new FakeSmokeProvider(plannerOutput, providerName: "Fake");
        IModelProviderRegistry providers = new InMemoryModelProviderRegistry([provider]);

        return (kernel, workspace, agents, providers);
    }

    private static (AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents, IModelProviderRegistry providers)
        BuildSliceBatchTestContext(string plannerOutput)
    {
        var context = BuildTestContext(plannerOutput);
        ModelFunctionCatalogStore.Save(context.workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        return context;
    }

}
