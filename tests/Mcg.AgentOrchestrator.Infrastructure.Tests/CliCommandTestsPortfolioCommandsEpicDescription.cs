using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: workspaces are per test; CaptureConsole uses the AsyncLocal console router.
public sealed class CliCommandTestsPortfolioCommandsEpicDescription : CliCommandTestBase
{
    private readonly string root = CreateTempDirectory();

    [Theory]
    [InlineData("--body-file")]
    [InlineData("--text-file")]
    public void Add_TitleAndBodyFile_StoresSeparateDescription(string flag)
    {
        var workspace = CreateRefinedWorkspace(root);
        var file = WriteTextFile("  Scope\nDone condition\n");
        var output = ExecuteCliAndCapture(["epic-add", "--title", "Remote execution", flag, file], new AgentOrchestratorKernel(), workspace);
        var epic = Assert.Single(new PortfolioStore(workspace.PortfolioStorePath).ListEpicsAsync().GetAwaiter().GetResult());
        Assert.Equal("Remote execution", epic.Title);
        Assert.Equal("Scope\nDone condition", epic.Description);
        Assert.Equal($"Added epic: [{epic.Id}] Remote execution{Environment.NewLine}", output);
    }

    [Fact]
    public void Add_PositionalTitleAndDescription_StoresSeparateFields()
    {
        var workspace = CreateRefinedWorkspace(root);
        ExecuteCliAndCapture(["epic-add", "Remote execution", "Scope and done condition"], new AgentOrchestratorKernel(), workspace);
        var epic = Assert.Single(new PortfolioStore(workspace.PortfolioStorePath).ListEpicsAsync().GetAwaiter().GetResult());
        Assert.Equal("Remote execution", epic.Title);
        Assert.Equal("Scope and done condition", epic.Description);
    }

    [Theory]
    [InlineData("First\nSecond\nThird")]
    [InlineData("First\rSecond")]
    [InlineData("long")]
    public void Add_LegacyFileWithLongOrMultilineTitle_RejectsWithoutCreatingEpic(string text)
    {
        var workspace = CreateRefinedWorkspace(root);
        var file = WriteTextFile(text == "long" ? new string('x', 201) : text);
        var error = Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            ["epic-add", "--text-file", file], new AgentOrchestratorKernel(), workspace));
        Assert.Contains("--title", error.Message);
        Assert.Empty(new PortfolioStore(workspace.PortfolioStorePath).ListEpicsAsync().GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData("Legacy title\r\n", "Legacy title")]
    [InlineData("boundary", "boundary")]
    public void Add_LegacyShortSingleLineFile_PreservesTitleForm(string text, string expected)
    {
        var workspace = CreateRefinedWorkspace(root);
        if (text == "boundary")
            text = expected = new string('x', 200);
        ExecuteCliAndCapture(["epic-add", "--text-file", WriteTextFile(text)], new AgentOrchestratorKernel(), workspace);
        var epic = Assert.Single(new PortfolioStore(workspace.PortfolioStorePath).ListEpicsAsync().GetAwaiter().GetResult());
        Assert.Equal(expected, epic.Title);
        Assert.Null(epic.Description);
    }

    [Fact]
    public void Show_Prefix_PrintsDescriptionMembersAndOneExistingRollupLine()
    {
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("aaaaaaaa111111111111111111111111"), "Remote feature");
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var project = store.AddProjectAsync("Control plane").GetAwaiter().GetResult();
        var epic = store.AddEpicAsync("Remote execution", project.Id, description: "Scope\nDone condition").GetAwaiter().GetResult();
        store.AssignGoalToEpicAsync(goal.Id.Value, epic.Id).GetAwaiter().GetResult();
        store.AssignBacklogItemToEpicAsync("backlog-member", epic.Id).GetAwaiter().GetResult();
        var members = ExecuteCliAndCapture(["epic-members", epic.Id[..8]], kernel, workspace);
        var rollup = ExecuteCliAndCapture(["epic-list"], kernel, workspace);
        var output = ExecuteCliAndCapture(["epic-show", epic.Id[..8]], kernel, workspace);

        Assert.Contains($"Epic: Remote execution{Environment.NewLine}Id: {epic.Id}{Environment.NewLine}", output);
        Assert.Contains($"Project: Control plane ({project.Id[..8]})", output);
        Assert.Contains($"Description:{Environment.NewLine}  Scope{Environment.NewLine}  Done condition{Environment.NewLine}", output);
        Assert.Contains($"  Goal: {goal.Id.Value}{Environment.NewLine}", output);
        Assert.Contains($"  BacklogItem: backlog-member{Environment.NewLine}", output);
        Assert.Contains(members, output);
        Assert.Contains(rollup, output);
        Assert.Single(output.Split(Environment.NewLine).Where(line => line.Contains(" goals=")));
    }

    [Fact]
    public void RenameAndDescribe_ShowReflectsEachChangeAndBlankClearsDescription()
    {
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = store.AddEpicAsync("Old title", description: "Old scope").GetAwaiter().GetResult();
        ExecuteCliAndCapture(["epic-rename", epic.Id[..8], "New title"], kernel, workspace);
        var renamed = ExecuteCliAndCapture(["epic-show", epic.Id[..8]], kernel, workspace);
        Assert.Contains("Epic: New title", renamed);
        Assert.Contains("Old scope", renamed);
        ExecuteCliAndCapture(["epic-describe", epic.Id[..8], "New scope"], kernel, workspace);
        var described = ExecuteCliAndCapture(["epic-show", "New title"], kernel, workspace);
        Assert.Contains("Epic: New title", described);
        Assert.Contains("New scope", described);
        Assert.DoesNotContain("Old scope", described);
        ExecuteCliAndCapture(["epic-describe", epic.Id[..8], "--text-file", WriteTextFile("File scope\nDone")], kernel, workspace);
        Assert.Contains("File scope", ExecuteCliAndCapture(["epic-show", epic.Id[..8]], kernel, workspace));
        ExecuteCliAndCapture(["epic-describe", epic.Id[..8], ""], kernel, workspace);
        Assert.Contains("Description: (no description)", ExecuteCliAndCapture(["epic-show", epic.Id[..8]], kernel, workspace));
        Assert.Null(store.ResolveEpicAsync(epic.Id).GetAwaiter().GetResult()!.Description);
    }

    [Fact]
    public void List_DescriptionAbsent_PreservesExactBaselineOutput()
    {
        var workspace = CreateRefinedWorkspace(root);
        var epic = new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Baseline epic").GetAwaiter().GetResult();
        var output = ExecuteCliAndCapture(["epic-list"], new AgentOrchestratorKernel(), workspace);
        Assert.Equal($"Baseline epic ({epic.Id[..8]}) project=unassigned goals=0 backlog=0 active=0 verified=0 parked=0 landed=0 newest=none verifying=0 failed=0 closed=0 missing=0 backlog-open=0 backlog-done=0{Environment.NewLine}", output);
        new PortfolioStore(workspace.PortfolioStorePath).UpdateEpicAsync(epic.Id, description: "Scope").GetAwaiter().GetResult();
        Assert.Equal(output, ExecuteCliAndCapture(["epic-list"], new AgentOrchestratorKernel(), workspace));
    }

    [Fact]
    public void Add_ConflictingTitleOrBodySources_RejectsWithoutCreatingEpic()
    {
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var file = WriteTextFile("Scope");
        string[][] commands =
        [
            ["epic-add", "Title", "--title", "Other"],
            ["epic-add", "--title", "Title", "--title", "Other"],
            ["epic-add", "--title", "Title", "positional body"],
            ["epic-add", "--title", "Title", "--body-file", file, "--text-file", file],
            ["epic-add", "--title", " "],
            ["epic-add", "Title", "inline body", "--body-file", file]
        ];
        foreach (var command in commands)
            Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(command, kernel, workspace));
        Assert.Empty(new PortfolioStore(workspace.PortfolioStorePath).ListEpicsAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void Show_UnknownOrAmbiguousEpic_UsesExistingResolutionErrors()
    {
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var error = Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(["epic-show", "missing"], kernel, workspace));
        Assert.Equal("Epic 'missing' was not found.", error.Message);
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        store.AddEpicAsync("Duplicate").GetAwaiter().GetResult();
        store.AddEpicAsync("Duplicate").GetAwaiter().GetResult();
        error = Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(["epic-show", "Duplicate"], kernel, workspace));
        Assert.Equal("Epic 'Duplicate' is ambiguous.", error.Message);
    }

    private string WriteTextFile(string text)
    {
        var path = Path.Combine(root, "description.txt");
        File.WriteAllText(path, text);
        return path;
    }

    public override async ValueTask DisposeAsync()
    {
        try { Directory.Delete(root, recursive: true); }
        finally { await base.DisposeAsync(); }
    }
}
