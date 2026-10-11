using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private roots, injected clocks and AsyncLocal console capture isolate each case.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliAttentionShowAgentReadEnvelopeContractTests : CliTaskQueryTestSupport
{
    [Fact]
    public async Task SeededForms_PinOrderedContractAndFullValues()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await SeedAsync(root);
            foreach (var args in Forms())
            {
                using var json = JsonDocument.Parse(Run(args, seed.Workspace, seed.Kernel));
                var payload = AssertEnvelope(json.RootElement);
                Assert.Equal(args.Length == 3 ? null : seed.First.Id.Value,
                    payload.GetProperty("goalId").GetString());
                var wait = Assert.Single(payload.GetProperty("waits").EnumerateArray());
                Assert.Equal(new[] { "id", "goalId", "taskId", "kind", "question", "requestedAt",
                    "isAutoDefaultable", "isDismissible", "isAnswerRequired", "isExternallyBlocked", "resumeCommand" },
                    wait.EnumerateObject().Select(property => property.Name));
                Assert.Equal(seed.Wait.Id.Value, wait.GetProperty("id").GetString());
                Assert.Equal(seed.First.Id.Value, wait.GetProperty("goalId").GetString());
                Assert.Equal(JsonValueKind.Null, wait.GetProperty("taskId").ValueKind);
                Assert.Equal("SpecClarification", wait.GetProperty("kind").GetString());
                Assert.Equal("Literal question", wait.GetProperty("question").GetString());
                Assert.Equal("2100-01-01T00:00:00.0000000Z", wait.GetProperty("requestedAt").GetString());
                Assert.True(wait.GetProperty("isAutoDefaultable").GetBoolean());
                Assert.False(wait.GetProperty("isDismissible").GetBoolean());
                Assert.True(wait.GetProperty("isAnswerRequired").GetBoolean());
                Assert.False(wait.GetProperty("isExternallyBlocked").GetBoolean());
                Assert.Equal(seed.Wait.ResumeCommand, wait.GetProperty("resumeCommand").GetString());
                var rows = payload.GetProperty("items").EnumerateArray().ToArray();
                var expectedItems = (await seed.Store.GetAttentionQueueAsync())
                    .Where(item => args.Length == 3 || item.GoalId == seed.First.Id.Value).ToArray();
                Assert.Equal(expectedItems.Select(item => item.Id), rows.Select(item => item.GetProperty("id").GetString()));
                Assert.Equal(args.Length == 3 ? 4 : 2, rows.Length);
                foreach (var item in expectedItems)
                {
                    var row = Assert.Single(rows, candidate => candidate.GetProperty("id").GetString() == item.Id);
                    Assert.Equal(new[] { "id", "clarificationId", "type", "goalId", "status", "subject", "body",
                        "correlationKey", "raisedAt" }, row.EnumerateObject().Select(property => property.Name));
                    Assert.Equal(item.Type.ToString(), row.GetProperty("type").GetString());
                    Assert.Equal(item.GoalId, row.GetProperty("goalId").GetString());
                    Assert.Equal("Raised", row.GetProperty("status").GetString());
                    Assert.Equal(item.Subject, row.GetProperty("subject").GetString());
                    Assert.Equal(item.Body, row.GetProperty("body").GetString());
                    Assert.Equal(item.CorrelationKey, row.GetProperty("correlationKey").GetString());
                    Assert.Equal(item.RaisedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        row.GetProperty("raisedAt").GetString());
                    if (item.Id == seed.Clarification.Id)
                        Assert.Equal("choose-path", row.GetProperty("clarificationId").GetString());
                    else
                        Assert.Equal(JsonValueKind.Null, row.GetProperty("clarificationId").ValueKind);
                }
                if (args.Length == 3)
                    Assert.Equal(JsonValueKind.Null, payload.GetProperty("goalId").ValueKind);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ScopedForms_SelectOnlyTheirGoalAndHaveIdenticalPayloads()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await SeedAsync(root);
            var secondWait = seed.Kernel.RequestHumanInput(seed.Second.Id, null, "Other goal question");
            using var global = JsonDocument.Parse(Run(Forms()[0], seed.Workspace, seed.Kernel));
            using var scoped = JsonDocument.Parse(Run(Forms()[1], seed.Workspace, seed.Kernel));
            using var flagged = JsonDocument.Parse(Run(Forms()[2], seed.Workspace, seed.Kernel));
            var all = AssertEnvelope(global.RootElement);
            var first = AssertEnvelope(scoped.RootElement);
            Assert.Equal(first.GetRawText(), AssertEnvelope(flagged.RootElement).GetRawText());
            Assert.Equal(seed.First.Id.Value, first.GetProperty("goalId").GetString());
            Assert.Equal(seed.Wait.Id.Value, Assert.Single(first.GetProperty("waits").EnumerateArray())
                .GetProperty("id").GetString());
            Assert.Equal(2, first.GetProperty("items").GetArrayLength());
            Assert.All(first.GetProperty("items").EnumerateArray(), item =>
                Assert.Equal(seed.First.Id.Value, item.GetProperty("goalId").GetString()));
            Assert.Equal(JsonValueKind.Null, all.GetProperty("goalId").ValueKind);
            Assert.Equal(new[] { seed.Wait.Id.Value, secondWait.Id.Value },
                all.GetProperty("waits").EnumerateArray().Select(wait => wait.GetProperty("id").GetString()));
            Assert.Equal(4, all.GetProperty("items").GetArrayLength());
            Assert.Contains(all.GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("goalId").GetString() == seed.Second.Id.Value);
            Assert.Contains(all.GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("goalId").ValueKind == JsonValueKind.Null);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClarificationId_UsesHumanIdentityIncludingResolvedPeers(bool collision)
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await SeedAsync(root, collision);
            var expectedId = collision ? seed.Clarification.CorrelationKey! : "choose-path";
            var human = Run(["attention", "show", "abc10000"], seed.Workspace, seed.Kernel);
            var humanLine = Assert.Single(human.Split("\r\n"), line => line.EndsWith("] Choose path", StringComparison.Ordinal));
            Assert.Equal(expectedId, humanLine[1..humanLine.IndexOf(']')]);
            foreach (var args in Forms())
            {
                using var json = JsonDocument.Parse(Run(args, seed.Workspace, seed.Kernel));
                var items = AssertEnvelope(json.RootElement).GetProperty("items").EnumerateArray();
                var clarification = Assert.Single(items, item => item.GetProperty("id").GetString() == seed.Clarification.Id);
                Assert.Equal(expectedId, clarification.GetProperty("clarificationId").GetString());
                Assert.Equal(JsonValueKind.Null, Assert.Single(items, item => item.GetProperty("id").GetString() == seed.Decision.Id)
                    .GetProperty("clarificationId").ValueKind);
                if (collision)
                {
                    Assert.Contains(await seed.Store.ListAsync(), item => item.Status == CollaborationItemStatus.Resolved);
                    Assert.DoesNotContain(items, item => item.GetProperty("status").GetString() == "Resolved");
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("attention", "show", "--json")]
    [InlineData("attention", "show", "abc10000", "--json")]
    [InlineData("attention", "show", "--goal", "abc10000", "--json")]
    public void EmptyGoal_KeepsBothArrays(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Empty goal");
            using var json = JsonDocument.Parse(Run(args, workspace, kernel));
            var payload = AssertEnvelope(json.RootElement);
            Assert.Equal(args.Length == 3 ? null : goal.Id.Value, payload.GetProperty("goalId").GetString());
            Assert.Equal(JsonValueKind.Array, payload.GetProperty("waits").ValueKind);
            Assert.Equal(JsonValueKind.Array, payload.GetProperty("items").ValueKind);
            Assert.Empty(payload.GetProperty("waits").EnumerateArray());
            Assert.Empty(payload.GetProperty("items").EnumerateArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void View_UsesEnvelopeClockAndFullTaskId()
    {
        var wait = new HumanInputRequest(new HumanInputRequestId("wait1000aaaaaaaaaaaaaaaaaaaaaaaa"),
            new GoalId("goal1000aaaaaaaaaaaaaaaaaaaaaaaa"), new TaskId("task1000aaaaaaaaaaaaaaaaaaaaaaaa"),
            "Task question", new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.FromHours(2)));
        using var json = JsonDocument.Parse(AttentionShowAgentReadView.Render(wait.GoalId.Value, [wait], [],
            _ => null, new FixedTimeProvider()));
        var payload = AssertEnvelope(json.RootElement);
        Assert.Equal("2026-10-06T00:00:00.0000000Z", json.RootElement.GetProperty("generatedAt").GetString());
        var row = Assert.Single(payload.GetProperty("waits").EnumerateArray());
        Assert.Equal(wait.TaskId!.Value, row.GetProperty("taskId").GetString());
        Assert.Equal("2026-10-06T00:00:00.0000000Z", row.GetProperty("requestedAt").GetString());
    }

    [Fact]
    public async Task HumanShowAndBareJson_KeepLiteralBytes()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await SeedAsync(root);
            var waits = "\r\nHuman waits: 1 active wait(s)\r\n" +
                $"  [SpecClarification] {seed.Wait.Id.Value[..8]} goal=abc10000 task=goal age=0s\r\n" +
                "    flags: auto-defaultable=True; dismissible=False; answer-required=True; externally-blocked=False\r\n" +
                "    question: Literal question\r\n" +
                $"    resume: attention answer abc10000 {seed.Wait.Id.Value[..8]} <answer>\r\n\r\n";
            var scoped = waits;
            foreach (var item in (await seed.Store.GetAttentionQueueAsync()).Where(item => item.GoalId == seed.First.Id.Value))
                scoped += item.Id == seed.Clarification.Id
                    ? "[choose-path] Choose path\r\n    Clarification body\r\n"
                    : $"[{seed.Decision.Id[..8]}] (Decision) Goal decision\r\n";
            scoped += "Answer with: attention answer abc10000 <id> <answer> or --text-file <path> (ids are stable; answering one does not renumber the rest)\r\n";
            foreach (var (args, expected) in new[]
            {
                (new[] { "attention", "show" }, waits),
                (new[] { "attention", "show", "abc10000" }, scoped)
            })
            {
                Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(Run(args, seed.Workspace, seed.Kernel)));
                IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                var profiles = WorkerProfileCatalog.Default();
                Goal? goal = null;
                var writer = CaptureConsole(() => Assert.False(CliCommandDispatcher.ExecuteCommand(args, seed.Kernel,
                    seed.Workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref goal)));
                Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(writer));
            }
            // Pin the landed payload's literal field names and values, without using either view projection.
            var expectedBarePayload = JsonSerializer.Serialize(new
            {
                waits = new[] { new
                {
                    id = seed.Wait.Id.Value, goalId = seed.First.Id.Value, taskId = (string?)null,
                    kind = "SpecClarification", question = "Literal question", requestedAt = "2100-01-01T00:00:00.0000000Z",
                    isAutoDefaultable = true, isDismissible = false, isAnswerRequired = true, isExternallyBlocked = false,
                    resumeCommand = seed.Wait.ResumeCommand
                } },
                items = (await seed.Store.GetAttentionQueueAsync()).Select(item => new
                {
                    id = item.Id, type = item.Type.ToString(), goalId = item.GoalId, status = "Raised",
                    subject = item.Subject, body = item.Body, correlationKey = item.CorrelationKey,
                    raisedAt = item.RaisedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                }).ToArray()
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var bare = JsonDocument.Parse(Run(["attention", "--json"], seed.Workspace, seed.Kernel));
            Assert.Equal("mcg.attention", bare.RootElement.GetProperty("schema").GetString());
            Assert.Equal(1, bare.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(expectedBarePayload, bare.RootElement.GetProperty("payload").GetRawText());
            var expectedEnvelope = "{\"schema\":\"mcg.attention\",\"schemaVersion\":1,\"generatedAt\":\"2026-10-06T00:00:00.0000000Z\",\"payload\":" + expectedBarePayload + "}";
            Assert.Equal(expectedEnvelope, AttentionAgentReadView.Render([seed.Wait],
                await seed.Store.GetAttentionQueueAsync(), new FixedTimeProvider()));
        }
        finally { Directory.Delete(root, true); }
    }

    private static JsonElement AssertEnvelope(JsonElement envelope)
    {
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "payload" },
            envelope.EnumerateObject().Select(property => property.Name));
        Assert.Equal("mcg.attention-show", AgentReadEnvelope.AttentionShowSchema);
        Assert.Equal(1, AgentReadEnvelope.AttentionShowSchemaVersion);
        Assert.Equal("mcg.attention-show", envelope.GetProperty("schema").GetString());
        Assert.Equal(JsonValueKind.Number, envelope.GetProperty("schemaVersion").ValueKind);
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        var timestamp = envelope.GetProperty("generatedAt").GetString()!;
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture).Offset);
        Assert.EndsWith("Z", timestamp);
        var payload = envelope.GetProperty("payload");
        Assert.Equal(new[] { "goalId", "waits", "items" }, payload.EnumerateObject().Select(property => property.Name));
        return payload;
    }

    private static string[][] Forms() =>
    [
        ["attention", "show", "--json"],
        ["attention", "show", "abc10000", "--json"],
        ["attention", "show", "--goal", "abc10000", "--json"]
    ];

    private static string Run(string[] args, OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        return CaptureConsole(() =>
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, new ProbeStateRepository(kernel) { ThrowOnOutbox = true },
                workspace, new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
            Assert.False(changed);
            Assert.Null(goal);
        });
    }

    private static async Task<Seed> SeedAsync(string root, bool collision = false)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel(new FutureClock());
        var first = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "First goal");
        var second = kernel.CreateGoal(new GoalId("abc20000bbbbbbbbbbbbbbbbbbbbbbbb"), "Second goal");
        var wait = kernel.RequestHumanInput(first.Id, null, "Literal question", isAutoDefaultable: true,
            isDismissible: false, isAnswerRequired: true, isExternallyBlocked: false);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var key = $"spec-clarification:{first.Id.Value}:scope-a:" + (collision ? "duplicate-topic" : "choose-path");
        var clarification = await store.RaiseAsync(CollaborationItemType.Clarification, first.Id.Value,
            "Choose path", "Clarification body", key);
        var decision = await store.RaiseAsync(CollaborationItemType.Decision, first.Id.Value, "Goal decision", "Decision body");
        await store.RaiseAsync(CollaborationItemType.Decision, second.Id.Value, "Other decision", "Other body");
        await store.RaiseAsync(CollaborationItemType.Decision, null, "Cross-goal decision", "Cross-goal body");
        if (collision)
        {
            var peer = await store.RaiseAsync(CollaborationItemType.Clarification, first.Id.Value,
                "Resolved peer", "Peer body", $"spec-clarification:{first.Id.Value}:scope-b:duplicate-topic");
            Assert.True(await store.TryResolveByIdAsync(peer.Id, "Already answered"));
        }
        return new Seed(workspace, kernel, store, first, second, wait, clarification, decision);
    }

    private sealed record Seed(OrchestratorWorkspace Workspace, AgentOrchestratorKernel Kernel,
        CollaborationItemStore Store, Goal First, Goal Second, HumanInputRequest Wait,
        CollaborationItem Clarification, CollaborationItem Decision);

    private sealed class FutureClock : IClock
    {
        public DateTimeOffset UtcNow => new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 6, 2, 0, 0, TimeSpan.FromHours(2));
    }
}
