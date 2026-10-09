using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: per-test workspaces/databases, AsyncLocal console capture and no shared environment writes.
public sealed class CliCommandTestsEpicPlanVerbs : CliCommandTestBase
{
    private readonly string root = CreateTempDirectory();

    [Fact]
    public async Task Refusals_WriteNoBytesOrRows_RemoveKeepsMembershipAndBacklog()
    {
        var workspace = CreateRefinedWorkspace(root);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Plan");
        var otherEpic = await portfolio.AddEpicAsync("Other");
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlog.AddAsync("Own slice");
        var other = await backlog.AddAsync("Other slice");
        await portfolio.AssignBacklogItemToEpicAsync(other.Id, otherEpic.Id);
        Execute(workspace, "epic-plan-add", epic.Id, "--backlog", item.Id);
        Assert.Equal(epic.Id, (await portfolio.GetBacklogMembershipAsync(item.Id))!.EpicId);

        string[][] refusals =
        [
            ["epic-plan-add", epic.Id, "--backlog", other.Id],
            ["epic-plan-add", epic.Id, "--backlog", item.Id],
            ["epic-plan-done", epic.Id, "1"],
            ["epic-plan-add", epic.Id, "--backlog", item.Id, "--at", "3"],
            ["epic-plan-add", epic.Id, "--backlog", item.Id, "--step-file", TextFile("step")],
            ["epic-plan-add", epic.Id],
            ["epic-plan-add", epic.Id, "--backlog", "unknown"],
            ["epic-plan-add", epic.Id, "--step-file", TextFile(" ")],
            ["epic-plan-move", epic.Id, "1", "2"],
            ["epic-plan-remove", epic.Id, "2"],
            ["epic-plan-add", epic.Id, "--backlog", item.Id, "--at", "0"],
            ["epic-plan-add", epic.Id, "--backlog", item.Id, "--backlog", item.Id],
            ["epic-plan-add", epic.Id, "--backlog"],
            ["epic-plan-add", epic.Id, "--backlog", item.Id, "--unknown", "value"],
            ["epic-decide", epic.Id, "--text-file", TextFile("")],
            ["epic-bar", epic.Id, "--text-file", Path.Combine(root, "missing.txt")]
        ];
        foreach (var command in refusals)
        {
            var bytes = File.ReadAllBytes(workspace.PortfolioStorePath);
            var rows = Dump(workspace.PortfolioStorePath);
            var error = Assert.ThrowsAny<Exception>(() => Execute(workspace, command));
            Assert.True(error is ArgumentException or InvalidOperationException or IOException, error.ToString());
            Assert.Equal(bytes, File.ReadAllBytes(workspace.PortfolioStorePath));
            Assert.Equal(rows, Dump(workspace.PortfolioStorePath));
            if (command[0] == "epic-plan-done")
            {
                Assert.IsType<InvalidOperationException>(error);
                Assert.Equal("slice status follows its backlog item and goal", error.Message);
            }
            if (command.SequenceEqual(new[] { "epic-plan-add", epic.Id, "--backlog", other.Id }))
            {
                Assert.IsType<InvalidOperationException>(error);
                Assert.Contains($"belongs to epic Other ({otherEpic.Id[..8]})", error.Message);
            }
            if (command.SequenceEqual(new[] { "epic-plan-add", epic.Id, "--backlog", item.Id }))
            {
                Assert.IsType<InvalidOperationException>(error);
                Assert.Contains("already in this plan at 1", error.Message);
            }
        }

        Execute(workspace, "epic-plan-remove", epic.Id, "1");
        Assert.Empty((await EpicPlanStore.OpenReadOnly(workspace.PortfolioStorePath).LoadAsync(epic.Id)).Items);
        var retained = (await backlog.GetByIdPrefixAsync(item.Id))!;
        Assert.Equal((item.Id, item.Title, item.Body, item.Status, item.CreatedAt, item.UpdatedAt, item.SourceGoalId),
            (retained.Id, retained.Title, retained.Body, retained.Status, retained.CreatedAt, retained.UpdatedAt, retained.SourceGoalId));
        Assert.Empty(retained.Notes);
        Assert.Empty(retained.Links);
        Assert.Empty(retained.Dependencies);
        Assert.Empty(retained.Dependents);
        Assert.Equal(epic.Id, (await portfolio.GetBacklogMembershipAsync(item.Id))!.EpicId);
        Assert.Contains($"BacklogItem: {item.Id}", Execute(workspace, "epic-plan", epic.Id));
    }

    [Fact]
    public async Task PositionsStepsDecisionsAndBar_RoundTripThroughVerbs()
    {
        var workspace = CreateRefinedWorkspace(root);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Plan", description: "Purpose of the plan");
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlog.AddAsync("Slice");
        Execute(workspace, "epic-plan-add", epic.Id, "--step-file", TextFile("First step"));
        Execute(workspace, "epic-plan-add", epic.Id, "--backlog", item.Id[..8], "--at", "1");
        Execute(workspace, "epic-plan-add", epic.Id, "--step-file", TextFile("Last step"));
        Execute(workspace, "epic-plan-move", epic.Id, "3", "1");
        var store = EpicPlanStore.OpenReadOnly(workspace.PortfolioStorePath);
        var plan = await store.LoadAsync(epic.Id);
        Assert.Equal(new[] { 1, 2, 3 }, plan.Items.Select(row => row.Position));
        Assert.Equal("Last step", plan.Items[0].Text);
        Assert.Equal(item.Id, plan.Items[1].BacklogItemId);
        Assert.Equal("First step", plan.Items[2].Text);
        Execute(workspace, "epic-plan-done", epic.Id, "1");
        var afterDone = File.ReadAllBytes(workspace.PortfolioStorePath);
        Execute(workspace, "epic-plan-done", epic.Id, "1");
        Assert.Equal(afterDone, File.ReadAllBytes(workspace.PortfolioStorePath));
        Execute(workspace, "epic-plan-remove", epic.Id, "2");
        plan = await store.LoadAsync(epic.Id);
        Assert.Equal(new[] { 1, 2 }, plan.Items.Select(row => row.Position));
        Assert.True(plan.Items[0].Done);
        Assert.False(plan.Items[1].Done);
        Execute(workspace, "epic-decide", epic.Id, "--text-file", TextFile("First decision"), "--by", "Owner");
        Execute(workspace, "epic-decide", epic.Id, "--text-file", TextFile("Second decision"));
        Execute(workspace, "epic-bar", epic.Id, "--text-file", TextFile("All slices shipped"));
        plan = await store.LoadAsync(epic.Id);
        Assert.Equal("All slices shipped", plan.Bar);
        Assert.Equal(new[] { "Second decision", "First decision" }, plan.Decisions.Select(row => row.Text));
        Assert.Null(plan.Decisions[0].DecidedBy);
        Assert.Equal("Owner", plan.Decisions[1].DecidedBy);
        Assert.All(plan.Decisions, row => Assert.Equal(TimeSpan.Zero, row.DecidedAt.Offset));
        var output = Execute(workspace, "epic-plan", epic.Id);
        Assert.Contains("Purpose: Purpose of the plan", output);
        Assert.Contains("Bar: All slices shipped", output);
        Assert.Contains("1. Last step — done", output);
        Assert.Contains("Next step: 2. First step — open", output);
        Assert.True(output.IndexOf("Second decision", StringComparison.Ordinal) < output.IndexOf("First decision", StringComparison.Ordinal));
        Execute(workspace, "epic-bar", epic.Id, "--text-file", TextFile(""));
        Assert.Null((await store.LoadAsync(epic.Id)).Bar);
    }

    [Theory]
    [InlineData("epic-plan")]
    [InlineData("epic-plan-add")]
    [InlineData("epic-plan-move")]
    [InlineData("epic-plan-remove")]
    [InlineData("epic-plan-done")]
    [InlineData("epic-decide")]
    [InlineData("epic-bar")]
    public void HelpAndUnknownFlags_AreRegistered(string command)
    {
        var workspace = CreateRefinedWorkspace(root);
        Assert.Contains(command, CliArgumentParser.RecognizedCommands);
        Assert.Contains($"Usage: {command}", Execute(workspace, command, "--help"));
        Assert.Throws<ArgumentException>(() => Execute(workspace, command, "--unknown-epic-option"));
        Assert.False(File.Exists(workspace.PortfolioStorePath));
    }

    private string TextFile(string text)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("n") + ".txt");
        File.WriteAllText(path, text);
        return path;
    }
    private static string Execute(OrchestratorWorkspace workspace, params string[] args) =>
        ExecuteCliAndCapture(args, new AgentOrchestratorKernel(), workspace);

    private static string Dump(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        var rows = new List<string>();
        foreach (var table in new[] { "projects", "epics", "backlog_epic_memberships", "goal_epic_memberships", "cluster_suggestions", "epic_plan_items", "epic_decisions", "store_schema_versions", "sqlite_sequence" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY 1";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(table + ":" + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
        }
        return string.Join("\n", rows);
    }

    public override async ValueTask DisposeAsync()
    {
        try { Directory.Delete(root, recursive: true); }
        finally { await base.DisposeAsync(); }
    }
}
