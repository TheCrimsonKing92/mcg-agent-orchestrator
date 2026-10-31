using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns its workspace and databases; console capture is AsyncLocal.
public sealed class CliCommandTestsBacklogPremiseCheck : CliCommandTestBase
{
    [Fact]
    public async Task CheckPrintsAllRulesSkimAndExactSummary()
    {
        using var seed = await CreateSeedAsync();
        var output = ExecuteCliAndCapture(["backlog-premise-check"], seed.Kernel, seed.Workspace);
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Equal(3, lines.Count(line => line.StartsWith("propose-close ", StringComparison.Ordinal)));
        Assert.Equal(1, lines.Count(line => line.StartsWith("skim ", StringComparison.Ordinal)));
        Assert.Contains(lines, line => line.StartsWith($"propose-close {seed.Missing.Id[..8]} ", StringComparison.Ordinal) &&
            line.Contains("rules=A,B ") && line.Contains($"refs:{seed.Done.Id[..8]}"));
        Assert.Contains(lines, line => line.StartsWith($"propose-close {seed.Linked.Id[..8]} ", StringComparison.Ordinal) &&
            line.Contains("rules=C ") && line.Contains("goal:bbbb0001"));
        Assert.Contains(lines, line => line.StartsWith($"propose-close {seed.Named.Id[..8]} ", StringComparison.Ordinal) &&
            line.Contains("rules=B,D ") && line.Contains("goal:bbbb0002"));
        Assert.Contains(lines, line => line.StartsWith($"skim {seed.Skim.Id[..8]} group=no-signal ", StringComparison.Ordinal));
        Assert.Contains("Backlog premise check: eligible=4 exempt-north-star=0 exempt-epic=1 propose-close=3 skim=1 min-age-days=30", lines);
    }

    [Fact]
    public async Task CheckPreservesEveryRowsStatusUpdatedAtAndBody()
    {
        using var seed = await CreateSeedAsync();
        var before = Rows(seed.Workspace);
        ExecuteCliAndCapture(["backlog-premise-check"], seed.Kernel, seed.Workspace);
        Assert.Equal(before, Rows(seed.Workspace));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedCloseReevaluatesAndRefusesSkimAndUnknownPrefixes(bool interactive)
    {
        using var seed = await CreateSeedAsync();
        var before = Rows(seed.Workspace);
        ExecuteCliAndCapture(["backlog-premise-check"], seed.Kernel, seed.Workspace);
        string[] args = ["backlog-premise-close", seed.Missing.Id[..8], seed.Linked.Id[..8],
            seed.Skim.Id[..8], "zzzzzzzz", "--confirm-backlog-premise-close"];
        var parts = interactive ? CliArgumentParser.SplitCommand(string.Join(' ', args)) : CliArgumentParser.NormalizeArgs(args);
        var output = ExecuteCliAndCapture(parts, seed.Kernel, seed.Workspace);
        var store = BacklogStore.OpenReadOnly(seed.Workspace.BacklogStorePath);
        foreach (var id in new[] { seed.Missing.Id, seed.Linked.Id })
        {
            var closed = await store.GetByExactIdAsync(id);
            Assert.NotNull(closed);
            Assert.Equal(BacklogItemStatus.Done, closed.Status);
            Assert.Contains("Closed: premise-check ", closed.Body);
        }
        Assert.Contains($"refused {seed.Skim.Id[..8]}: no-close-rule", output);
        Assert.Contains("refused zzzzzzzz: not-found", output);
        Assert.Contains("Backlog premise close: closed=2 refused=2", output);
        Assert.Equal(before.Where(row => row.Id != seed.Missing.Id && row.Id != seed.Linked.Id),
            Rows(seed.Workspace).Where(row => row.Id != seed.Missing.Id && row.Id != seed.Linked.Id));
    }

    [Fact]
    public async Task CloseWithoutConfirmationThrowsUsageAndPreservesEveryRow()
    {
        using var seed = await CreateSeedAsync();
        var before = Rows(seed.Workspace);
        var error = Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            ["backlog-premise-close", seed.Missing.Id[..8], seed.Linked.Id[..8], seed.Skim.Id[..8], "zzzzzzzz"], seed.Kernel, seed.Workspace));
        Assert.Contains(CliCommandHelp.BacklogPremiseCloseUsage, error.Message);
        Assert.Equal(before, Rows(seed.Workspace));
    }

    [Fact]
    public async Task CloseDoesNotReuseEarlierCheckProposals()
    {
        using var seed = await CreateSeedAsync();
        ExecuteCliAndCapture(["backlog-premise-check"], seed.Kernel, seed.Workspace);
        Directory.CreateDirectory(Path.Combine(seed.Workspace.RootDirectory, "src"));
        File.WriteAllText(Path.Combine(seed.Workspace.RootDirectory, "src", "retired-premise.cs"), "restored");
        var before = Rows(seed.Workspace);
        var output = ExecuteCliAndCapture(["backlog-premise-close", seed.Missing.Id[..8], "--confirm-backlog-premise-close"], seed.Kernel, seed.Workspace);
        Assert.Contains($"refused {seed.Missing.Id[..8]}: no-close-rule", output);
        Assert.Contains("closed=0 refused=1", output);
        Assert.Equal(before, Rows(seed.Workspace));
    }

    [Fact]
    public async Task CloseRefusesExemptAndAlreadyClosedItemsIncludingDuplicateArguments()
    {
        using var seed = await CreateSeedAsync();
        var output = ExecuteCliAndCapture(["backlog-premise-close", seed.Epic.Id[..8], seed.Done.Id[..8],
            seed.Linked.Id[..8], seed.Linked.Id[..8].ToUpperInvariant(), "--confirm-backlog-premise-close"], seed.Kernel, seed.Workspace);
        Assert.Contains($"refused {seed.Epic.Id[..8]}: not-eligible", output);
        Assert.Contains($"refused {seed.Done.Id[..8]}: not-open", output);
        Assert.Contains($"refused {seed.Linked.Id[..8]}: not-open", output);
        Assert.Contains("closed=1 refused=3", output);
    }

    [Theory]
    [InlineData("backlog-premise-check", "--min-age-days", "-1")]
    [InlineData("backlog-premise-check", "--min-age-days", "abc")]
    [InlineData("backlog-premise-check", "--min-age-days")]
    [InlineData("backlog-premise-close", "--confirm-backlog-premise-close")]
    [InlineData("backlog-premise-close", "abc", "--confirm-backlog-premise-close", "--min-age-days", "-1")]
    public void InvalidArgumentsThrowBeforeOpeningMissingBacklog(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var error = Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(args, new AgentOrchestratorKernel(), workspace));
            Assert.Contains(args[0] == "backlog-premise-check" ? CliCommandHelp.BacklogPremiseCheckUsage : CliCommandHelp.BacklogPremiseCloseUsage, error.Message);
            Assert.False(File.Exists(workspace.BacklogStorePath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private async Task<Seed> CreateSeedAsync()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var store = new BacklogStore(workspace.BacklogStorePath);
        // A note-only citation also proves that the command hydrates notes after ListAsync.
        var missing = await store.AddAsync("Amber", "obsolete implementation");
        await store.AppendNoteAsync(missing.Id, "src/retired-premise.cs");
        var linked = await store.AddAsync("Bronze", "linked implementation");
        var named = await store.AddAsync("Copper", "sole named target");
        var skim = await store.AddAsync("Diamond", "owner judgement required");
        var epic = await store.AddAsync("Emerald", "protected by epic");
        var done = await store.AddAsync("Fuchsia", $"Resolved {missing.Id[..8]}");
        await store.CloseAsync(done.Id);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = workspace.BacklogStorePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE backlog SET updated_at = $old";
            command.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddDays(-90).ToString("O"));
            command.ExecuteNonQuery();
        }
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var portfolioEpic = await portfolio.AddEpicAsync("Premise fixture");
        await portfolio.AssignBacklogItemToEpicAsync(epic.Id, portfolioEpic.Id);
        var kernel = new AgentOrchestratorKernel();
        var linkedGoal = kernel.CreateGoal(new GoalId("bbbb0001aaaaaaaaaaaaaaaaaaaaaaaa"), "Orchid");
        kernel.SetGoalSourceBacklogItemLink(linkedGoal.Id, linked.Id, SourceBacklogCoverage.Full);
        kernel.CreateGoal(new GoalId("bbbb0002aaaaaaaaaaaaaaaaaaaaaaaa"), $"Fix the thing (backlog {named.Id[..8]}).");
        // The epic exemption must override otherwise sufficient Rule C evidence.
        var epicGoal = kernel.CreateGoal(new GoalId("bbbb0003aaaaaaaaaaaaaaaaaaaaaaaa"), "Sapphire");
        kernel.SetGoalSourceBacklogItemLink(epicGoal.Id, epic.Id, SourceBacklogCoverage.Full);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with { Status = GoalStatus.Completed }).ToArray()
        });
        return new(workspace, kernel, missing, linked, named, skim, epic, done);
    }

    private static Row[] Rows(OrchestratorWorkspace workspace)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspace.BacklogStorePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, status, updated_at, body FROM backlog ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows.ToArray();
    }

    private sealed record Row(string Id, string Status, string UpdatedAt, string Body);
    private sealed record Seed(OrchestratorWorkspace Workspace, AgentOrchestratorKernel Kernel,
        BacklogItem Missing, BacklogItem Linked, BacklogItem Named, BacklogItem Skim, BacklogItem Epic, BacklogItem Done) : IDisposable
    {
        public void Dispose() => Directory.Delete(Workspace.RootDirectory, recursive: true);
    }
}
