using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsBriefLint : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData("--brief-file", true)]
    [Xunit.InlineData("--text-file", true)]
    [Xunit.InlineData("inline", true)]
    [Xunit.InlineData("--brief-file", false)]
    [Xunit.InlineData("--text-file", false)]
    [Xunit.InlineData("inline", false)]
    public async Task GoalCreationPrintsFindingsAndStillPersistsGoal(string source, bool hasTrap)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile("brief-lint-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var brief = hasTrap ? "Create the .git-keep marker." : "Update the display label.";
        var path = Path.Combine(root, "brief.md");
        File.WriteAllText(path, brief);
        string[] parts = source == "inline" ? ["goal", brief] : ["goal", source, path];
        var repository = new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel());
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var changed = false;

        var output = CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            parts, repository, workspace, ref agents,
            new InMemoryModelProviderRegistry([new BriefRefinerProvider()]), ref profiles, ref currentGoal));

        Xunit.Assert.True(changed);
        var restored = await repository.LoadAsync();
        var created = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.Equal(brief, created.Objective);
        Xunit.Assert.Equal(created.Id, Xunit.Assert.IsType<Goal>(currentGoal).Id);
        AssertLintOutput(output, hasTrap);
    }

    [Xunit.Theory]
    [Xunit.InlineData("--brief-file", true)]
    [Xunit.InlineData("--text-file", true)]
    [Xunit.InlineData("--brief-file", false)]
    [Xunit.InlineData("--text-file", false)]
    [Xunit.InlineData("inline", false)]
    public void RevisionPrintsFindingsAndStillRecordsNewBrief(string source, bool hasTrap)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Original brief", [new TaskSpec(TaskId.New(), "work", AgentRole.Developer)]);
        // Inline revision intentionally remains outside the lint surface, even with a trap.
        var brief = hasTrap || source == "inline" ? "Create the .git-keep marker." : "Update the display label.";
        var path = Path.Combine(root, "revision.md");
        File.WriteAllText(path, brief);
        string[] parts = source == "inline"
            ? ["revise", goal.Id.Value, brief]
            : ["revise", goal.Id.Value, source, path];

        var output = ExecuteCliAndCapture(parts, kernel, workspace);

        AssertLintOutput(output, hasTrap);
        Xunit.Assert.Contains("authoritative=v2", output, StringComparison.Ordinal);
        Xunit.Assert.Equal(brief, goal.Objective);
        Xunit.Assert.Equal(2, goal.BriefVersions.Count);
        Xunit.Assert.Equal(brief, goal.AuthoritativeBrief.Text);
    }

    private static void AssertLintOutput(string output, bool hasTrap)
    {
        var lines = output.Split('\n').Where(line => line.StartsWith("BRIEF-LINT ", StringComparison.Ordinal)).ToArray();
        if (hasTrap)
            Xunit.Assert.StartsWith("BRIEF-LINT blocks-dispatch git-directory-reference:", Xunit.Assert.Single(lines), StringComparison.Ordinal);
        else
            Xunit.Assert.Empty(lines);
    }

    private sealed class BriefRefinerProvider : IModelProvider
    {
        public string ProviderName => "brief-lint-refiner";

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelResponse("""
                {
                  "behavioralContract": "Update the requested label or marker.",
                  "acceptanceCriteria": ["The requested change is visible."],
                  "verificationClass": "TestVerifiable",
                  "decisions": [],
                  "forks": []
                }
                """, new ModelUsage(1, 1), "stop"));
    }
}
