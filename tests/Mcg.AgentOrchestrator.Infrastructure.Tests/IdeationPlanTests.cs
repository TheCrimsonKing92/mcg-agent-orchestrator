using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class IdeationPlanTests
{
    // ── Parse: valid fenced JSON returns ranked ideas ────────────────────────

    [Xunit.Fact(DisplayName = "IdeationPlan_Parse_ValidFencedJson_ReturnsIdeas")]
    public void IdeationPlan_Parse_ValidFencedJson_ReturnsIdeas()
    {
        var json = """
            ```json
            [{"title":"Reduce rework","rationale":"loop-health shows 12% rework rate","scope":"acceptance gate","value":"Fewer wasted dispatches","effort":"Medium","risk":"May over-reject"}]
            ```
            """;
        var plan = IdeationProposalPlanner.Parse(json);

        Assert.True(plan.IsValid);
        Assert.True(plan.Ideas.Count == 1);
        Assert.True(plan.Ideas[0].Title == "Reduce rework");
        Assert.True(plan.Ideas[0].Effort == "Medium");
    }

    // ── Parse: multiple ranked ideas ────────────────────────────────────────

    [Xunit.Fact(DisplayName = "IdeationPlan_Parse_MultipleIdeas_ReturnsAllInOrder")]
    public void IdeationPlan_Parse_MultipleIdeas_ReturnsAllInOrder()
    {
        var json = """
            ```json
            [
              {"title":"Idea A","rationale":"loop-health shows 3 escalations","scope":"conductor","value":"High","effort":"Low","risk":"None"},
              {"title":"Idea B","rationale":"dogfood log shows 2 retries","scope":"worker dispatch","value":"Medium","effort":"Medium","risk":"Low"}
            ]
            ```
            """;
        var plan = IdeationProposalPlanner.Parse(json);

        Assert.True(plan.IsValid);
        Assert.True(plan.Ideas.Count == 2);
        Assert.True(plan.Ideas[0].Title == "Idea A");
        Assert.True(plan.Ideas[1].Title == "Idea B");
    }

    // ── Parse: missing fenced block ─────────────────────────────────────────

    [Xunit.Fact(DisplayName = "IdeationPlan_Parse_NoFencedJson_ReturnsError")]
    public void IdeationPlan_Parse_NoFencedJson_ReturnsError()
    {
        var plan = IdeationProposalPlanner.Parse("Here are my ideas: improve X, improve Y.");

        Assert.False(plan.IsValid);
        Assert.True(plan.ValidationErrors.Any(e => e.Contains("fenced JSON", StringComparison.OrdinalIgnoreCase)));
    }

    // ── Parse: hand-wavy rationale rejected ─────────────────────────────────

    [Xunit.Fact(DisplayName = "IdeationPlan_Parse_HandWavyRationale_ReturnsError")]
    public void IdeationPlan_Parse_HandWavyRationale_ReturnsError()
    {
        var json = """
            ```json
            [{"title":"Make it better","rationale":"This would improve the system generally","scope":"all","value":"Better UX","effort":"Low","risk":"None"}]
            ```
            """;
        var plan = IdeationProposalPlanner.Parse(json);

        Assert.False(plan.IsValid);
        Assert.True(plan.ValidationErrors.Any(e =>
            e.Contains("evidence citation", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("Make it better", StringComparison.Ordinal)));
    }

    // ── Parse: numeric data point counts as evidence citation ───────────────

    [Xunit.Fact(DisplayName = "IdeationPlan_Parse_NumericDataPoint_CountsAsEvidence")]
    public void IdeationPlan_Parse_NumericDataPoint_CountsAsEvidence()
    {
        var json = """
            ```json
            [{"title":"Fix 3 escalations","rationale":"There were 3 operator escalations in the last week","scope":"conductor","value":"Smoother automation","effort":"Low","risk":"None"}]
            ```
            """;
        var plan = IdeationProposalPlanner.Parse(json);

        Assert.True(plan.IsValid);
    }

    // ── HasEvidenceCitation: keyword check ───────────────────────────────────

    [Xunit.Fact(DisplayName = "IdeationPlan_HasEvidenceCitation_KnownKeyword_ReturnsTrue")]
    public void IdeationPlan_HasEvidenceCitation_KnownKeyword_ReturnsTrue()
    {
        Assert.True(IdeationProposalPlanner.HasEvidenceCitation("provenance audit shows 2 unbacked goals"));
        Assert.True(IdeationProposalPlanner.HasEvidenceCitation("loop-health shows 9% rework"));
        Assert.True(IdeationProposalPlanner.HasEvidenceCitation("dogfood log reveals repeated retries"));
        Assert.True(IdeationProposalPlanner.HasEvidenceCitation("backlog item was never started"));
    }

    [Xunit.Fact(DisplayName = "IdeationPlan_HasEvidenceCitation_GenericText_ReturnsFalse")]
    public void IdeationPlan_HasEvidenceCitation_GenericText_ReturnsFalse()
    {
        Assert.False(IdeationProposalPlanner.HasEvidenceCitation("This would be nice to have"));
        Assert.False(IdeationProposalPlanner.HasEvidenceCitation("Improvement to the overall system"));
        Assert.False(IdeationProposalPlanner.HasEvidenceCitation("General enhancement"));
    }

    // ── CLI: ideate preview does not append to backlog ───────────────────────

    [Xunit.Fact(DisplayName = "Cli_Ideate_Preview_ShowsIdeasWithoutAppending")]
    public void Cli_Ideate_Preview_ShowsIdeasWithoutAppending()
    {
        var (kernel, workspace, agents, providers) = BuildTestContext(SingleIdeaJson);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["ideate"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        // No goals created; main kernel is untouched
        Assert.True(kernel.Goals.Count == 0);
        Assert.True(output.Contains("Reduce rework", StringComparison.Ordinal));

        // BACKLOG.md should not have been created
        var backlogPath = Path.Combine(workspace.ExecutionDirectory, "BACKLOG.md");
        Assert.False(File.Exists(backlogPath));
    }

    // ── CLI: ideate --append-backlog appends valid ideas ────────────────────

    [Xunit.Fact(DisplayName = "Cli_Ideate_AppendBacklog_AppendsIdeasToFile")]
    public void Cli_Ideate_AppendBacklog_AppendsIdeasToFile()
    {
        var (kernel, workspace, agents, providers) = BuildTestContext(SingleIdeaJson);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        // Create the BACKLOG.md file first
        var backlogPath = Path.Combine(workspace.ExecutionDirectory, "BACKLOG.md");
        File.WriteAllText(backlogPath, "# Backlog\n\n");

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["ideate", "--append-backlog"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var content = File.ReadAllText(backlogPath);
        Assert.True(content.Contains("Reduce rework", StringComparison.Ordinal));
    }

    // ── CLI: ideate --append-backlog throws on invalid plan ──────────────────

    [Xunit.Fact(DisplayName = "Cli_Ideate_AppendBacklog_HandWavyPlan_Throws")]
    public void Cli_Ideate_AppendBacklog_HandWavyPlan_Throws()
    {
        var handWavyJson = """
            ```json
            [{"title":"Make it better","rationale":"This would improve things","scope":"all","value":"Better","effort":"Low","risk":"None"}]
            ```
            """;
        var (kernel, workspace, agents, providers) = BuildTestContext(handWavyJson);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var backlogPath = Path.Combine(workspace.ExecutionDirectory, "BACKLOG.md");
        File.WriteAllText(backlogPath, "# Backlog\n\n");
        var originalContent = File.ReadAllText(backlogPath);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["ideate", "--append-backlog"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));

        Assert.True(ex.Message.Contains("validation error", StringComparison.OrdinalIgnoreCase));
        // BACKLOG.md should not have been modified
        Assert.True(File.ReadAllText(backlogPath) == originalContent);
    }

    // ── SelectBestOfN: all-valid picks the most-ideas plan ──────────────────

    [Xunit.Fact(DisplayName = "IdeationPlan_SelectBestOfN_AllValid_PicksMostIdeas")]
    public void IdeationPlan_SelectBestOfN_AllValid_PicksMostIdeas()
    {
        var two = MakeValidPlan(2);
        var three = MakeValidPlan(3);
        var one = MakeValidPlan(1);

        var best = IdeationProposalPlanner.SelectBestOfN([two, three, one]);

        Assert.True(best.Ideas.Count == 3);
    }

    // ── SelectBestOfN: first invalid + second valid picks the valid one ──────

    [Xunit.Fact(DisplayName = "IdeationPlan_SelectBestOfN_FirstInvalidSecondValid_PicksValid")]
    public void IdeationPlan_SelectBestOfN_FirstInvalidSecondValid_PicksValid()
    {
        var invalid = new IdeationPlan([], ["Worker output did not contain a fenced JSON block."]);
        var valid = MakeValidPlan(2);

        var best = IdeationProposalPlanner.SelectBestOfN([invalid, valid]);

        Assert.True(best.IsValid);
        Assert.True(best.Ideas.Count == 2);
    }

    // ── SelectBestOfN: none valid returns ValidationErrors ───────────────────

    [Xunit.Fact(DisplayName = "IdeationPlan_SelectBestOfN_NoneValid_ReturnsValidationErrors")]
    public void IdeationPlan_SelectBestOfN_NoneValid_ReturnsValidationErrors()
    {
        var first = new IdeationPlan([], ["Parse failed."]);
        var second = new IdeationPlan([], ["No fenced JSON."]);

        var best = IdeationProposalPlanner.SelectBestOfN([first, second]);

        Assert.False(best.IsValid);
        Assert.True(best.ValidationErrors.Count > 0);
    }

    private static IdeationPlan MakeValidPlan(int ideaCount)
    {
        var ideas = Enumerable.Range(1, ideaCount)
            .Select(i => new IdeaProposal(
                $"Idea {i}",
                $"loop-health shows {i}% rework",
                "conductor",
                "value",
                "Low",
                "None"))
            .ToList();
        return new IdeationPlan(ideas, []);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string SingleIdeaJson = """
        ```json
        [{"title":"Reduce rework","rationale":"loop-health shows 12% rework rate across 5 goals","scope":"acceptance gate","value":"Fewer wasted dispatches","effort":"Medium","risk":"May tighten gates too aggressively"}]
        ```
        """;

    private static (AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents, IModelProviderRegistry providers)
        BuildTestContext(string ideationOutput)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();

        // Register an Ideation agent for the test
        var ideationAgent = new AgentDefinition(
            AgentId.New(),
            "Test-Ideation",
            AgentRole.Ideation,
            new ModelProfile("Fake", "fake-ideation-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);

        IReadOnlyList<AgentDefinition> agents = [ideationAgent];
        var provider = new FakeSmokeProvider(ideationOutput, providerName: "Fake");
        IModelProviderRegistry providers = new InMemoryModelProviderRegistry([provider]);

        return (kernel, workspace, agents, providers);
    }

}
