using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its workspace, databases and objective file.
// Drive the same argument normalization and flag validation as Program before dispatch.
public sealed class CliCommandTestsEpicAtCreationArguments : CliCommandTestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BacklogAdd_EpicArgument_PreservesTokensAndRecordsMembership(bool inline, bool titleFlag)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = portfolio.AddEpicAsync("Creation Board").GetAwaiter().GetResult();
        var args = new List<string> { "backlog-add" };
        if (titleFlag)
            args.Add("--title");
        args.Add("Creation member");
        args.AddRange(inline ? [$"--epic={epic.Id[..8]}"] : ["--epic", epic.Id[..8]]);
        args.Add("--no-similar");

        var parts = NormalizeAndValidate(args.ToArray());
        Assert.Equal(args.ToArray(), parts.ToArray());
        var output = ExecuteCliAndCapture(parts, new AgentOrchestratorKernel(), workspace);

        var item = Assert.Single(new BacklogStore(workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult());
        Assert.Equal(epic.Id, portfolio.GetBacklogMembershipAsync(item.Id).GetAwaiter().GetResult()!.EpicId);
        Assert.Contains($"Assigned backlog item {item.Id[..8]} to epic {epic.Id[..8]}", output);
    }

    [Theory]
    [InlineData("--text-file")]
    [InlineData("--brief-file")]
    public void Goal_InlineEpicTitle_CommitsAndRecordsMembership(string fileFlag)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = portfolio.AddEpicAsync("Creation Board").GetAwaiter().GetResult();
        var parts = NormalizeAndValidate(["goal", fileFlag, WriteObjective(workspace), $"--epic={epic.Title}"]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var providers = new InMemoryModelProviderRegistry([]);
        Goal? current = null;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(parts,
            new SqliteOrchestratorStateRepository(workspace.SqliteStatePath), workspace,
            ref agents, providers, ref profiles, ref current));

        var goal = Assert.Single(new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            .LoadAsync().GetAwaiter().GetResult().Goals);
        Assert.Equal(epic.Id, portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult()!.EpicId);
        Assert.Contains($"Assigned goal {goal.Id.Value[..8]} to epic {epic.Id[..8]}", output);
    }

    [Theory]
    [InlineData("goal", false)]
    [InlineData("backlog-add", false)]
    [InlineData("backlog-add", true)]
    public void Creation_UnknownInlineEpic_RejectsBeforeCreating(string command, bool titleFlag)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        string[] args = command == "goal"
            ? [command, "--text-file", WriteObjective(workspace), "--epic=missing-board"]
            : titleFlag
                ? [command, "--title", "New member", "--epic=missing-board", "--no-similar"]
                : [command, "New member", "--epic=missing-board", "--no-similar"];
        var parts = NormalizeAndValidate(args);

        var error = Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(parts, kernel, workspace));

        Assert.Contains("--epic 'missing-board'", error.Message);
        Assert.Contains("No epic matched", error.Message);
        Assert.Empty(kernel.Goals);
        Assert.Empty(new BacklogStore(workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("backlog-add")]
    public void Creation_EmptyInlineEpic_RejectsBeforeCreating(string command)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        var parts = NormalizeAndValidate([command, "New member", "--epic="]);

        var error = Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(parts, kernel, workspace));

        Assert.Equal("--epic requires an epic id or title.", error.Message);
        Assert.Empty(kernel.Goals);
        Assert.Empty(new BacklogStore(workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData("--simple")]
    [InlineData("--run")]
    [InlineData("--from-backlog")]
    public void Goal_AliasWithInlineEpic_RejectsBeforeCreating(string alias)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        var parts = NormalizeAndValidate(["goal", "New member", alias, "--epic=Board"]);

        var error = Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(parts, kernel, workspace));

        Assert.Contains("goal <objective>", error.Message);
        Assert.Contains("goal --text-file <path>", error.Message);
        Assert.Contains("goal --brief-file <path>", error.Message);
        Assert.Empty(kernel.Goals);
        Assert.Empty(new BacklogStore(workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult());
        Assert.False(File.Exists(workspace.PortfolioStorePath));
    }

    private static IReadOnlyList<string> NormalizeAndValidate(string[] args)
    {
        var parts = CliArgumentParser.NormalizeArgs(args);
        CliCommandHelp.ThrowIfInvalidFlags(parts);
        return parts;
    }

    private static string WriteObjective(OrchestratorWorkspace workspace)
    {
        var path = Path.Combine(workspace.ExecutionDirectory, "creation-arguments-brief.md");
        File.WriteAllText(path, "Update the CLI help label in src/Cli/Help.cs.");
        return path;
    }
}
