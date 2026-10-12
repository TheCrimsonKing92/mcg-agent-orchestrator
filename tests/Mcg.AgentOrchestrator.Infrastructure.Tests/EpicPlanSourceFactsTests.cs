using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns all stores and uses in-process CLI/formatting seams.
public sealed class EpicPlanSourceFactsTests : CliCommandTestBase
{
    private readonly string root = CreateTempDirectory();

    [Theory]
    [InlineData("Slice", "Open", "RemainingScope", false)]
    [InlineData("Full", "Open", "ReconciliationRequired", false)]
    [InlineData(null, "Open", "ReconciliationRequired", false)]
    [InlineData("Full", "Done", "Done", true)]
    [InlineData("Slice", "Superseded", "Superseded", true)]
    public async Task Delivered_history_does_not_complete_open_source_scope(string? coverage,
        string sourceState, string expected, bool complete)
    {
        var workspace = CreateRefinedWorkspace(root);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var source = await backlog.AddAsync("Umbrella scope");
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Truthful plan");
        await portfolio.AssignBacklogItemToEpicAsync(source.Id, epic.Id);
        await new EpicPlanStore(workspace.PortfolioStorePath).AddSliceAsync(epic.Id, source.Id);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Delivered increment");
        if (coverage is null) kernel.SetGoalSourceBacklogItemId(goal.Id, source.Id);
        else kernel.SetGoalSourceBacklogItemLink(goal.Id, source.Id, Enum.Parse<SourceBacklogCoverage>(coverage));
        kernel = WithStatus(kernel, goal.Id, GoalStatus.Completed);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        if (coverage is not null) await PersistClaim(workspace, source.Id, goal.Id.Value,
            Enum.Parse<SourceBacklogCoverage>(coverage));
        await portfolio.AssignGoalToEpicAsync(goal.Id.Value, epic.Id);
        if (sourceState == "Done") await backlog.CloseAsync(source.Id);
        if (sourceState == "Superseded")
        {
            var replacement = await backlog.AddAsync("Replacement scope");
            await backlog.SupersedeAsync(source.Id, replacement.Id);
        }
        var before = StoreBytes(workspace);
        for (var read = 0; read < 2; read++)
        {
            var view = EpicPlanStatusReader.Load(workspace, [epic])[epic.Id];
            var item = Assert.Single(view.Items);
            var facts = Assert.IsType<EpicPlanSourceFacts>(item.SourceFacts);
            Assert.Equal(Enum.Parse<EpicPlanSliceStatus>(expected), item.Status);
            Assert.Equal(complete, facts.ScopeComplete);
            Assert.False(facts.AutomaticIntakeEligible);
            Assert.Equal(goal.Id.Value, facts.OwnerGoalId);
            Assert.Equal(coverage == "Slice" ? SourceBacklogCoverage.Slice : SourceBacklogCoverage.Full, facts.Coverage);
            Assert.Equal(goal.Id.Value, Assert.Single(facts.DeliveredGoals).Id);
            Assert.Empty(view.UnplannedMembers);
            Assert.Empty(EpicPlanReadySliceSelector.Read(workspace));
            Assert.Equal(complete ? "complete" : EpicPlanNextStep.StalledText,
                EpicPlanNextStep.Compute(view.Items).Text);
            Assert.Equal(complete, !EpicPlanNextStep.Compute(view.Items).IsStalled);
            if (!complete) Assert.StartsWith("0 of 1 slices landed;", EpicPlanNextStep.Summary(view.Items));
            var cli = ExecuteCliAndCapture(["epic-plan", epic.Id], new AgentOrchestratorKernel(), workspace);
            var console = OwnerConsoleEpicPlanFormatter.Lines(new(epic, [], [], [], [], view), 1000);
            Assert.Contains(facts.Render(), cli);
            Assert.Contains("  " + facts.Render(), console);
            Assert.Contains("delivered: " + goal.Id.Value[..8] + " Completed", cli);
            Assert.Equal(complete, cli.Contains("Next step: complete", StringComparison.Ordinal));
            Assert.Equal(complete, console.Contains("Next step: complete"));
            Assert.Equal(complete ? 0 : 1, view.Items.Count(row => !row.ScopeComplete));
        }
        AssertStoreBytes(before, workspace);
    }

    [Fact]
    public async Task Persisted_owner_wins_while_completed_sibling_remains_delivered()
    {
        var workspace = CreateRefinedWorkspace(root);
        var source = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Shared scope");
        var epic = await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Plan");
        await new EpicPlanStore(workspace.PortfolioStorePath).AddSliceAsync(epic.Id, source.Id);
        var kernel = new AgentOrchestratorKernel();
        var owner = kernel.CreateGoal("Current slice owner");
        kernel.SetGoalSourceBacklogItemLink(owner.Id, source.Id, SourceBacklogCoverage.Slice);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        await PersistClaim(workspace, source.Id, owner.Id.Value, SourceBacklogCoverage.Slice);
        var sibling = kernel.CreateGoal("Completed historical sibling");
        kernel.SetGoalSourceBacklogItemLink(sibling.Id, source.Id, SourceBacklogCoverage.Full);
        kernel = WithStatus(kernel, sibling.Id, GoalStatus.Completed);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        await portfolio.AssignGoalToEpicAsync(owner.Id.Value, epic.Id);
        await portfolio.AssignGoalToEpicAsync(sibling.Id.Value, epic.Id);
        var before = StoreBytes(workspace);
        var view = EpicPlanStatusReader.Load(workspace, [epic])[epic.Id];
        var facts = Assert.Single(view.Items).SourceFacts!;
        Assert.Equal(EpicPlanOwnershipResolution.Persisted, facts.Resolution);
        Assert.Equal(owner.Id.Value, facts.OwnerGoalId);
        Assert.Equal(SourceBacklogCoverage.Slice, facts.Coverage);
        Assert.Equal(2, facts.LinkedGoals.Count);
        Assert.Equal(sibling.Id.Value, Assert.Single(facts.DeliveredGoals).Id);
        Assert.Equal(EpicPlanSliceStatus.InFlight, Assert.Single(view.Items).Status);
        Assert.Empty(view.UnplannedMembers);
        Assert.False(facts.ScopeComplete);
        Assert.False(facts.AutomaticIntakeEligible);
        AssertStoreBytes(before, workspace);
    }

    [Theory]
    [InlineData("ambiguous", "Ambiguous")]
    [InlineData("missing", "MissingOwner")]
    [InlineData("malformed", "InvalidCoverage")]
    [InlineData("numeric", "InvalidCoverage")]
    public async Task Unresolved_ownership_retains_delivery_and_fails_closed(string defect,
        string expected)
    {
        var workspace = CreateRefinedWorkspace(root);
        var source = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Unresolved scope");
        var epic = await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Plan");
        await new EpicPlanStore(workspace.PortfolioStorePath).AddSliceAsync(epic.Id, source.Id);
        var kernel = new AgentOrchestratorKernel();
        var delivered = kernel.CreateGoal("Delivered history");
        kernel.SetGoalSourceBacklogItemLink(delivered.Id, source.Id, SourceBacklogCoverage.Full);
        kernel = WithStatus(kernel, delivered.Id, GoalStatus.Completed);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        if (defect != "ambiguous")
            await PersistClaim(workspace, source.Id, delivered.Id.Value, SourceBacklogCoverage.Full);
        var other = kernel.CreateGoal("Other history");
        kernel.SetGoalSourceBacklogItemLink(other.Id, source.Id, SourceBacklogCoverage.Slice);
        kernel = WithStatus(kernel, delivered.Id, GoalStatus.Completed);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        if (defect != "ambiguous")
        {
            MutateClaim(workspace, source.Id, defect == "missing" ? "missing-goal" : other.Id.Value,
                defect == "malformed" ? "unknown" : defect == "numeric" ? "0" : "Full");
        }
        var before = StoreBytes(workspace);
        var view = EpicPlanStatusReader.Load(workspace, [epic])[epic.Id];
        var item = Assert.Single(view.Items);
        var facts = item.SourceFacts!;
        Assert.Equal(Enum.Parse<EpicPlanOwnershipResolution>(expected), facts.Resolution);
        Assert.Equal(EpicPlanSliceStatus.Unresolved, item.Status);
        Assert.False(facts.AutomaticIntakeEligible);
        Assert.False(facts.ScopeComplete);
        Assert.Equal(delivered.Id.Value, Assert.Single(facts.DeliveredGoals).Id);
        Assert.Equal(defect == "ambiguous" ? null : defect == "missing" ? "missing-goal" : other.Id.Value,
            facts.OwnerGoalId);
        if (defect == "ambiguous") Assert.Null(facts.Owner);
        if (defect is "malformed" or "numeric") Assert.Null(facts.Coverage);
        Assert.True(EpicPlanNextStep.Compute(view.Items).IsStalled);
        Assert.Empty(EpicPlanReadySliceSelector.Read(workspace));
        AssertStoreBytes(before, workspace);
    }

    [Fact]
    public async Task Reopened_full_source_shows_observed_open_and_reconciliation()
    {
        var workspace = CreateRefinedWorkspace(root);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var source = await backlog.AddAsync("Reopened scope");
        var epic = await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Plan");
        await new EpicPlanStore(workspace.PortfolioStorePath).AddSliceAsync(epic.Id, source.Id);
        var kernel = new AgentOrchestratorKernel();
        var owner = kernel.CreateGoal("Delivered full goal");
        kernel.SetGoalSourceBacklogItemLink(owner.Id, source.Id, SourceBacklogCoverage.Full);
        kernel = WithStatus(kernel, owner.Id, GoalStatus.Completed);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        await PersistClaim(workspace, source.Id, owner.Id.Value, SourceBacklogCoverage.Full);
        await backlog.CloseAsync(source.Id);
        Assert.True(Assert.Single(EpicPlanStatusReader.Load(workspace, [epic])[epic.Id].Items).ScopeComplete);
        await backlog.ReopenAsync(source.Id, "More source scope");
        var before = StoreBytes(workspace);
        var item = Assert.Single(EpicPlanStatusReader.Load(workspace, [epic])[epic.Id].Items);
        Assert.Equal(EpicPlanSliceStatus.ReconciliationRequired, item.Status);
        Assert.Equal(BacklogItemStatus.Open, item.SourceFacts!.SourceStatus);
        Assert.False(item.ScopeComplete);
        Assert.Equal(owner.Id.Value, Assert.Single(item.SourceFacts.DeliveredGoals).Id);
        Assert.DoesNotContain("reopened", item.SourceFacts.Render(), StringComparison.OrdinalIgnoreCase);
        AssertStoreBytes(before, workspace);
    }

    [Fact]
    public void Missing_state_read_does_not_create_a_store_and_unreadable_state_throws()
    {
        var path = Path.Combine(root, "absent.db");
        var source = BoardFillReadyItemSelectorTests.Item();
        Assert.True(EpicPlanGoalClaimSnapshot.Read(path).Resolve(source).AutomaticIntakeEligible);
        Assert.False(File.Exists(path));
        File.WriteAllText(path, "not SQLite");
        var before = File.ReadAllBytes(path);
        Assert.Throws<SqliteException>(() => EpicPlanGoalClaimSnapshot.Read(path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Legacy_schema_without_coverage_resolves_one_owner_as_full()
    {
        var path = Path.Combine(root, "legacy.db");
        using (var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE goals(id TEXT, status TEXT, source_backlog_item_id TEXT);
                INSERT INTO goals VALUES ('legacy-goal', 'Completed', $source);
                """;
            command.Parameters.AddWithValue("$source", BoardFillReadyItemSelectorTests.Item().Id);
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(path);
        var facts = EpicPlanGoalClaimSnapshot.Read(path).Resolve(BoardFillReadyItemSelectorTests.Item());
        Assert.Equal(EpicPlanOwnershipResolution.Legacy, facts.Resolution);
        Assert.Equal(SourceBacklogCoverage.Full, facts.Coverage);
        Assert.Equal("legacy-goal", facts.OwnerGoalId);
        Assert.False(facts.ScopeComplete);
        Assert.False(facts.AutomaticIntakeEligible);
        Assert.Equal("legacy-goal", Assert.Single(facts.DeliveredGoals).Id);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("backlog")]
    [InlineData("portfolio")]
    public async Task Store_write_guard_rejects_wal_and_checkpointed_changes(string store)
    {
        var workspace = CreateRefinedWorkspace(root);
        await new BacklogStore(workspace.BacklogStorePath).AddAsync("Source scope");
        await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Plan");
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("Owned increment");
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        var (path, update) = store switch
        {
            "state" => (workspace.SqliteStatePath, "UPDATE goals SET status='Cancelled'"),
            "backlog" => (workspace.BacklogStorePath, "UPDATE backlog SET title='Changed scope'"),
            _ => (workspace.PortfolioStorePath, "UPDATE epics SET title='Changed plan'")
        };
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        writer.Open();
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_checkpoint(TRUNCATE)";
        command.ExecuteNonQuery();
        Assert.Equal(0L, new FileInfo(path + "-wal").Length);
        var before = StoreBytes(workspace);
        AssertStoreBytes(before, workspace);

        // A committed write can live entirely in WAL while the main database is unchanged.
        command.CommandText = update;
        Assert.Equal(1, command.ExecuteNonQuery());
        Assert.Equal(before[path], ReadStoreBytes(path));
        Assert.True(new FileInfo(path + "-wal").Length > 0);
        Assert.Throws<Xunit.Sdk.EqualException>(() => AssertStoreBytes(before, workspace));

        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        command.ExecuteNonQuery();
        Assert.Equal(0L, new FileInfo(path + "-wal").Length);
        Assert.Throws<Xunit.Sdk.EqualException>(() => AssertStoreBytes(before, workspace));
    }

    private static AgentOrchestratorKernel WithStatus(AgentOrchestratorKernel kernel, GoalId id, GoalStatus status)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = snapshot.Goals.Select(goal => goal.Id == id.Value ? goal with { Status = status } : goal).ToArray() });
    }

    private static async Task PersistClaim(OrchestratorWorkspace workspace, string source, string owner,
        SourceBacklogCoverage coverage)
    {
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.TransactAsync((kernel, _) => Task.FromResult((ShouldSave: true,
            Result: new SourceBacklogClaimStore(workspace.SqliteStatePath).EnsureClaimForNewGoal(kernel, source, owner, coverage))));
    }

    // Corrupt fixtures exercise conservative reads, never replacement authorization.
    private static void MutateClaim(OrchestratorWorkspace workspace, string source, string owner, string coverage)
    {
        using var connection = new SqliteConnection("Data Source=" + workspace.SqliteStatePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE source_backlog_claims SET owner_goal_id=$owner, coverage=$coverage WHERE backlog_item_id=$source";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$coverage", coverage);
        command.Parameters.AddWithValue("$source", source);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static Dictionary<string, byte[]> StoreBytes(OrchestratorWorkspace workspace) =>
        new[] { workspace.SqliteStatePath, workspace.BacklogStorePath, workspace.PortfolioStorePath }
            .SelectMany(path => new[] { path, path + "-wal" }).Where(File.Exists)
            .ToDictionary(path => path, ReadStoreBytes)
            // SQLite read-only opens can create an empty WAL; it contains no persisted state.
            // Retain every database and nonempty WAL so both checkpointed and pending writes fail.
            .Where(pair => !pair.Key.EndsWith("-wal", StringComparison.Ordinal) || pair.Value.Length != 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    private static byte[] ReadStoreBytes(string path)
    {
        // The negative controls retain a SQLite writer to keep committed frames in WAL.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void AssertStoreBytes(Dictionary<string, byte[]> before, OrchestratorWorkspace workspace)
    {
        var after = StoreBytes(workspace);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var pair in after) Assert.Equal(before[pair.Key], pair.Value);
    }

    public override async ValueTask DisposeAsync()
    {
        try { Directory.Delete(root, recursive: true); }
        finally { await base.DisposeAsync(); }
    }
}
