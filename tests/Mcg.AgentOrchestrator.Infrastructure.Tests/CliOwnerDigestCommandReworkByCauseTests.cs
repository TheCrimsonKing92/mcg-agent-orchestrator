using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class CliOwnerDigestCommandReworkByCauseTests
{
    private const string GoalId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static DateTimeOffset Start => OwnerDigestTestFixture.Start;

    [Fact]
    public async Task CommandReportsCauseRoleUsageAndPreservesLegacyRoundSections()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        await AddRoundsAsync(fixture);
        CreateIntents(fixture,
            ("developer", -0.7, "Human", "Applied", "retry"), // after retry, before dispatch
            ("reviewer", 1.6, "Agent", "Applied", "adjudicate"), // after retry, before dispatch
            ("developer", 1.1, "Agent", "Applied", "retry"), // after the deciding dispatch
            ("other", -0.76, "Agent", "Applied", "retry"),
            ("developer", -0.76, "Agent", "Pending", "retry"),
            ("developer", -0.76, "Agent", "Applied", "progress"));
        var intentsPath = Path.Combine(fixture.Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        var intentsHash = SHA256.HashData(File.ReadAllBytes(intentsPath));

        var text = Run(fixture);
        Assert.Equal(0, text.Code);
        var legacyStart = text.Output.IndexOf("Rounds by role", StringComparison.Ordinal);
        var reworkStart = text.Output.IndexOf("Rework rounds by cause", StringComparison.Ordinal);
        Assert.True(legacyStart >= 0 && reworkStart > legacyStart);
        Assert.Equal(LegacyText, text.Output[legacyStart..reworkStart]);
        Assert.Equal(string.Join(Environment.NewLine,
            "Rework rounds by cause | Family | Rounds | Input | Cached input | Output | Usage unreported",
            "Developer | GateRed | 2 | 50 | 10 | 15 | 0",
            "Developer | OperatorRetry | 1 | 10 | 2 | 3 | 0",
            "Developer | Unclassified | 1 | 0 | 0 | 0 | 1",
            "Reviewer | StewardRoute | 1 | 40 | 8 | 12 | 0",
            "Total | all | 5 | 100 | 20 | 30 | 1 | unclassified=1 share=0.2", ""),
            text.Output[reworkStart..]);

        var json = Run(fixture, "--json");
        Assert.Equal(0, json.Code);
        using var document = JsonDocument.Parse(json.Output);
        var rounds = document.RootElement.GetProperty("rounds");
        Assert.Equal(LegacyByRoleJson, rounds.GetProperty("byRole").GetRawText());
        Assert.Equal(LegacyByModelJson, rounds.GetProperty("byModel").GetRawText());
        var rework = rounds.GetProperty("reworkByCause");
        Assert.Equal("""
            [{"role":"Developer","family":"GateRed","rounds":2,"inputTokens":50,"cachedInputTokens":10,"outputTokens":15,"usageUnreported":0},{"role":"Developer","family":"OperatorRetry","rounds":1,"inputTokens":10,"cachedInputTokens":2,"outputTokens":3,"usageUnreported":0},{"role":"Developer","family":"Unclassified","rounds":1,"inputTokens":0,"cachedInputTokens":0,"outputTokens":0,"usageUnreported":1},{"role":"Reviewer","family":"StewardRoute","rounds":1,"inputTokens":40,"cachedInputTokens":8,"outputTokens":12,"usageUnreported":0}]
            """, rework.GetProperty("rows").GetRawText());
        Assert.Equal("""
            {"rounds":5,"unclassified":1,"unclassifiedShare":0.2,"inputTokens":100,"cachedInputTokens":20,"outputTokens":30,"usageUnreported":1}
            """, rework.GetProperty("total").GetRawText());
        Assert.DoesNotContain("FirstPass", rework.GetRawText());
        Assert.Equal(intentsHash, SHA256.HashData(File.ReadAllBytes(intentsPath)));
    }

    [Fact]
    public async Task EmptyReworkHasZeroTotalAndUnknownShare()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        CreateIntents(fixture);
        var text = Run(fixture);
        Assert.Equal(0, text.Code);
        Assert.Contains("Total | all | 0 | 0 | 0 | 0 | 0 | unclassified=0 share=n/a", text.Output);
        var json = Run(fixture, "--json");
        Assert.Equal(0, json.Code);
        using var document = JsonDocument.Parse(json.Output);
        var rework = document.RootElement.GetProperty("rounds").GetProperty("reworkByCause");
        Assert.Empty(rework.GetProperty("rows").EnumerateArray());
        Assert.Equal("""
            {"rounds":0,"unclassified":0,"unclassifiedShare":null,"inputTokens":0,"cachedInputTokens":0,"outputTokens":0,"usageUnreported":0}
            """, rework.GetProperty("total").GetRawText());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-a-database")]
    [InlineData("legacy-schema")]
    public async Task UnavailableIntentStoreIsToleratedWithoutWritingIt(string kind)
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        await AddRoundsAsync(fixture);
        var path = Path.Combine(fixture.Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        if (kind == "missing") File.Delete(path);
        else if (kind == "legacy-schema")
        {
            File.Delete(path);
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE operator_intents (task_id TEXT, completed_at TEXT, status TEXT, verb TEXT)";
            command.ExecuteNonQuery();
        }
        var before = File.Exists(path) ? SHA256.HashData(File.ReadAllBytes(path)) : null;
        var result = Run(fixture, "--json");
        Assert.Equal(0, result.Code);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(3, document.RootElement.GetProperty("rounds").GetProperty("reworkByCause")
            .GetProperty("total").GetProperty("unclassified").GetInt32());
        if (before is null) Assert.False(File.Exists(path));
        else Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task IntentReaderUsesAppliedVerbsUntilBoundaryAndLegacyNullActor()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        CreateIntents(fixture,
            ("human", -1, "Human", "Applied", "retry"),
            ("legacy-human", 1, null, "Applied", "retry"),
            ("steward", 2, "Agent", "Applied", "adjudicate"),
            ("at-until", 24, "Human", "Applied", "retry"),
            ("after-until", 25, "Human", "Applied", "retry"),
            ("pending", 1, "Human", "Pending", "retry"),
            ("other-verb", 1, "Human", "Applied", "answer"),
            ("invalid-actor", 1, "Unrecognized", "Applied", "retry"));
        var path = Path.Combine(fixture.Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO operator_intents VALUES ('malformed', 'invalid-time', 'Human', 'Applied', 'retry')";
            command.ExecuteNonQuery();
        }
        var intents = CliOwnerDigestRetryIntents.Read(fixture.Workspace, OwnerDigestTestFixture.End)
            .OrderBy(i => i.AppliedAt).ToArray();
        Assert.Equal(new AppliedRetryIntent[]
        {
            new("human", Start.AddHours(-1), OperatorActorKind.Human),
            new("legacy-human", Start.AddHours(1), OperatorActorKind.Human),
            new("steward", Start.AddHours(2), OperatorActorKind.Agent)
        }, intents);
    }

    // Derived from the unchanged Rows/WriteRows contract at base 7623e4b2f, not candidate output.
    private static readonly string LegacyText = string.Join(Environment.NewLine,
        "Rounds by role | Rounds | Completed | Failed | Superseded | Other | Input | Cached input | Output | Usage unreported",
        "Developer | 4 | 1 | 0 | 3 | 0 | 60 | 12 | 18 | 1",
        "Reviewer | 2 | 1 | 0 | 1 | 0 | 140 | 28 | 42 | 0",
        "Rounds by model | Rounds | Completed | Failed | Superseded | Other | Input | Cached input | Output | Usage unreported",
        "p/m | 6 | 2 | 0 | 4 | 0 | 200 | 40 | 60 | 1", "");

    private const string LegacyByRoleJson = """
        [{"key":"Developer","rounds":4,"completed":1,"failed":0,"superseded":3,"other":0,"inputTokens":60,"cachedInputTokens":12,"outputTokens":18,"usageUnreported":1},{"key":"Reviewer","rounds":2,"completed":1,"failed":0,"superseded":1,"other":0,"inputTokens":140,"cachedInputTokens":28,"outputTokens":42,"usageUnreported":0}]
        """;
    private const string LegacyByModelJson = """
        [{"key":"p/m","rounds":6,"completed":2,"failed":0,"superseded":4,"other":0,"inputTokens":200,"cachedInputTokens":40,"outputTokens":60,"usageUnreported":1}]
        """;

    private static async Task AddRoundsAsync(OwnerDigestTestFixture fixture)
    {
        TaskDispatchSnapshot Dispatch(double hour, long? input) => new("worker", "command", "root",
            Start.AddHours(hour), "p", "m", ContextPackageReceipt: new WorkerContextPackageReceipt("package", [],
                Value(input), Value(input / 5), Value(input * 3 / 10)));
        ProgressEventSnapshot Event(string task, double hour, ProgressKind kind, string message) =>
            new(GoalId, task, kind, message, Start.AddHours(hour));
        var goal = new GoalSnapshot(GoalId, "Rework report", GoalStatus.Active,
            [new TaskSnapshot("developer", "Develop", AgentRole.Developer, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory:
                [Dispatch(-1, 1000), Dispatch(1, 10), Dispatch(2, 20), Dispatch(3, 30), Dispatch(4, null), Dispatch(24, 9999)]),
             new TaskSnapshot("reviewer", "Review", AgentRole.Reviewer, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory: [Dispatch(0.5, 100), Dispatch(2, 40)])],
            [Event("developer", -0.75, ProgressKind.TaskRetried, "operator free-text retry"),
             Event("developer", 1.5, ProgressKind.TaskRetried, "Acceptance criteria unmet; retrying task with feedback"),
             Event("developer", 2.5, ProgressKind.TaskRetried, "Acceptance criteria unmet; retrying task with feedback"),
             Event("developer", 3.5, ProgressKind.TaskRetried, "unclassified free text"),
             Event("developer", 4.25, ProgressKind.TaskCompleted, "done"),
             Event("developer", 23, ProgressKind.TaskRetried, "ACTIONABLE_CANDIDATE_RED: outside window"),
             Event("developer", 24.5, ProgressKind.TaskCompleted, "done"),
             Event("reviewer", 1.5, ProgressKind.TaskRetried, "steward free-text route"),
             Event("reviewer", 2.25, ProgressKind.TaskCompleted, "done")]);
        await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath).SaveGoalSnapshotsAsync([goal]);
    }

    private static ProviderUsageValue Value(long? value) => value is { } count
        ? ProviderUsageValue.Reported(count) : ProviderUsageValue.Unknown("unreported");

    private static void CreateIntents(OwnerDigestTestFixture fixture,
        params (string Task, double Hour, string? Actor, string Status, string Verb)[] rows)
    {
        var path = Path.Combine(fixture.Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        File.Delete(path); // The fixture deliberately seeded a non-SQLite file.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE operator_intents (task_id TEXT, completed_at TEXT, actor_kind TEXT, status TEXT, verb TEXT)";
        command.ExecuteNonQuery();
        foreach (var row in rows)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO operator_intents VALUES ($task, $at, $actor, $status, $verb)";
            command.Parameters.AddWithValue("$task", row.Task);
            command.Parameters.AddWithValue("$at", Start.AddHours(row.Hour).ToString("O"));
            command.Parameters.AddWithValue("$actor", (object?)row.Actor ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", row.Status);
            command.Parameters.AddWithValue("$verb", row.Verb);
            command.ExecuteNonQuery();
        }
    }

    private static (int Code, string Output) Run(OwnerDigestTestFixture fixture, params string[] flags)
    {
        var args = new[] { "owner-digest", "--rounds", "--since", Start.ToString("O"),
            "--until", OwnerDigestTestFixture.End.ToString("O") }.Concat(flags).ToArray();
        using var output = new StringWriter();
        var code = CliOwnerDigestCommand.Run(args, fixture.Workspace, fixture.Clock, output);
        return (code, output.ToString());
    }
}
