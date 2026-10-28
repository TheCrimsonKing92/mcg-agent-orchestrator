using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns its stores and CLI children with an allow-listed environment.
public sealed class BacklogRecentOrderingTests
{
    private static string Id(int value) => value.ToString("x8") + new string('0', 24);
    private static readonly string OwnerId = Id(100);

    [Theory]
    [InlineData(1, new[] { 6 })]
    [InlineData(2, new[] { 6, 3 })]
    [InlineData(3, new[] { 6, 3, 4 })]
    [InlineData(4, new[] { 6, 3, 4, 5 })]
    public async Task NewestFirst_MoreMatchesThanLimit_SelectsRecentWithAscendingTies(
        int limit, int[] expected)
    {
        using var fixture = await Fixture.CreateAsync();
        await AssertBothAsync(fixture, expected,
            "--newest-first", "--status", "Open", "--text", "NEEDLE",
            "--limit", limit.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("claimed", new[] { 6, 1 })]
    [InlineData("closed", new[] { 7, 2 })]
    [InlineData("Open", new[] { 6, 3, 4, 5, 1 })]
    public async Task NewestFirst_ResolvedAndStoredStatus_PreservesFilteredMembership(
        string status, int[] expected)
    {
        using var fixture = await Fixture.CreateAsync();
        await AssertBothAsync(fixture, expected,
            "--newest-first", "--status", status, "--text", "needle");
        await AssertBothAsync(fixture, [expected[0]],
            "--newest-first", "--status", status, "--text", "needle", "--limit", "1");
    }

    [Fact]
    public async Task NewestFirst_AllFlag_PreservesMembershipAndStatusPrecedence()
    {
        using var fixture = await Fixture.CreateAsync();
        await AssertBothAsync(fixture, [8, 7, 6, 3, 4, 5, 2, 1], "--newest-first");
        await AssertBothAsync(fixture, [8, 7, 6, 3, 4, 5, 2, 1], "--all", "--newest-first");
        await AssertBothAsync(fixture, [6, 3, 4],
            "--all", "--newest-first", "--status", "open", "--text", "needle", "--limit", "3");
        await AssertBothAsync(fixture, [6], "--NEWEST-FIRST", "--text=--needle", "--limit", "1");
    }

    [Fact]
    public async Task DefaultOrder_LimitAndEmptyCases_PreserveExistingSelection()
    {
        using var fixture = await Fixture.CreateAsync();
        // Default order remains the store's created_at/id order, including offset text order.
        await AssertBothAsync(fixture, [1, 2, 4, 3, 5, 6, 7, 8]);
        await AssertBothAsync(fixture, [1, 2, 4], "--limit", "3");
        await AssertBothAsync(fixture, [1, 2, 4, 3, 5, 6, 7, 8], "--limit", "99");
        await AssertBothAsync(fixture, [], "--newest-first", "--limit", "0");
        await AssertBothAsync(fixture, [], "--newest-first", "--text", "absent");
        await AssertBothAsync(fixture, [], "--newest-first", "--status", "unknown");
    }

    [Fact]
    public async Task OmittedLimit_OverTwoHundredItems_ReturnsEveryMatch()
    {
        using var fixture = await Fixture.CreateAsync();
        var store = new BacklogStore(fixture.Workspace.BacklogStorePath);
        var timestamp = DateTimeOffset.Parse("2026-07-01T00:00:00Z", CultureInfo.InvariantCulture);
        for (var index = 200; index < 401; index++)
            Assert.True(await store.UpsertAsync(new(Id(index), "Bulk", "bulk", BacklogItemStatus.Open,
                timestamp, timestamp, null)));
        await AssertBothAsync(fixture, Enumerable.Range(200, 201).ToArray(), "--text", "bulk");
        await AssertBothAsync(fixture, Enumerable.Range(200, 201).ToArray(),
            "--text", "bulk", "--newest-first");
    }

    [Theory]
    [InlineData("--limit", "-1", "--limit requires a non-negative integer value.")]
    [InlineData("--limit", "invalid", "--limit requires a non-negative integer value.")]
    [InlineData("--unknown", "", "Usage: backlog-list")]
    public async Task NewestFirst_InvalidArguments_RetainsExistingErrors(
        string flag, string value, string expectedError)
    {
        using var fixture = await Fixture.CreateAsync();
        var args = value.Length == 0 ? new[] { flag } : new[] { flag, value };
        var baseline = await fixture.RunAsync(args);
        var newest = await fixture.RunAsync([.. args, "--newest-first"]);
        Assert.NotEqual(0, baseline.ExitCode);
        Assert.Equal(baseline.ExitCode, newest.ExitCode);
        Assert.Equal(baseline.StandardOutput, newest.StandardOutput);
        Assert.Equal(baseline.StandardError, newest.StandardError);
        Assert.Contains(expectedError, baseline.StandardOutput + baseline.StandardError);
    }

    [Fact]
    public async Task NewestFirst_FullEnvelopeAndReadOnlyState_PreserveStoredAuthority()
    {
        using var fixture = await Fixture.CreateAsync();
        var before = await fixture.SnapshotAsync();
        var result = await fixture.RunAsync(["--newest-first", "--text", "needle", "--json"]);
        AssertSuccess(result);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var envelope = document.RootElement;
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "payload" },
            envelope.EnumerateObject().Select(property => property.Name));
        Assert.Equal("mcg.backlog-list", envelope.GetProperty("schema").GetString());
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.EndsWith("Z", envelope.GetProperty("generatedAt").GetString()!);
        Assert.Equal(new[] { "items" }, envelope.GetProperty("payload").EnumerateObject().Select(p => p.Name));
        var rows = envelope.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { 7, 6, 3, 4, 5, 2, 1 }.Select(Id),
            rows.Select(row => row.GetProperty("id").GetString()));
        foreach (var row in rows)
        {
            Assert.Equal(new[] { "id", "status", "goalId", "ownerAmbiguous", "eligible", "title",
                "createdAt", "updatedAt", "priority", "tags", "supersededBy" },
                row.EnumerateObject().Select(property => property.Name));
            Assert.False(row.GetProperty("ownerAmbiguous").GetBoolean());
            Assert.EndsWith("Z", row.GetProperty("createdAt").GetString()!);
            Assert.EndsWith("Z", row.GetProperty("updatedAt").GetString()!);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("supersededBy").ValueKind);
        }
        var recent = Assert.Single(rows, row => row.GetProperty("id").GetString() == Id(6));
        Assert.Equal("claimed", recent.GetProperty("status").GetString());
        Assert.Equal(OwnerId, recent.GetProperty("goalId").GetString());
        Assert.Equal("--needle recent", recent.GetProperty("title").GetString());
        Assert.Equal("P2", recent.GetProperty("priority").GetString());
        Assert.Equal("recent,fixture", recent.GetProperty("tags").GetString());
        Assert.True(recent.GetProperty("eligible").GetBoolean());
        var dependent = Assert.Single(rows, row => row.GetProperty("id").GetString() == Id(5));
        Assert.Equal(JsonValueKind.Null, dependent.GetProperty("goalId").ValueKind);
        Assert.Equal(JsonValueKind.Null, dependent.GetProperty("priority").ValueKind);
        Assert.False(dependent.GetProperty("eligible").GetBoolean());
        await AssertBothAsync(fixture, [6, 3],
            "--newest-first", "--status", "open", "--text", "needle", "--limit", "2");
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    private static async Task AssertBothAsync(Fixture fixture, int[] expected, params string[] args)
    {
        var human = await fixture.RunAsync(args);
        var json = await fixture.RunAsync([.. args, "--json"]);
        AssertSuccess(human);
        AssertSuccess(json);
        var humanIds = Regex.Matches(human.StandardOutput, @"\b([0-9a-f]{32}) \| status=")
            .Select(match => match.Groups[1].Value).ToArray();
        using var document = JsonDocument.Parse(json.StandardOutput);
        var jsonIds = document.RootElement.GetProperty("payload").GetProperty("items")
            .EnumerateArray().Select(row => row.GetProperty("id").GetString()).ToArray();
        Assert.Equal(expected.Select(Id), humanIds);
        Assert.Equal(humanIds, jsonIds);
        Assert.Contains($"Backlog list: {expected.Length} item(s) from backlog store", human.StandardOutput);
    }

    private static void AssertSuccess(CliChildProcessResult result)
    {
        Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}; stderr={result.StandardError}; stdout={result.StandardOutput}");
        Assert.Equal(string.Empty, result.StandardError);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "backlog-recent-" + Guid.NewGuid().ToString("N"));
        internal OrchestratorWorkspace Workspace { get; }

        private Fixture()
        {
            Directory.CreateDirectory(root);
            Workspace = OrchestratorWorkspace.ForDirectory(root);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                StateDbMigrations.EnsureUpToDate(fixture.Workspace.SqliteStatePath);
                var store = new BacklogStore(fixture.Workspace.BacklogStorePath);
                // Shuffled insertion, offsets, update times and priorities distinguish competing sorts.
                foreach (var index in new[] { 5, 8, 3, 7, 1, 6, 4, 2 })
                {
                    var created = DateTimeOffset.Parse(index switch
                    {
                        1 => "2026-08-01T00:00:00Z", 2 => "2026-08-02T00:00:00Z",
                        3 => "2026-10-27T12:00:00Z", 4 => "2026-10-27T07:00:00-05:00",
                        5 => "2026-10-27T14:00:00+02:00", 6 => "2026-10-28T00:00:00Z",
                        7 => "2026-10-29T00:00:00Z", _ => "2026-10-30T00:00:00Z"
                    }, CultureInfo.InvariantCulture);
                    var title = index switch { 4 => "Body match", 6 => "--needle recent", 8 => "Noise", _ => "Needle " + index };
                    Assert.True(await store.UpsertAsync(new(Id(index), title, index == 8 ? "unmatched" : "needle body",
                        index is 2 or 7 ? BacklogItemStatus.Done : BacklogItemStatus.Open,
                        created, created.AddDays(100 - index * 10), null,
                        index == 1 ? "P0" : index == 6 ? "P2" : null,
                        index == 6 ? "recent,fixture" : "")));
                }
                await store.AppendNoteAsync(Id(1), "Persisted note and history fixture");
                await store.AddDependencyAsync(Id(5), new(Id(1), BacklogDependencyTargetKind.Backlog));
                await store.LinkAsync(Id(3), Id(4), BacklogLinkKind.Related);
                var kernel = new AgentOrchestratorKernel();
                foreach (var (item, owner) in new[] { (6, OwnerId), (1, Id(101)) })
                {
                    var goal = kernel.CreateGoal(new GoalId(owner), "Claim owner " + item);
                    kernel.SetGoalSourceBacklogItemId(goal.Id, Id(item));
                }
                var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
                await repository.SaveAsync(kernel);
                var claims = new SourceBacklogClaimStore(fixture.Workspace.SqliteStatePath);
                foreach (var item in new[] { 1, 6 })
                {
                    await repository.TransactAsync((persisted, _) =>
                        Task.FromResult((ShouldSave: false, Result: claims.ResolveOrMaterializeClaim(persisted, Id(item)))));
                    Assert.NotNull(claims.ResolveClaim(new AgentOrchestratorKernel(), Id(item)));
                }
                await using var connection = Open(fixture.Workspace.SqliteStatePath, readOnly: false);
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ('recent-fixture', $kind, '{}', '2026-10-01T00:00:00Z')";
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        internal Task<CliChildProcessResult> RunAsync(string[] args)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
            start.ArgumentList.Add("backlog-list");
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            start.Environment.Clear();
            foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE",
                "LOCALAPPDATA", "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "DOTNET_ROOT(x86)" })
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    start.Environment[name] = value;
            start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
            return CliChildProcessRunner.RunAsync(start);
        }

        internal async Task<string> SnapshotAsync()
        {
            var snapshot = new List<string>();
            foreach (var (path, tables) in new[]
            {
                (Workspace.BacklogStorePath, new[] { "backlog", "backlog_notes", "backlog_links", "backlog_dependencies", "backlog_history" }),
                (Workspace.SqliteStatePath, new[] { "goals", "source_backlog_claims", "state_outbox" })
            })
            {
                await using var connection = Open(path, readOnly: true);
                await connection.OpenAsync();
                foreach (var table in tables)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT * FROM {table} ORDER BY 1, 2";
                    await using var reader = await command.ExecuteReaderAsync();
                    var rows = new List<string?[]>();
                    while (await reader.ReadAsync())
                        rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index)
                            ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)).ToArray());
                    Assert.NotEmpty(rows);
                    snapshot.Add(table + ":" + JsonSerializer.Serialize(rows));
                }
            }
            return string.Join("\n", snapshot);
        }

        private static SqliteConnection Open(string path, bool readOnly) => new(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
