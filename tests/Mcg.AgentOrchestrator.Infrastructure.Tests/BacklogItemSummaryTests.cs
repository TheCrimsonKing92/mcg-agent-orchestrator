using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns all databases, files and child handles; console capture is AsyncLocal.
public sealed class BacklogItemSummaryTests : IDisposable
{
    private readonly string root = CreateTempDirectory();
    private const string OwnerId = "aaaa1111000000000000000000000000";
    private const string BodyMarker = "UNIQUE_SUMMARY_BODY_MARKER";
    private const string NoteMarker = "UNIQUE_SUMMARY_NOTE_MARKER";
    private static readonly string[] Omitted =
        ["body", "notes", "linkedGoals", "lineage", "dependencies", "dependents", "ambiguousLinkedGoalIds"];

    [Fact]
    public async Task Cli_SummaryPreservesMetadataCountsAndCanonicalStores()
    {
        var seed = await SeedAsync("persisted", rich: true);
        Directory.CreateDirectory(seed.Workspace.LogDirectory);
        File.WriteAllText(Path.Combine(seed.Workspace.LogDirectory, "existing-evidence.log"), "retained evidence");
        var beforeState = SnapshotDatabase(seed.Workspace.SqliteStatePath);
        var beforeBacklog = SnapshotDatabase(seed.Workspace.BacklogStorePath);
        var beforeFiles = SnapshotArtifacts(seed.Workspace);

        var fullOutput = await RunCliAsync(["backlog-show", seed.Item.Id, "--json"]);
        using var full = JsonDocument.Parse(fullOutput);
        Assert.Equal("mcg.backlog-item", full.RootElement.GetProperty("schema").GetString());
        var detail = full.RootElement.GetProperty("payload");
        Assert.Contains(BodyMarker, fullOutput);
        Assert.Contains(NoteMarker, fullOutput);
        Assert.Equal(12, detail.GetProperty("linkedGoals").GetArrayLength());
        Assert.Equal(40, detail.GetProperty("notes").GetArrayLength());
        Assert.Equal(1, detail.GetProperty("lineage").GetArrayLength());
        Assert.Equal(1, detail.GetProperty("dependencies").GetArrayLength());
        Assert.Equal(1, detail.GetProperty("dependents").GetArrayLength());

        foreach (var flags in new[] { new[] { "--summary", "--json" }, new[] { "--json", "--summary" } })
        {
            var output = await RunCliAsync(["backlog-show", seed.Item.Id[..8], .. flags]);
            using var summary = AssertSummary(output);
            AssertParity(detail, summary.RootElement.GetProperty("payload"));
            AssertOmissions(output, summary.RootElement.GetProperty("payload"));
            Assert.Equal($"backlog-show {seed.Item.Id} --json",
                summary.RootElement.GetProperty("payload").GetProperty("fullDetailCommand").GetString());
        }

        var human = await RunCliAsync(["backlog-show", seed.Item.Id[..8], "--summary"]);
        Assert.Contains($"Title:   {seed.Item.Title}", human);
        Assert.Contains($"Id:      {seed.Item.Id}", human);
        Assert.Contains($"Owner:   {OwnerId}", human);
        Assert.Contains("Counts: linkedGoals=12 lineage=1 dependencies=1 dependents=1 notes=40", human);
        Assert.Contains($"Omitted: {string.Join(", ", Omitted)}", human);
        Assert.Contains($"Full detail: backlog-show {seed.Item.Id}" + Environment.NewLine, human);
        Assert.DoesNotContain(BodyMarker, human);
        Assert.DoesNotContain(NoteMarker, human);
        var fullHuman = await RunCliAsync(["backlog-show", seed.Item.Id]);
        Assert.Contains(BodyMarker, fullHuman);
        Assert.Contains(NoteMarker, fullHuman);
        Assert.DoesNotContain("Omitted:", fullHuman);
        Assert.Equal(beforeState, SnapshotDatabase(seed.Workspace.SqliteStatePath));
        Assert.Equal(beforeBacklog, SnapshotDatabase(seed.Workspace.BacklogStorePath));
        Assert.Equal(beforeFiles, SnapshotArtifacts(seed.Workspace));
    }

    [Theory]
    [InlineData("unclaimed", null, null, null, false, 0)]
    [InlineData("legacy", OwnerId, 1L, "full", false, 1)]
    [InlineData("ambiguous", null, null, null, true, 2)]
    [InlineData("persisted", OwnerId, 7L, "full", false, 2)]
    [InlineData("slice", OwnerId, 7L, "slice", false, 2)]
    [InlineData("missing-owner", OwnerId, 7L, "full", false, 1)]
    public async Task Ownership_SummaryMatchesAuthoritativeFullView(string mode, string? owner,
        long? version, string? coverage, bool ambiguous, int linkedCount)
    {
        var seed = await SeedAsync(mode);
        var beforeState = SnapshotDatabase(seed.Workspace.SqliteStatePath);
        var beforeBacklog = SnapshotDatabase(seed.Workspace.BacklogStorePath);
        using var full = JsonDocument.Parse(Read(seed, "--json"));
        var output = Read(seed, "--summary", "--json");
        using var document = AssertSummary(output);
        var payload = document.RootElement.GetProperty("payload");
        AssertParity(full.RootElement.GetProperty("payload"), payload);
        Assert.Equal(owner, payload.GetProperty("owner").GetString());
        Assert.Equal(version, payload.GetProperty("claimVersion").Deserialize<long?>());
        Assert.Equal(coverage, payload.GetProperty("coverage").GetString());
        Assert.Equal(ambiguous, payload.GetProperty("ownerAmbiguous").GetBoolean());
        Assert.Equal(ambiguous ? "legacy-owner-ambiguous" : null,
            payload.GetProperty("claimAmbiguityReason").GetString());
        Assert.Equal(ambiguous ? 2 : 0, payload.GetProperty("ambiguousLinkedGoalCount").GetInt32());
        Assert.Equal(linkedCount, payload.GetProperty("linkedGoalCount").GetInt32());
        Assert.Equal(0, payload.GetProperty("noteCount").GetInt32());
        AssertOmissions(output, payload);
        var human = Read(seed, "--summary");
        Assert.Contains($"Owner:   {(ambiguous ? "legacy-owner-ambiguous" : owner ?? "-")}", human);
        Assert.Contains($"Version: {version?.ToString(CultureInfo.InvariantCulture) ?? "-"}", human);
        Assert.Contains($"Coverage: {coverage ?? "-"}", human);
        Assert.Contains("Omitted:", human);
        if (ambiguous) Assert.DoesNotContain(OwnerId, human);
        Assert.Equal(beforeState, SnapshotDatabase(seed.Workspace.SqliteStatePath));
        Assert.Equal(beforeBacklog, SnapshotDatabase(seed.Workspace.BacklogStorePath));
    }

    [Theory]
    [InlineData("--SUMMARY", "--JSON")]
    [InlineData("--summary", "--json", "--summary", "--json")]
    public async Task Flags_CaseAndDuplicatesRetainExistingParserSemantics(params string[] flags)
    {
        var seed = await SeedAsync("unclaimed");
        using var summary = AssertSummary(Read(seed, flags));
        Assert.Equal(seed.Item.Id, summary.RootElement.GetProperty("payload").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lookup_SummaryRetainsMissingAndAmbiguousPrefixErrors(bool ambiguous)
    {
        var seed = await SeedAsync("unclaimed");
        var store = new BacklogStore(seed.Workspace.BacklogStorePath);
        var first = await store.AddAsync("Collision one");
        var second = await store.AddAsync("Collision two");
        ExecuteSql(seed.Workspace.BacklogStorePath, $"""
            UPDATE backlog SET id = 'dddddddd000000000000000000000001' WHERE id = '{first.Id}';
            UPDATE backlog SET id = 'dddddddd000000000000000000000002' WHERE id = '{second.Id}';
            """);
        var prefix = ambiguous ? "dddddddd" : "missing";
        var baseline = await RunCliResultAsync(["backlog-show", prefix]);
        Assert.NotEqual(0, baseline.ExitCode);
        Assert.NotEmpty(baseline.StandardError);
        foreach (var flags in new[] { new[] { "--summary" }, new[] { "--json", "--summary" } })
        {
            var result = await RunCliResultAsync(["backlog-show", prefix, .. flags]);
            Assert.Equal(baseline.ExitCode, result.ExitCode);
            Assert.Equal(baseline.StandardError, result.StandardError);
            Assert.Equal(baseline.StandardOutput, result.StandardOutput);
        }
    }

    [Fact]
    public async Task Flags_InvalidFlagsFailAndHelpDescribesBothEnvelopes()
    {
        var seed = await SeedAsync("unclaimed");
        var result = await RunCliResultAsync(["backlog-show", seed.Item.Id, "--summary", "--unknown"]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--unknown", result.StandardError);
        Assert.DoesNotContain("\"schema\"", result.StandardOutput);
        var help = await RunCliAsync(["backlog-show", "--summary", "--json", "--help"]);
        Assert.Contains("[--summary] [--json]", help);
        Assert.Contains("--json emits the mcg.backlog-item agent envelope.", help);
        Assert.Contains("mcg.backlog-item-summary version 1", help);
        Assert.DoesNotContain("\"schema\"", help);
    }

    private sealed record Seed(OrchestratorWorkspace Workspace, AgentOrchestratorKernel Kernel, BacklogItem Item);

    private async Task<Seed> SeedAsync(string mode, bool rich = false)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Complete summary title " + new string('T', 400),
            BodyMarker + new string('B', 8000), rich ? "source-goal-identity" : null);
        if (rich)
        {
            for (var index = 0; index < 40; index++) await store.AppendNoteAsync(item.Id, $"{NoteMarker}-{index}");
            var prerequisite = await store.AddAsync("Prerequisite");
            var dependent = await store.AddAsync("Dependent");
            await store.AddDependencyAsync(item.Id, new(prerequisite.Id, BacklogDependencyTargetKind.Backlog));
            await store.AddDependencyAsync(dependent.Id, new(item.Id, BacklogDependencyTargetKind.Backlog));
        }
        var kernel = new AgentOrchestratorKernel();
        var count = rich ? 12 : mode is "unclaimed" ? 0 : mode is "legacy" ? 1 : 2;
        for (var index = 0; index < count; index++)
        {
            var goal = kernel.CreateGoal(new GoalId(index == 0 ? OwnerId : $"bbbb{index:D4}" + new string('0', 24)), "Linked history");
            kernel.SetGoalSourceBacklogItemLink(goal.Id, item.Id,
                mode == "slice" ? SourceBacklogCoverage.Slice : SourceBacklogCoverage.Full);
        }
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        if (mode is "persisted" or "slice" or "missing-owner")
        {
            var claims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
            await repository.TransactAsync((current, _) => Task.FromResult((ShouldSave: false,
                Result: claims.EnsureClaimForNewGoal(new AgentOrchestratorKernel(), item.Id, OwnerId,
                    mode == "slice" ? SourceBacklogCoverage.Slice : SourceBacklogCoverage.Full))));
            ExecuteSql(workspace.SqliteStatePath,
                $"UPDATE source_backlog_claims SET version = 7 WHERE backlog_item_id = '{item.Id}'");
        }
        if (rich)
            ExecuteSql(workspace.SqliteStatePath, $$"""
                INSERT INTO goal_replacement_lineage
                    (predecessor_goal_id, successor_goal_id, backlog_item_id, request_id, replaced_at)
                VALUES ('historical-predecessor', '{{OwnerId}}', '{{item.Id}}',
                    'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '2026-09-01T00:00:00.0000000+00:00');
                INSERT INTO state_outbox (id, kind, payload_json, created_at)
                VALUES ('summary-pending-outbox', '{{GoalOperationJournal.AcceptanceRetryAuditOutboxKind}}',
                    '{}', '2026-09-01T00:00:00.0000000+00:00');
                """);
        if (mode == "missing-owner")
        {
            ExecuteSql(workspace.SqliteStatePath, $"DELETE FROM goals WHERE id = '{OwnerId}'");
            kernel = await repository.LoadAsync();
        }
        return new(workspace, kernel, (await store.GetByExactIdAsync(item.Id))!);
    }

    private static void AssertParity(JsonElement full, JsonElement summary)
    {
        foreach (var property in new[] { "id", "title", "status", "createdAt", "updatedAt", "sourceGoalId",
            "priority", "tags", "supersededBy", "owner", "ownerAmbiguous", "claimVersion", "coverage" })
            Assert.Equal(full.GetProperty(property).GetRawText(), summary.GetProperty(property).GetRawText());
        foreach (var (collection, count) in new[] { ("linkedGoals", "linkedGoalCount"), ("lineage", "lineageCount"),
            ("dependencies", "dependencyCount"), ("dependents", "dependentCount"), ("notes", "noteCount"),
            ("ambiguousLinkedGoalIds", "ambiguousLinkedGoalCount") })
            Assert.Equal(full.GetProperty(collection).GetArrayLength(), summary.GetProperty(count).GetInt32());
    }

    private static void AssertOmissions(string output, JsonElement payload)
    {
        Assert.Equal(Omitted, payload.GetProperty("omittedSections").EnumerateArray().Select(value => value.GetString()));
        foreach (var property in Omitted) Assert.False(payload.TryGetProperty(property, out _));
        Assert.DoesNotContain(BodyMarker, output);
        Assert.DoesNotContain(NoteMarker, output);
        Assert.DoesNotContain("bbbb0001", output);
        Assert.DoesNotContain("historical-predecessor", output);
    }

    private static JsonDocument AssertSummary(string output)
    {
        var document = JsonDocument.Parse(output);
        Assert.Equal("mcg.backlog-item-summary", document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "payload" },
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.EndsWith("Z", document.RootElement.GetProperty("generatedAt").GetString());
        return document;
    }

    private static string Read(Seed seed, params string[] flags) => AsyncLocalConsoleRouter.Capture(() =>
    {
        string[] args = ["backlog-show", seed.Item.Id, .. flags];
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        Assert.True(CliReadOnlyCommandRunner.TryExecute(args,
            new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath), seed.Workspace,
            new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref current, out var changed));
        Assert.False(changed);
    });

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-backlog-summary-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task<string> RunCliAsync(string[] args)
    {
        var result = await RunCliResultAsync(args);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.StandardError);
        Assert.NotEmpty(result.StandardOutput);
        return result.StandardOutput;
    }

    private Task<CliChildProcessResult> RunCliResultAsync(string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE",
            "LOCALAPPDATA", "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "DOTNET_ROOT(x86)" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
        return CliChildProcessRunner.RunAsync(start);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());

    private static void ExecuteSql(string path, string sql)
    {
        using var connection = Open(path, SqliteOpenMode.ReadWrite);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Assert.NotEqual(0, command.ExecuteNonQuery());
    }

    private static string SnapshotDatabase(string path)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        connection.Open();
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name";
        var names = new List<string>();
        using (var reader = tables.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
        Assert.NotEmpty(names);
        var snapshot = new List<string>();
        foreach (var name in names)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(JsonSerializer.Serialize(values));
            }
            snapshot.Add(name + ":" + JsonSerializer.Serialize(rows.Order(StringComparer.Ordinal)));
        }
        return string.Join("\n", snapshot);
    }

    private static string[] SnapshotArtifacts(OrchestratorWorkspace workspace) =>
        Directory.GetFiles(workspace.OrchestratorDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(workspace.OrchestratorDirectory, path)
                + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    public void Dispose() => Directory.Delete(root, recursive: true);
}
