using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns its temp stores and uses AsyncLocal console capture.
public sealed class CliGoalsEpicJsonTests : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = CreateTempDirectory();
    private static readonly string[] MemberIds =
        ["abc10000aaaaaaaaaaaaaaaaaaaaaaaa", "abc20000bbbbbbbbbbbbbbbbbbbbbbbb"];

    [Theory]
    [InlineData("goals", "--epic", "Board", "--json")]
    [InlineData("goals", "--epic=Board", "--json")]
    [InlineData("GOALS", "--EPIC", "Board", "--JSON")]
    [InlineData("GoAlS", "--EpIc=Board", "--JsOn")]
    public void JsonForms_QueryRoute_PrintsOnlyMemberEnvelope(params string[] args)
    {
        var workspace = Seed();
        var probe = NewReadProbe();
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));

        var output = ExecuteRead(args, workspace, probe);

        using var document = AssertEnvelope(output);
        var rows = document.RootElement.GetProperty("payload").GetProperty("goals")
            .EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(MemberIds.OrderBy(id => id), rows.Select(row => row.GetProperty("id").GetString()).OrderBy(id => id));
        foreach (var row in rows)
        {
            Assert.Equal(new[] { "id", "status", "condition", "objective" },
                row.EnumerateObject().Select(property => property.Name));
            Assert.Equal("Draft", row.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("condition").ValueKind);
            Assert.Equal(row.GetProperty("id").GetString() == MemberIds[0] ? "First member" : "Second member",
                row.GetProperty("objective").GetString());
        }
        Assert.DoesNotContain("Unrelated goal", output);
        AssertNoRepositoryAccess(probe);
    }

    [Theory]
    [InlineData("goals", "--epic", "Board", "--json")]
    [InlineData("goals", "--epic=Board", "--json")]
    public void JsonForms_ReadAndPersistentRoutes_MatchStoredMemberSerialization(params string[] args)
    {
        var workspace = Seed();
        var readProbe = NewReadProbe();
        var writerProbe = new ProbeStateRepository(new AgentOrchestratorKernel());
        var readOutput = ExecuteRead(args, workspace, readProbe);
        var writerOutput = ExecutePersistent(args, workspace, writerProbe);

        using var read = AssertEnvelope(readOutput);
        using var writer = AssertEnvelope(writerOutput);
        Assert.Equal(NormalizeTimestamp(readOutput, read.RootElement),
            NormalizeTimestamp(writerOutput, writer.RootElement));
        using var expected = JsonDocument.Parse(GoalsAgentReadView.Serialize(ReadMembers(workspace)));
        Assert.Equal(expected.RootElement.GetProperty("payload").GetProperty("goals").GetRawText(),
            read.RootElement.GetProperty("payload").GetProperty("goals").GetRawText());
        AssertNoRepositoryAccess(readProbe);
        Assert.True(writerProbe.ListOutboxMessagesCount > 0);
        Assert.Contains("cli:goals", writerProbe.ObservedWriteOperationTags);
    }

    [Theory]
    [InlineData("goals", "--epic", "--json")]
    [InlineData("goals", "--json", "--epic", "Board")]
    [InlineData("goals", "--epic", "Board", "--json", "extra")]
    [InlineData("goals", "--epic", "Board", "--json", "--json")]
    [InlineData("goals", "--epic=--json")]
    [InlineData("goals", "--epic", "Board", "extra", "--json")]
    [InlineData("goals", "--epic=Board", "--json", "extra")]
    [InlineData("goals", "--epic=Board", "--json", "--json")]
    [InlineData("goals", "--epic", " ", "--json")]
    [InlineData("goals", "--epic=", "--json")]
    [InlineData("goals", "--epic=--json", "--json")]
    [InlineData("goals", "--epic", "Board", "--help", "--json")]
    public void InvalidForms_QueryRoute_DeclinesWithoutRepositoryAccess(params string[] args)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var probe = NewReadProbe();
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Empty(ExecuteRead(args, workspace, probe, expectedHandled: false));
        AssertNoRepositoryAccess(probe);
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    [InlineData("ambiguous", false)]
    [InlineData("ambiguous", true)]
    [InlineData("unversioned", false)]
    [InlineData("unversioned", true)]
    public void EpicErrors_JsonAndHumanForms_KeepExceptionsAndEmptyOutput(string scenario, bool inline)
    {
        var workspace = Seed();
        var reference = scenario == "unknown" ? "missing" : "Board";
        if (scenario == "ambiguous")
            new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Board").GetAwaiter().GetResult();
        if (scenario == "unversioned")
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = workspace.PortfolioStorePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE store_schema_versions";
            command.ExecuteNonQuery();
        }
        string[] humanArgs = inline ? ["goals", $"--epic={reference}"] : ["goals", "--epic", reference];
        string[] jsonArgs = [.. humanArgs, "--json"];
        string? expectedMessage = null;
        foreach (var persistent in new[] { false, true })
        foreach (var args in new[] { humanArgs, jsonArgs })
        {
            var probe = persistent ? new ProbeStateRepository(new AgentOrchestratorKernel()) : NewReadProbe();
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var output = CaptureConsole(() =>
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                {
                    if (persistent) InvokePersistent(args, workspace, probe);
                    else InvokeRead(args, workspace, probe);
                });
                expectedMessage ??= error.Message;
                Assert.Equal(expectedMessage, error.Message);
                Assert.Contains(scenario switch
                {
                    "unknown" => "was not found", "ambiguous" => "is ambiguous", _ => "run setup"
                }, error.Message);
            });
            Assert.Empty(output);
            if (!persistent) AssertNoRepositoryAccess(probe);
            else Assert.True(probe.ListOutboxMessagesCount > 0);
        }
    }

    [Theory]
    [InlineData("goals", "--epic", "Board")]
    [InlineData("goals", "--epic=Board")]
    public void FormatFlag_BothRoutes_PreserveHumanRenderingAndEmitJson(params string[] args)
    {
        var workspace = Seed();
        var expected = CaptureConsole(() => ConsoleViews.PrintGoals(ReadMembers(workspace)));
        Assert.NotEmpty(expected);
        Assert.Equal(expected, ExecuteRead(args, workspace, NewReadProbe()));
        Assert.Equal(expected, ExecutePersistent(args, workspace, new ProbeStateRepository(new AgentOrchestratorKernel())));
        Assert.DoesNotContain("\"schema\"", expected);
        using var readJson = AssertEnvelope(ExecuteRead([.. args, "--json"], workspace, NewReadProbe()));
        using var writerJson = AssertEnvelope(ExecutePersistent([.. args, "--json"], workspace,
            new ProbeStateRepository(new AgentOrchestratorKernel())));
    }

    private static JsonDocument AssertEnvelope(string output)
    {
        var document = JsonDocument.Parse(output);
        var envelope = document.RootElement;
        Assert.Equal(envelope.GetRawText() + Environment.NewLine, output);
        Assert.DoesNotContain("\n", envelope.GetRawText());
        Assert.DoesNotContain("\r", envelope.GetRawText());
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "payload" },
            envelope.EnumerateObject().Select(property => property.Name));
        Assert.Equal("mcg.goals", envelope.GetProperty("schema").GetString());
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        var timestamp = envelope.GetProperty("generatedAt").GetString()!;
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture).Offset);
        Assert.EndsWith("Z", timestamp);
        Assert.Equal(new[] { "goals" }, envelope.GetProperty("payload").EnumerateObject().Select(property => property.Name));
        return document;
    }

    private static string NormalizeTimestamp(string output, JsonElement envelope)
    {
        var timestampProperty = "\"generatedAt\":" + envelope.GetProperty("generatedAt").GetRawText();
        return output.Replace(timestampProperty, "\"generatedAt\":\"<timestamp>\"", StringComparison.Ordinal);
    }

    private OrchestratorWorkspace Seed()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = store.AddEpicAsync("Board").GetAwaiter().GetResult();
        foreach (var id in MemberIds)
            store.AssignGoalToEpicAsync(id, epic.Id).GetAwaiter().GetResult();
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId(MemberIds[0]), "First member");
        kernel.CreateGoal(new GoalId(MemberIds[1]), "Second member");
        kernel.CreateGoal(new GoalId("eeeeeeee111111111111111111111111"), "Unrelated goal");
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        return workspace;
    }

    private static GoalSummary[] ReadMembers(OrchestratorWorkspace workspace) =>
        SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
            .ListGoalMetadataAsync().GetAwaiter().GetResult().Where(goal => MemberIds.Contains(goal.Id)).ToArray();

    private static ProbeStateRepository NewReadProbe() => new(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };

    private static string ExecuteRead(string[] args, OrchestratorWorkspace workspace,
        ProbeStateRepository probe, bool expectedHandled = true) =>
        CaptureConsole(() => InvokeRead(args, workspace, probe, expectedHandled));

    private static void InvokeRead(string[] args, OrchestratorWorkspace workspace,
        ProbeStateRepository probe, bool expectedHandled = true)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        var handled = CliReadOnlyCommandRunner.TryExecute(args, probe, workspace,
            new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref current, out var changed);
        Assert.Equal(expectedHandled, handled);
        Assert.False(changed);
    }

    private static string ExecutePersistent(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository probe) =>
        CaptureConsole(() => InvokePersistent(args, workspace, probe));

    private static void InvokePersistent(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository probe)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, probe, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles,
            ref current, skipReadOnlyRoute: true));
    }

    private static void AssertNoRepositoryAccess(ProbeStateRepository probe)
    {
        Assert.Empty(probe.LoadedGoalIds);
        Assert.Empty(probe.ObservedWriteOperationTags);
        Assert.Equal(0, probe.FullLoadAttempts);
        Assert.Equal(0, probe.LoadGoalsCount);
        Assert.Equal(0, probe.LoadGoalCount);
        Assert.Equal(0, probe.ListGoalMetadataCount);
        Assert.Equal(0, probe.MutationAttempts);
        Assert.Equal(0, probe.SaveAttempts);
        Assert.Equal(0, probe.MergeSaveAttempts);
        Assert.Equal(0, probe.ListOutboxMessagesCount);
        Assert.Equal(0, probe.OutboxClaimAttempts);
        Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
