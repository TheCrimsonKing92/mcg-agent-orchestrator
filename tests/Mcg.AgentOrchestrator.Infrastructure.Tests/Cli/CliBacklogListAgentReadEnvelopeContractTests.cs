using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its stores and uses AsyncLocal console capture.
public sealed class CliBacklogListAgentReadEnvelopeContractTests : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = CreateTempDirectory();

    [Theory]
    [InlineData("backlog-list", "--json")]
    [InlineData("BACKLOG-LIST", "--JSON")]
    public async Task JsonFlag_QueryRoute_EmitsResolvedRowsWithoutWriterAccess(params string[] args)
    {
        var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
        var probe = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));

        using var document = AssertEnvelope(ExecuteRead(args, seed.Workspace, probe));
        var rows = Items(document).EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        foreach (var row in rows)
        {
            Assert.Equal(new[] { "id", "status", "goalId", "ownerAmbiguous", "eligible", "title",
                    "createdAt", "updatedAt", "priority", "tags", "supersededBy" },
                row.EnumerateObject().Select(property => property.Name));
            Assert.False(row.GetProperty("ownerAmbiguous").GetBoolean());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("priority").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("supersededBy").ValueKind);
            Assert.Equal("", row.GetProperty("tags").GetString());
            Assert.EndsWith("Z", row.GetProperty("createdAt").GetString()!);
            Assert.EndsWith("Z", row.GetProperty("updatedAt").GetString()!);
        }
        var prerequisite = Assert.Single(rows, row => row.GetProperty("id").GetString() == seed.Prerequisite.Id);
        Assert.Equal("claimed", prerequisite.GetProperty("status").GetString());
        Assert.Equal(CliBacklogReadOnlyRouteTests.LinkedGoalId, prerequisite.GetProperty("goalId").GetString());
        Assert.True(prerequisite.GetProperty("eligible").GetBoolean());
        Assert.Equal("Quartz query prerequisite", prerequisite.GetProperty("title").GetString());
        var dependent = Assert.Single(rows, row => row.GetProperty("id").GetString() == seed.Dependent.Id);
        Assert.Equal("open", dependent.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, dependent.GetProperty("goalId").ValueKind);
        Assert.False(dependent.GetProperty("eligible").GetBoolean());
        Assert.Equal("Quartz query dependent", dependent.GetProperty("title").GetString());
        Assert.Equal(seed.Dependent.CreatedAt, DateTimeOffset.ParseExact(
            dependent.GetProperty("createdAt").GetString()!, "O", CultureInfo.InvariantCulture));
        AssertNoWriterAccess(probe);
    }

    [Fact]
    public async Task JsonFlag_ReadAndPersistentRoutes_MatchExceptGeneratedAt()
    {
        var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
        var probe = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
        var readOutput = ExecuteRead(["backlog-list", "--json"], seed.Workspace, probe);
        var writer = new ProbeStateRepository(seed.Kernel);
        var persistentOutput = ExecutePersistent(["backlog-list", "--json"], seed.Workspace, writer);
        using var read = AssertEnvelope(readOutput);
        using var persistent = AssertEnvelope(persistentOutput);
        Assert.Equal(NormalizeTimestamp(readOutput, read), NormalizeTimestamp(persistentOutput, persistent));
        AssertNoWriterAccess(probe);
        Assert.True(writer.ListOutboxMessagesCount > 0);
    }

    [Theory]
    [InlineData("--status", "claimed", 1)]
    [InlineData("--status", "open", 2)]
    [InlineData("--limit", "1", 1)]
    [InlineData("--text", "quartz", 2)]
    [InlineData("--text", "nomatch", 0)]
    public async Task JsonFlag_Filters_PreserveSelectionAndEmptyEnvelope(string flag, string value, int count)
    {
        var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
        var output = ExecuteRead(["backlog-list", flag, value, "--json"], seed.Workspace,
            new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true });
        using var document = AssertEnvelope(output);
        var rows = Items(document).EnumerateArray().ToArray();
        Assert.Equal(count, rows.Length);
        var expectedIds = count switch
        {
            0 => Array.Empty<string>(),
            1 => [seed.Prerequisite.Id],
            _ => new[] { seed.Prerequisite.Id, seed.Dependent.Id }
        };
        Assert.Equal(expectedIds, rows.Select(row => row.GetProperty("id").GetString()));
        Assert.DoesNotContain("Backlog list:", output);
    }

    [Fact]
    public async Task HumanForm_BothRoutes_PreserveItemLinesAndSummary()
    {
        var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
        var readOutput = ExecuteRead(["backlog-list"], seed.Workspace,
            new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true });
        var persistentOutput = ExecutePersistent(["backlog-list"], seed.Workspace,
            new ProbeStateRepository(seed.Kernel));
        Assert.Equal(readOutput, persistentOutput);
        foreach (var output in new[] { readOutput, persistentOutput })
        {
            Assert.DoesNotContain("schema", output);
            Assert.EndsWith(Environment.NewLine, output);
            var lines = output[..^Environment.NewLine.Length].Split(Environment.NewLine);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith(HumanPrefix(seed.Prerequisite), lines[0]);
            Assert.EndsWith($" {seed.Prerequisite.Id} | status=claimed | goal={CliBacklogReadOnlyRouteTests.LinkedGoalId} | title=Quartz query prerequisite", lines[0]);
            Assert.StartsWith(HumanPrefix(seed.Dependent), lines[1]);
            Assert.EndsWith($" {seed.Dependent.Id} | status=open | goal=- | title=Quartz query dependent", lines[1]);
            Assert.Equal("Backlog list: 2 item(s) from backlog store", lines[2]);
        }
    }

    [Fact]
    public void Serialize_FixedOffsetClock_PreservesUtcNullsAndEmptyRows()
    {
        var instant = new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.FromHours(2));
        var clock = new FixedTimeProvider(instant);
        BacklogListAgentReadView.Row[] rows =
        [
            new("ambiguous", "Ambiguous owner", "open", null, true, false,
                instant, instant.AddHours(1), "", "quartz,query", ""),
            new("closed", "Closed item", "closed", "linked", false, true,
                instant, instant, "P1", "", "replacement")
        ];
        var output = BacklogListAgentReadView.Serialize(rows, clock);
        using var document = JsonDocument.Parse(output);
        Assert.Equal("2026-10-06T00:00:00.0000000Z", document.RootElement.GetProperty("generatedAt").GetString());
        var ambiguous = Items(document)[0];
        Assert.Equal(JsonValueKind.Null, ambiguous.GetProperty("priority").ValueKind);
        Assert.Equal(JsonValueKind.Null, ambiguous.GetProperty("supersededBy").ValueKind);
        Assert.Contains("\"goalId\":null,\"ownerAmbiguous\":true", output);
        Assert.Equal("2026-10-06T00:00:00.0000000Z", ambiguous.GetProperty("createdAt").GetString());
        Assert.Equal("2026-10-06T01:00:00.0000000Z", ambiguous.GetProperty("updatedAt").GetString());
        Assert.Equal("quartz,query", ambiguous.GetProperty("tags").GetString());
        var closed = Items(document)[1];
        Assert.Equal("closed", closed.GetProperty("status").GetString());
        Assert.Equal("linked", closed.GetProperty("goalId").GetString());
        Assert.Equal("P1", closed.GetProperty("priority").GetString());
        Assert.Equal("replacement", closed.GetProperty("supersededBy").GetString());
        var emptyOutput = BacklogListAgentReadView.Serialize([], clock);
        using var empty = JsonDocument.Parse(emptyOutput);
        Assert.Equal(0, Items(empty).GetArrayLength());
        Assert.Contains("\"items\":[]", emptyOutput);
    }

    [Theory]
    [InlineData("backlog-list", "--jsn")]
    [InlineData("backlog-list", "--json", "--frobnitz")]
    public void UnknownFlags_Validation_StillThrowsWithUsage(params string[] args)
    {
        var error = Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(args));
        Assert.Contains(CliCommandHelp.BacklogListUsage, error.Message);
    }

    [Fact]
    public void JsonWithHelp_StartupPath_PrintsHelpWithoutEnvelope()
    {
        var output = CaptureConsole(() =>
            Assert.True(CliCommandHandlers.TryPrintStartupHelp(["backlog-list", "--json", "--help"])));
        Assert.Contains("Usage: backlog-list", output);
        Assert.Contains("[--json]", output);
        Assert.DoesNotContain("\"schema\"", output);
    }

    private static string HumanPrefix(BacklogItem item) =>
        "[Open] [created=" + item.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
        "] [updated=" + item.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "]";

    private static JsonDocument AssertEnvelope(string output)
    {
        var document = JsonDocument.Parse(output);
        var envelope = document.RootElement;
        Assert.Equal(envelope.GetRawText() + Environment.NewLine, output);
        Assert.DoesNotContain("\n", envelope.GetRawText());
        Assert.DoesNotContain("\r", envelope.GetRawText());
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "payload" },
            envelope.EnumerateObject().Select(property => property.Name));
        Assert.Equal("mcg.backlog-list", envelope.GetProperty("schema").GetString());
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        var timestamp = envelope.GetProperty("generatedAt").GetString()!;
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture).Offset);
        Assert.EndsWith("Z", timestamp);
        Assert.Equal(new[] { "items" }, envelope.GetProperty("payload").EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Array, Items(document).ValueKind);
        return document;
    }

    private static JsonElement Items(JsonDocument document) => document.RootElement.GetProperty("payload").GetProperty("items");

    private static string NormalizeTimestamp(string output, JsonDocument document) =>
        output.Replace("\"generatedAt\":" + document.RootElement.GetProperty("generatedAt").GetRawText(),
            "\"generatedAt\":\"<timestamp>\"", StringComparison.Ordinal);

    private static string ExecuteRead(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository probe) =>
        CaptureConsole(() =>
        {
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? current = null;
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, probe, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref current, out var changed));
            Assert.False(changed);
        });

    private static string ExecutePersistent(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository probe) =>
        CaptureConsole(() =>
        {
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? current = null;
            Assert.False(CliPersistentStateRunner.ExecuteCommand(args, probe, workspace, ref agents,
                new InMemoryModelProviderRegistry([]), ref profiles, ref current, skipReadOnlyRoute: true));
        });

    private static void AssertNoWriterAccess(ProbeStateRepository probe)
    {
        Assert.Equal(0, probe.MutationAttempts);
        Assert.Equal(0, probe.SaveAttempts);
        Assert.Equal(0, probe.MergeSaveAttempts);
        Assert.Equal(0, probe.ListOutboxMessagesCount);
        Assert.Equal(0, probe.OutboxClaimAttempts);
        Assert.Equal(0, probe.FullLoadAttempts);
    }

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
