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

        var ex = Assert.Throws<InvalidOperationException>(() =>
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

    private static string CaptureConsole(Action action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return writer.ToString();
    }
}
