using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its stores and uses AsyncLocal console capture.
public sealed class CliBacklogShowAgentReadEnvelopeContractTests : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = CreateTempDirectory();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonFlag_QueryRoute_EmitsItemWithoutWriterAccess(bool upper)
    {
        var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
        var probe = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
        string[] args = upper
            ? ["BACKLOG-SHOW", seed.Prerequisite.Id[..8].ToUpperInvariant(), "--JSON"]
            : ["backlog-show", seed.Prerequisite.Id[..8], "--json"];
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));

        // Before the change, this exact command rejects --json before producing output.
        using var document = AssertEnvelope(ExecuteRead(args, seed.Workspace, probe));
        var item = document.RootElement.GetProperty("payload");
        Assert.Equal(seed.Prerequisite.Id, item.GetProperty("id").GetString());
        Assert.Equal(CliBacklogReadOnlyRouteTests.LinkedGoalId, item.GetProperty("owner").GetString());
        Assert.False(item.GetProperty("ownerAmbiguous").GetBoolean());
        Assert.Empty(item.GetProperty("ambiguousLinkedGoalIds").EnumerateArray());
        var linked = Assert.Single(item.GetProperty("linkedGoals").EnumerateArray());
        Assert.Equal(CliBacklogReadOnlyRouteTests.LinkedGoalId, linked.GetProperty("goalId").GetString());
        Assert.Equal("authoritative", linked.GetProperty("authority").GetString());
        Assert.Equal(new[] { seed.Dependent.Id }, Strings(item.GetProperty("dependents")));
        Assert.Empty(item.GetProperty("dependencies").EnumerateArray());
        Assert.Equal("Read linked goal claims", item.GetProperty("body").GetString());
        AssertNoWriterAccess(probe);
    }

    [Fact]
    public async Task JsonFlag_ClaimedAndDependentItems_MatchHumanScalars()
    {
        var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
        var probe = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
        foreach (var id in new[] { seed.Prerequisite.Id, seed.Dependent.Id })
        {
            var human = Lines(ExecuteRead(["backlog-show", id], seed.Workspace, probe));
            using var document = AssertEnvelope(ExecuteRead(["backlog-show", id, "--json"], seed.Workspace, probe));
            var item = document.RootElement.GetProperty("payload");
            foreach (var (label, property) in new[]
                { ("Id:", "id"), ("Title:", "title"), ("Status:", "status"), ("Owner:", "owner"), ("Coverage:", "coverage") })
            {
                var value = item.GetProperty(property);
                Assert.Equal(Scalar(human, label), value.ValueKind == JsonValueKind.Null ? "-" : value.GetString());
            }
            var version = item.GetProperty("claimVersion");
            Assert.Equal(Scalar(human, "Version:"), version.ValueKind == JsonValueKind.Null
                ? "-" : version.GetInt64().ToString(CultureInfo.InvariantCulture));
            Assert.Equal(Section(human, "Linked goals:"), item.GetProperty("linkedGoals").EnumerateArray().Select(goal =>
                $"- {goal.GetProperty("goalId").GetString()![..8]} status={goal.GetProperty("status").GetString()} " +
                $"authority={goal.GetProperty("authority").GetString()} landing={goal.GetProperty("landing").GetString()}"));
            Assert.Equal(Section(human, "Dependencies:"), item.GetProperty("dependencies").EnumerateArray().Select(dependency =>
                $"- {dependency.GetProperty("prerequisiteId").GetString()} state={dependency.GetProperty("state").GetString()}"));
            Assert.Equal(Section(human, "Dependents:"), Strings(item.GetProperty("dependents")).Select(dependent => $"- {dependent}"));
            if (id == seed.Dependent.Id)
            {
                var dependency = Assert.Single(item.GetProperty("dependencies").EnumerateArray());
                Assert.Equal("backlog", dependency.GetProperty("targetKind").GetString());
                Assert.Equal(seed.Prerequisite.Id, dependency.GetProperty("prerequisiteId").GetString());
            }
        }
        AssertNoWriterAccess(probe);
    }

    [Fact]
    public async Task JsonFlag_ReadAndPersistentRoutes_MatchExceptGeneratedAt()
    {
        var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
        var probe = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
        string[] args = ["backlog-show", seed.Prerequisite.Id[..8], "--json"];
        var readOutput = ExecuteRead(args, seed.Workspace, probe);
        var persistentOutput = ExecutePersistent(args, seed.Workspace,
            new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath));
        using var read = AssertEnvelope(readOutput);
        using var persistent = AssertEnvelope(persistentOutput);
        Assert.Equal(NormalizeTimestamp(readOutput, read), NormalizeTimestamp(persistentOutput, persistent));
        AssertNoWriterAccess(probe);
    }

    [Fact]
    public void Serialize_FixedOffsetClock_EmitsUtcAndAmbiguousOwnerFacts()
    {
        var instant = new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.FromHours(2));
        const string predecessor = "abc10000aaaaaaaaaaaaaaaaaaaaaaaa";
        const string successor = "abc20000bbbbbbbbbbbbbbbbbbbbbbbb";
        var requestId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var row = new BacklogShowAgentReadView.Row("item", "Ambiguous owner", "Open", instant,
            instant.AddHours(1), predecessor, "P1", "quartz,query", successor, predecessor, true,
            [predecessor, successor], 7, "full",
            [new(predecessor, "Draft", "ambiguous", "Created")],
            [new(predecessor, successor, requestId, instant)],
            [new(successor, "goal", "Draft/Created")], [successor], "Body\nsecond line",
            [new(instant, "Note\nsecond line")]);
        var output = BacklogShowAgentReadView.Serialize(row, new FixedTimeProvider(instant));
        using var document = AssertEnvelope(output + Environment.NewLine);
        Assert.Equal("2026-10-06T00:00:00.0000000Z", document.RootElement.GetProperty("generatedAt").GetString());
        var item = document.RootElement.GetProperty("payload");
        Assert.Equal("2026-10-06T00:00:00.0000000Z", item.GetProperty("createdAt").GetString());
        Assert.Equal("2026-10-06T01:00:00.0000000Z", item.GetProperty("updatedAt").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("owner").ValueKind);
        Assert.True(item.GetProperty("ownerAmbiguous").GetBoolean());
        Assert.Equal(new[] { predecessor, successor }, Strings(item.GetProperty("ambiguousLinkedGoalIds")));
        Assert.Equal(predecessor, item.GetProperty("sourceGoalId").GetString());
        Assert.Equal("P1", item.GetProperty("priority").GetString());
        Assert.Equal("quartz,query", item.GetProperty("tags").GetString());
        Assert.Equal(successor, item.GetProperty("supersededBy").GetString());
        Assert.Equal(7, item.GetProperty("claimVersion").GetInt64());
        Assert.Equal("full", item.GetProperty("coverage").GetString());
        var linked = Assert.Single(item.GetProperty("linkedGoals").EnumerateArray());
        Assert.Equal(new[] { "goalId", "status", "authority", "landing" }, Names(linked));
        Assert.Equal(predecessor, linked.GetProperty("goalId").GetString());
        var lineage = Assert.Single(item.GetProperty("lineage").EnumerateArray());
        Assert.Equal(new[] { "predecessorGoalId", "successorGoalId", "requestId", "replacedAt" }, Names(lineage));
        Assert.Equal(predecessor, lineage.GetProperty("predecessorGoalId").GetString());
        Assert.Equal(successor, lineage.GetProperty("successorGoalId").GetString());
        Assert.Equal(requestId.ToString("D"), lineage.GetProperty("requestId").GetString());
        Assert.Equal("2026-10-06T00:00:00.0000000Z", lineage.GetProperty("replacedAt").GetString());
        var dependency = Assert.Single(item.GetProperty("dependencies").EnumerateArray());
        Assert.Equal(new[] { "prerequisiteId", "targetKind", "state" }, Names(dependency));
        Assert.Equal(successor, dependency.GetProperty("prerequisiteId").GetString());
        Assert.Equal("Draft/Created", dependency.GetProperty("state").GetString());
        Assert.Equal("Body\nsecond line", item.GetProperty("body").GetString());
        var note = Assert.Single(item.GetProperty("notes").EnumerateArray());
        Assert.Equal(new[] { "createdAt", "text" }, Names(note));
        Assert.Equal("2026-10-06T00:00:00.0000000Z", note.GetProperty("createdAt").GetString());
        Assert.Equal("Note\nsecond line", note.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Serialize_EmptyOptionals_EmitsNullsAndEmptyCollections(string? empty)
    {
        var instant = new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.FromHours(2));
        var row = new BacklogShowAgentReadView.Row("item", "Empty item", "Open", instant, instant,
            empty, empty, "", empty, null, false, [], null, null, [], [], [], [], "", []);
        var output = BacklogShowAgentReadView.Serialize(row, new FixedTimeProvider(instant));
        using var document = AssertEnvelope(output + Environment.NewLine);
        var item = document.RootElement.GetProperty("payload");
        foreach (var property in new[] { "sourceGoalId", "priority", "supersededBy", "owner", "claimVersion", "coverage" })
            Assert.Equal(JsonValueKind.Null, item.GetProperty(property).ValueKind);
        foreach (var property in new[] { "ambiguousLinkedGoalIds", "linkedGoals", "lineage", "dependencies", "dependents", "notes" })
        {
            Assert.Equal(JsonValueKind.Array, item.GetProperty(property).ValueKind);
            Assert.Empty(item.GetProperty(property).EnumerateArray());
        }
        Assert.False(item.GetProperty("ownerAmbiguous").GetBoolean());
        Assert.Equal("", item.GetProperty("tags").GetString());
        Assert.Equal("", item.GetProperty("body").GetString());
    }

    [Fact]
    public async Task HumanForm_BothRoutes_PreserveLiteralSeedLines()
    {
        var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
        var probe = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
        string[] args = ["backlog-show", seed.Prerequisite.Id[..8]];
        var readOutput = ExecuteRead(args, seed.Workspace, probe);
        var persistentOutput = ExecutePersistent(args, seed.Workspace,
            new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath));
        Assert.Equal(readOutput, persistentOutput);
        Assert.DoesNotContain("schema", readOutput);
        var claim = new SourceBacklogClaimStore(seed.Workspace.SqliteStatePath).ResolveClaim(seed.Kernel, seed.Prerequisite.Id)!;
        var linked = Assert.Single(seed.Kernel.Goals, goal => goal.Id.Value == CliBacklogReadOnlyRouteTests.LinkedGoalId);
        Assert.Equal(new[]
        {
            $"Id:      {seed.Prerequisite.Id}", "Title:   Quartz query prerequisite", "Status:  Open",
            $"Created: {seed.Prerequisite.CreatedAt:O}", $"Updated: {seed.Prerequisite.UpdatedAt:O}",
            $"Owner:   {CliBacklogReadOnlyRouteTests.LinkedGoalId}",
            $"Version: {claim.Version.ToString(CultureInfo.InvariantCulture)}",
            $"Coverage: {claim.Coverage.ToString().ToLowerInvariant()}", "", "Linked goals:",
            $"- abc10000 status={linked.Status} authority=authoritative landing={GoalLifecycle.ResolveState(linked)}",
            "", "Dependents:", $"- {seed.Dependent.Id}", "", "Read linked goal claims"
        }, Lines(readOutput));
        AssertNoWriterAccess(probe);
    }

    [Fact]
    public async Task MissingItem_JsonAndHuman_ThrowSameErrorWithoutOutput()
    {
        var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
        var probe = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
        InvalidOperationException? humanError = null;
        InvalidOperationException? jsonError = null;
        var humanOutput = CaptureConsole(() => humanError = Assert.Throws<InvalidOperationException>(() =>
            RunRead(["backlog-show", "missing"], seed.Workspace, probe)));
        var jsonOutput = CaptureConsole(() => jsonError = Assert.Throws<InvalidOperationException>(() =>
            RunRead(["backlog-show", "missing", "--json"], seed.Workspace, probe)));
        Assert.Equal(humanError!.Message, jsonError!.Message);
        Assert.Equal("No backlog item found with id prefix 'missing'.", jsonError.Message);
        Assert.Equal("", humanOutput);
        Assert.Equal("", jsonOutput);
        AssertNoWriterAccess(probe);
    }

    [Theory]
    [InlineData("backlog-show", "x", "--jsn")]
    [InlineData("backlog-show", "x", "--json", "--frobnitz")]
    public void UnknownFlags_Validation_StillThrowsWithUsage(params string[] args)
    {
        var error = Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(args));
        Assert.Contains(CliCommandHelp.BacklogShowUsage, error.Message);
    }

    [Fact]
    public void JsonWithHelp_StartupPath_PrintsHelpWithoutEnvelope()
    {
        var output = CaptureConsole(() =>
            Assert.True(CliCommandHandlers.TryPrintStartupHelp(["backlog-show", "--json", "--help"])));
        Assert.Contains("Usage: backlog-show", output);
        Assert.Contains("[--json]", output);
        Assert.Contains("--json emits the mcg.backlog-item agent envelope.", output);
        Assert.DoesNotContain("\"schema\"", output);
    }

    private static JsonDocument AssertEnvelope(string output)
    {
        var document = JsonDocument.Parse(output);
        var envelope = document.RootElement;
        Assert.Equal(envelope.GetRawText() + Environment.NewLine, output);
        Assert.DoesNotContain("\n", envelope.GetRawText());
        Assert.DoesNotContain("\r", envelope.GetRawText());
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "payload" }, Names(envelope));
        Assert.Equal("mcg.backlog-item", envelope.GetProperty("schema").GetString());
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        var timestamp = envelope.GetProperty("generatedAt").GetString()!;
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture).Offset);
        Assert.EndsWith("Z", timestamp);
        Assert.Equal(new[] { "id", "title", "status", "createdAt", "updatedAt", "sourceGoalId", "priority", "tags",
            "supersededBy", "owner", "ownerAmbiguous", "ambiguousLinkedGoalIds", "claimVersion", "coverage",
            "linkedGoals", "lineage", "dependencies", "dependents", "body", "notes" }, Names(envelope.GetProperty("payload")));
        return document;
    }

    private static IEnumerable<string> Names(JsonElement element) => element.EnumerateObject().Select(property => property.Name);
    private static IEnumerable<string?> Strings(JsonElement array) => array.EnumerateArray().Select(element => element.GetString());
    private static string[] Lines(string output)
    {
        Assert.EndsWith(Environment.NewLine, output);
        return output[..^Environment.NewLine.Length].Split(Environment.NewLine);
    }

    private static string Scalar(string[] lines, string label) =>
        Assert.Single(lines, line => line.StartsWith(label, StringComparison.Ordinal))[label.Length..].Trim();

    private static IEnumerable<string> Section(string[] lines, string heading)
    {
        var index = Array.IndexOf(lines, heading);
        return index < 0 ? [] : lines.Skip(index + 1).TakeWhile(line => line.Length > 0);
    }

    private static string NormalizeTimestamp(string output, JsonDocument document) =>
        output.Replace("\"generatedAt\":" + document.RootElement.GetProperty("generatedAt").GetRawText(),
            "\"generatedAt\":\"<timestamp>\"", StringComparison.Ordinal);

    private static string ExecuteRead(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository probe) =>
        CaptureConsole(() => RunRead(args, workspace, probe));

    private static void RunRead(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository probe)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        Assert.True(CliReadOnlyCommandRunner.TryExecute(args, probe, workspace,
            new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref current, out var changed));
        Assert.False(changed);
    }

    private static string ExecutePersistent(string[] args, OrchestratorWorkspace workspace,
        ITransactionalOrchestratorStateRepository repository) =>
        CaptureConsole(() =>
        {
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? current = null;
            Assert.False(CliPersistentStateRunner.ExecuteCommand(args, repository, workspace, ref agents,
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
