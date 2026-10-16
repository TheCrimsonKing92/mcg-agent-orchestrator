using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.Text.Json;

// Parallel-safe: each test owns its database files and request-local trace callbacks.
public sealed class EpicDetailReadSnapshotTests : CliCommandTestBase
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(0)]
    public async Task Read_SelectedEpic_UsesOriginalPromotionTimeAndRetainsUnknownDates(int days)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Promotion scope");
        var other = await portfolio.AddEpicAsync("Unrelated promotion");
        var kernel = new AgentOrchestratorKernel();
        var original = kernel.CreateGoal("Old promotion with recent metadata");
        var unknown = kernel.CreateGoal("Legacy unknown promotion");
        var sentinel = kernel.CreateGoal("Unrelated recent promotion");
        var exported = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(exported with
        {
            Goals = exported.Goals.Select(goal => goal with { Status = GoalStatus.Completed }).ToArray()
        });
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        repository.RecordPromotionFacts([new(original.Id.Value, new string('b', 40), new string('a', 40),
            new string('c', 40), now.AddDays(-14), GoalPromotionFact.SoloProvenance)]);
        repository.RecordPromotionFacts([new(sentinel.Id.Value, new string('e', 40), new string('d', 40),
            new string('f', 40), now, GoalPromotionFact.SoloProvenance)]);
        await portfolio.AssignGoalToEpicAsync(original.Id.Value, epic.Id);
        await portfolio.AssignGoalToEpicAsync(unknown.Id.Value, epic.Id);
        await portfolio.AssignGoalToEpicAsync(sentinel.Id.Value, other.Id);
        DateTimeOffset? since = days == 0 ? null : now.AddDays(-days);
        var expected = EpicProgressReadModel.Load(workspace, since).Single(row => row.Epic.Id == epic.Id);
        var before = Bytes(workspace);
        var scoped = await EpicDetailReadSnapshot.ReadAsync(workspace, epic.Id, since, CancellationToken.None);
        var detail = OwnerConsoleEpicViewModel.BuildDetail(scoped.Row, since, scoped.Goals);

        Assert.Equal(expected.MemberGoals, scoped.Row.MemberGoals);
        Assert.Equal(expected.WindowLandedCount, scoped.Row.WindowLandedCount);
        Assert.Equal(now.AddDays(-14), scoped.Row.MemberGoals.Single(goal => goal.Id == original.Id.Value).PromotionFact!.PromotedAt);
        Assert.Null(scoped.Row.MemberGoals.Single(goal => goal.Id == unknown.Id.Value).PromotionFact);
        Assert.DoesNotContain(scoped.Row.MemberGoals, goal => goal.Id == sentinel.Id.Value);
        Assert.Equal(days == 0 ? 2 : 0, detail.Landed.Count);
        Assert.Equal(days == 0 ? 0 : 1, detail.UnknownLandingDates!.Count);
        Assert.Empty(scoped.Goals);
        AssertBytes(before, workspace);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(0)]
    public async Task Read_SelectedEpic_ScopesEveryStoreAndMatchesBroadProjection(int days)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var project = await portfolio.AddProjectAsync("Selected project");
        var epic = await portfolio.AddEpicAsync("Selected epic", project.Id);
        var other = await portfolio.AddEpicAsync("Unrelated epic");
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var source = await backlog.AddAsync("Selected source");
        var ready = await backlog.AddAsync("Ready source");
        var prerequisite = await backlog.AddAsync("Direct prerequisite");
        await backlog.CloseAsync(prerequisite.Id);
        var sentinelBacklog = await backlog.AddAsync("Unrelated sentinel");
        await backlog.AddDependencyAsync(ready.Id, new(prerequisite.Id, BacklogDependencyTargetKind.Backlog));
        var kernel = new AgentOrchestratorKernel();
        var owner = kernel.CreateGoal("Selected owner");
        kernel.SetGoalSourceBacklogItemLink(owner.Id, source.Id, SourceBacklogCoverage.Slice);
        var sibling = kernel.CreateGoal("Historical delivery");
        kernel.SetGoalSourceBacklogItemLink(sibling.Id, source.Id, SourceBacklogCoverage.Full);
        var goalPrerequisite = kernel.CreateGoal("Completed goal prerequisite");
        var sentinelGoal = kernel.CreateGoal("Unrelated sentinel goal");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal =>
            goal.Id == sibling.Id.Value || goal.Id == goalPrerequisite.Id.Value ? goal with { Status = GoalStatus.Completed } : goal).ToArray() });
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        await backlog.AddDependencyAsync(ready.Id, new(goalPrerequisite.Id.Value, BacklogDependencyTargetKind.Goal), goalExists: _ => true);
        await portfolio.AssignGoalToEpicAsync(owner.Id.Value, epic.Id);
        await portfolio.AssignGoalToEpicAsync(sentinelGoal.Id.Value, other.Id);
        await portfolio.AssignBacklogItemToEpicAsync(source.Id, epic.Id);
        await portfolio.AssignBacklogItemToEpicAsync(sentinelBacklog.Id, other.Id);
        var plans = new EpicPlanStore(workspace.PortfolioStorePath);
        await plans.AddSliceAsync(epic.Id, source.Id);
        await plans.AddSliceAsync(epic.Id, ready.Id);
        Execute(workspace.SqliteStatePath, "INSERT INTO source_backlog_claims(backlog_item_id,owner_goal_id,coverage,version,updated_at) VALUES ($source,$owner,'Slice',1,$at)",
            ("$source", source.Id), ("$owner", owner.Id.Value), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        DateTimeOffset? since = days == 0 ? null : DateTimeOffset.UtcNow.AddDays(-days);
        var expected = EpicProgressReadModel.Load(workspace, since, true).Single(row => row.Epic.Id == epic.Id);
        var before = Bytes(workspace);
        var trace = new Trace();
        var result = await new WorkspaceEpicProgressSource(workspace, trace.Observe).LoadDetailAsync(epic.Id, since, CancellationToken.None);

        Assert.Equal(since, result.Since);
        Assert.Equal(expected.Members, result.Row.Members);
        Assert.Equal(expected.MemberGoals, result.Row.MemberGoals);
        Assert.Equal(expected.Project, result.Row.Project);
        Assert.Equal(expected.WindowCreatedCount, result.Row.WindowCreatedCount);
        Assert.Equal(expected.WindowLandedCount, result.Row.WindowLandedCount);
        Assert.Equal(expected.WindowFailedCount, result.Row.WindowFailedCount);
        Assert.Equal(expected.Plan!.Items.Select(item => item.Render()), result.Row.Plan!.Items.Select(item => item.Render()));
        // The public version 1 response emits Items directly, including every nested
        // goal's own coverage; rendered source coverage alone cannot prove parity.
        Assert.Equal(JsonSerializer.Serialize(expected.Plan.Items, EpicProgressAgentReadView.Options),
            JsonSerializer.Serialize(result.Row.Plan.Items, EpicProgressAgentReadView.Options));
        Assert.Equal(expected.Outcome.Render(), result.Row.Outcome.Render());
        Assert.Equal(EpicPlanSliceStatus.Ready, result.Row.Plan.Items[1].Status);
        var facts = result.Row.Plan.Items[0].SourceFacts!;
        Assert.Equal(SourceBacklogCoverage.Slice, facts.Coverage);
        Assert.Equal("Slice", facts.Owner!.Coverage);
        Assert.Equal("Slice", facts.LinkedGoals.Single(goal => goal.Id == owner.Id.Value).Coverage);
        Assert.Equal("Full", Assert.Single(facts.DeliveredGoals).Coverage);
        Assert.Equal(sibling.Id.Value, Assert.Single(facts.DeliveredGoals).Id);
        Assert.Equal(2, facts.LinkedGoals.Count);
        Assert.Equal([owner.Id.Value], result.Goals.Keys);
        Assert.Equal(3, trace.Paths.Count);
        Assert.Equal(3, trace.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(trace.Statements.Where(sql => sql.Contains(" FROM goals", StringComparison.Ordinal)), sql => Assert.Contains("WHERE", sql));
        Assert.All(trace.Statements.Where(sql => sql.Contains(" FROM backlog", StringComparison.Ordinal)), sql => Assert.Contains("WHERE", sql));
        Assert.All(trace.Statements.Where(sql => sql.Contains(" FROM source_backlog_claims", StringComparison.Ordinal)), sql => Assert.Contains("WHERE", sql));
        Assert.DoesNotContain(trace.Statements, sql => sql.Contains(sentinelGoal.Id.Value, StringComparison.Ordinal) || sql.Contains(sentinelBacklog.Id, StringComparison.Ordinal));
        Assert.Single(trace.Statements.Where(sql => sql.StartsWith("SELECT id, status, objective, updated_at", StringComparison.Ordinal)));
        Assert.Single(trace.Statements.Where(sql => sql.StartsWith("SELECT id, snapshot_json FROM goals", StringComparison.Ordinal)));
        Assert.Single(trace.Statements.Where(sql => sql.StartsWith("SELECT id, position, kind", StringComparison.Ordinal)));
        AssertBytes(before, workspace);
        Execute(workspace.SqliteStatePath, "UPDATE goals SET status='Cancelled' WHERE id=$id", ("$id", goalPrerequisite.Id.Value));
        var changed = await EpicDetailReadSnapshot.ReadAsync(workspace, epic.Id, since, CancellationToken.None);
        Assert.Equal(EpicPlanSliceStatus.Blocked, changed.Row.Plan!.Items[1].Status);
        Execute(workspace.BacklogStorePath, "DELETE FROM backlog WHERE id=$id", ("$id", prerequisite.Id));
        var missing = await EpicDetailReadSnapshot.ReadAsync(workspace, epic.Id, since, CancellationToken.None);
        Assert.Equal("blocked on " + prerequisite.Id, missing.Row.Plan!.Items[1].Detail);
    }

    [Fact]
    public async Task Read_WriteBeforeHydration_KeepsStateSnapshotAndNextRequestSeesChange()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Coherent epic");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Original objective");
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        await portfolio.AssignGoalToEpicAsync(goal.Id.Value, epic.Id);
        Execute(workspace.SqliteStatePath, "PRAGMA journal_mode=WAL");
        var wrote = false;
        Exception? writeFailure = null;
        var trace = new Trace(sql =>
        {
            if (wrote || !sql.StartsWith("SELECT id, snapshot_json FROM goals", StringComparison.Ordinal)) return;
            wrote = true;
            try
            {
                Execute(workspace.SqliteStatePath, "UPDATE goals SET objective='New objective', snapshot_json=json_set(snapshot_json,'$.Objective','New objective') WHERE id=$id", ("$id", goal.Id.Value));
            }
            catch (Exception ex) { writeFailure = ex; }
        });
        var captured = await EpicDetailReadSnapshot.ReadAsync(workspace, epic.Id, null, CancellationToken.None, trace.Observe);
        Assert.True(wrote);
        Assert.Null(writeFailure);
        Assert.Equal("Original objective", Assert.Single(captured.Row.MemberGoals).Title);
        Assert.Equal("Original objective", captured.Goals[goal.Id.Value].Objective);
        var next = await EpicDetailReadSnapshot.ReadAsync(workspace, epic.Id, null, CancellationToken.None);
        Assert.Equal("New objective", Assert.Single(next.Row.MemberGoals).Title);
        Assert.Equal("New objective", next.Goals[goal.Id.Value].Objective);
    }

    [Theory]
    [InlineData(null, 1, "ReconciliationRequired")]
    [InlineData("Slice", 1, "RemainingScope")]
    [InlineData("Full", 1, "ReconciliationRequired")]
    [InlineData("Bogus", 1, "Unresolved")]
    [InlineData(null, 2, "Unresolved")]
    public async Task Read_LegacySchema_PreservesCoverageAndAmbiguityWithoutWrites(string? coverage, int siblings, string expected)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        // A separate path avoids deleting a current state store to create the legacy fixture.
        var legacy = Path.Combine(root, "legacy-state.db");
        var source = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Legacy scope");
        using (var connection = new SqliteConnection("Data Source=" + legacy + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = coverage is null ?
                "CREATE TABLE goals(id TEXT, status TEXT, source_backlog_item_id TEXT)" :
                "CREATE TABLE goals(id TEXT, status TEXT, source_backlog_item_id TEXT, snapshot_json TEXT)";
            command.ExecuteNonQuery();
        }
        for (var i = 0; i < siblings; i++)
            Execute(legacy, coverage is null ? "INSERT INTO goals VALUES($id,'Completed',$source)" :
                "INSERT INTO goals VALUES($id,'Completed',$source,json_object('SourceBacklogCoverage',$coverage))",
                coverage is null ? [("$id", "legacy" + i), ("$source", source.Id)] :
                    [("$id", "legacy" + i), ("$source", source.Id), ("$coverage", coverage)]);
        var before = File.ReadAllBytes(legacy);
        using var session = new SqliteReadSession(legacy);
        var claims = EpicPlanGoalClaimSnapshot.ReadScoped(session.Connection, [source.Id], [], CancellationToken.None);
        var metadata = await SqliteOrchestratorStateRepository.ReadEpicMetadataAsync(session.Connection, claims.Goals.Keys, false);
        var plan = new EpicPlan("legacy", null, [new(1, EpicPlanItemKind.Slice, source.Id, null, false)], []);
        var status = Assert.Single(EpicPlanStatusReader.Compute(plan, new Dictionary<string, BacklogItem> { [source.Id] = source },
            metadata.ToDictionary(goal => goal.Id), claims));
        Assert.Equal(Enum.Parse<EpicPlanSliceStatus>(expected), status.Status);
        Assert.Equal(siblings, status.SourceFacts!.DeliveredGoals.Count);
        Assert.All(status.SourceFacts.LinkedGoals, goal => Assert.Equal(coverage, goal.Coverage));
        Assert.All(status.SourceFacts.DeliveredGoals, goal => Assert.Equal(coverage, goal.Coverage));
        Assert.Equal(before, File.ReadAllBytes(legacy));
    }

    [Fact]
    public async Task Read_ReplacementOwnerOutsideMembershipAndLinks_RemainsAuthoritative()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var source = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Claimed source");
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Replacement epic");
        await new EpicPlanStore(workspace.PortfolioStorePath).AddSliceAsync(epic.Id, source.Id);
        var kernel = new AgentOrchestratorKernel();
        var owner = kernel.CreateGoal("Replacement without a source link");
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        Execute(workspace.SqliteStatePath, "INSERT INTO source_backlog_claims(backlog_item_id,owner_goal_id,coverage,version,updated_at) VALUES($source,$owner,'Slice',1,$at)",
            ("$source", source.Id), ("$owner", owner.Id.Value), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        var result = await EpicDetailReadSnapshot.ReadAsync(workspace, epic.Id, null, CancellationToken.None);
        var facts = Assert.Single(result.Row.Plan!.Items).SourceFacts!;
        Assert.Equal(owner.Id.Value, facts.OwnerGoalId);
        Assert.Equal(EpicPlanOwnershipResolution.Persisted, facts.Resolution);
        Assert.Null(facts.Owner!.Coverage);
        Assert.Equal(SourceBacklogCoverage.Slice, facts.Coverage);
        Assert.Empty(facts.LinkedGoals);
        Execute(workspace.SqliteStatePath, "UPDATE source_backlog_claims SET owner_goal_id='absent-owner'");
        var next = await EpicDetailReadSnapshot.ReadAsync(workspace, epic.Id, null, CancellationToken.None);
        Assert.Equal(EpicPlanOwnershipResolution.MissingOwner, Assert.Single(next.Row.Plan!.Items).SourceFacts!.Resolution);
        Execute(workspace.SqliteStatePath, "UPDATE source_backlog_claims SET owner_goal_id=$owner,coverage='Bogus'", ("$owner", owner.Id.Value));
        var invalid = await EpicDetailReadSnapshot.ReadAsync(workspace, epic.Id, null, CancellationToken.None);
        Assert.Equal(EpicPlanOwnershipResolution.InvalidCoverage, Assert.Single(invalid.Row.Plan!.Items).SourceFacts!.Resolution);
    }

    [Fact]
    public async Task Read_EmptyEpicAndMissingStores_DoesNotReadGlobalStateOrCreateStores()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var epic = await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Empty epic");
        var trace = new Trace();
        var result = await EpicDetailReadSnapshot.ReadAsync(workspace, epic.Id, null, CancellationToken.None, trace.Observe);
        Assert.Empty(result.Row.Members);
        Assert.Empty(result.Goals);
        Assert.DoesNotContain(trace.Statements, sql => sql.Contains(" FROM goals", StringComparison.Ordinal) || sql.Contains(" FROM backlog ", StringComparison.Ordinal));
        var missing = Path.Combine(CreateTempDirectory(), "missing.db");
        Assert.Empty(EpicPlanGoalClaimSnapshot.Read(missing).Goals);
        Assert.False(File.Exists(missing));
        File.WriteAllText(missing, "corrupt authoritative state");
        Assert.Throws<SqliteException>(() => EpicPlanGoalClaimSnapshot.Read(missing));
        Assert.Equal("corrupt authoritative state", File.ReadAllText(missing));
    }

    private static void Execute(string path, string sql, params (string Name, string Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Read_CancellationBetweenStores_StopsAcquisitionAndDisposesReadSessions()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Cancelled epic");
        var source = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Selected source");
        await portfolio.AssignBacklogItemToEpicAsync(source.Id, epic.Id);
        // Establish WAL sidecars before taking the byte baseline and keep their lifetime stable.
        // SQLite can create an empty WAL when a read-only connection first reads a WAL database.
        using var portfolioReader = new SqliteReadSession(workspace.PortfolioStorePath);
        using var backlogReader = new SqliteReadSession(workspace.BacklogStorePath);
        foreach (var session in new[] { portfolioReader, backlogReader })
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema";
            command.ExecuteScalar();
        }
        var before = Bytes(workspace);
        using var cancellation = new CancellationTokenSource();
        var paths = new List<string>();
        var connections = new List<SqliteConnection>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EpicDetailReadSnapshot.ReadAsync(workspace,
            epic.Id, null, cancellation.Token, (path, connection) =>
            {
                paths.Add(path);
                connections.Add(connection);
                if (path == workspace.BacklogStorePath) cancellation.Cancel();
            }));
        Assert.Equal([workspace.PortfolioStorePath, workspace.BacklogStorePath], paths);
        Assert.All(connections, connection => Assert.Equal(System.Data.ConnectionState.Closed, connection.State));
        AssertBytes(before, workspace);
        // A new request can open all the same stores after cancellation without stale evidence.
        var next = await EpicDetailReadSnapshot.ReadAsync(workspace, epic.Id, null, CancellationToken.None);
        Assert.Equal(source.Id, Assert.Single(next.Row.Members).MemberId);
    }

    private static Dictionary<string, byte[]> Bytes(OrchestratorWorkspace workspace) =>
        new[] { workspace.PortfolioStorePath, workspace.BacklogStorePath, workspace.SqliteStatePath }
            .SelectMany(path => new[] { path, path + "-wal" }).Where(File.Exists)
            .ToDictionary(path => path, ReadStoreBytes);

    private static byte[] ReadStoreBytes(string path)
    {
        // Retained SQLite read sessions keep WAL handles open with read/write access.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void AssertBytes(Dictionary<string, byte[]> before, OrchestratorWorkspace workspace)
    {
        var after = Bytes(workspace);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }

    private sealed class Trace(Action<string>? onStatement = null)
    {
        internal readonly List<string> Paths = [];
        internal readonly List<string> Statements = [];
        private readonly List<delegate_trace> callbacks = [];
        internal void Observe(string path, SqliteConnection connection)
        {
            Paths.Add(path);
            delegate_trace callback = (_, statement) =>
            {
                var sql = statement.utf8_to_string();
                Statements.Add(sql);
                onStatement?.Invoke(sql);
            };
            callbacks.Add(callback);
            raw.sqlite3_trace(connection.Handle, callback, null);
        }
    }
}
