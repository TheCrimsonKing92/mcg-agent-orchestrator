using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private roots, injected clocks and AsyncLocal console capture; safe to run in parallel.
public sealed class CliAttentionAgentReadEnvelopeContractTests : CliTaskQueryTestSupport
{
    [Fact]
    public async Task SeededListFormsPinEnvelopeAndFullPayload()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel(new FixedClock());
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Attention contract");
            var wait = kernel.RequestHumanInput(goal.Id, null, "Choose <full answer>",
                isAutoDefaultable: true, isDismissible: false, isAnswerRequired: true,
                isExternallyBlocked: false, resumeCommand: "attention answer full-request <answer>");
            var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var decision = await store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value,
                "Goal decision", "Full <body>", "goal-decision");
            var crossGoal = await store.RaiseAsync(CollaborationItemType.Decision, null,
                "Cross-goal decision", "Cross-goal body", "cross-goal-decision");
            var items = await store.GetAttentionQueueAsync();
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            using var bare = JsonDocument.Parse(Run(["attention", "--json"], workspace, repository));
            using var list = JsonDocument.Parse(Run(["attention", "list", "--json"], workspace, repository));
            var payload = AssertEnvelope(bare.RootElement);
            Assert.Equal(payload.GetRawText(), AssertEnvelope(list.RootElement).GetRawText());
            var row = Assert.Single(payload.GetProperty("waits").EnumerateArray());
            Assert.Equal(new[] { "id", "goalId", "taskId", "kind", "question", "plannerIdentity", "plannerEvidence", "owner", "availability", "nextAction", "requestedAt",
                "isAutoDefaultable", "isDismissible", "isAnswerRequired", "isExternallyBlocked", "resumeCommand" },
                row.EnumerateObject().Select(property => property.Name));
            Assert.Equal(wait.Id.Value, row.GetProperty("id").GetString());
            Assert.Equal(goal.Id.Value, row.GetProperty("goalId").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("taskId").ValueKind);
            Assert.Equal("SpecClarification", row.GetProperty("kind").GetString());
            Assert.Equal(wait.Question, row.GetProperty("question").GetString());
            Assert.Equal(wait.RequestedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                row.GetProperty("requestedAt").GetString());
            Assert.True(row.GetProperty("isAutoDefaultable").GetBoolean());
            Assert.False(row.GetProperty("isDismissible").GetBoolean());
            Assert.True(row.GetProperty("isAnswerRequired").GetBoolean());
            Assert.False(row.GetProperty("isExternallyBlocked").GetBoolean());
            Assert.Equal(wait.ResumeCommand, row.GetProperty("resumeCommand").GetString());
            var rows = payload.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal(items.Select(item => item.Id), rows.Select(item => item.GetProperty("id").GetString()));
            foreach (var item in new[] { decision, crossGoal })
            {
                var itemRow = Assert.Single(rows, candidate => candidate.GetProperty("id").GetString() == item.Id);
                Assert.Equal(new[] { "id", "type", "goalId", "status", "subject", "body", "correlationKey", "raisedAt" },
                    itemRow.EnumerateObject().Select(property => property.Name));
                Assert.Equal("Decision", itemRow.GetProperty("type").GetString());
                Assert.Equal("Raised", itemRow.GetProperty("status").GetString());
                Assert.Equal(item.GoalId, itemRow.GetProperty("goalId").GetString());
                Assert.Equal(item.Subject, itemRow.GetProperty("subject").GetString());
                Assert.Equal(item.Body, itemRow.GetProperty("body").GetString());
                Assert.Equal(item.CorrelationKey, itemRow.GetProperty("correlationKey").GetString());
                var stored = Assert.Single(items, candidate => candidate.Id == item.Id);
                Assert.Equal(stored.RaisedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    itemRow.GetProperty("raisedAt").GetString());
            }
            Assert.Equal(JsonValueKind.Null,
                Assert.Single(rows, item => item.GetProperty("id").GetString() == crossGoal.Id)
                    .GetProperty("goalId").ValueKind);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("attention", "--json")]
    [InlineData("attention", "list", "--json")]
    public void EmptyListKeepsBothArrays(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            using var json = JsonDocument.Parse(Run(args, workspace, new ProbeStateRepository(new AgentOrchestratorKernel())));
            var payload = AssertEnvelope(json.RootElement);
            Assert.Equal(JsonValueKind.Array, payload.GetProperty("waits").ValueKind);
            Assert.Equal(JsonValueKind.Array, payload.GetProperty("items").ValueKind);
            Assert.Empty(payload.GetProperty("waits").EnumerateArray());
            Assert.Empty(payload.GetProperty("items").EnumerateArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ViewUsesEnvelopeClockAndFullTaskId()
    {
        var wait = new HumanInputRequest(new HumanInputRequestId("wait1000aaaaaaaaaaaaaaaaaaaaaaaa"),
            new GoalId("goal1000aaaaaaaaaaaaaaaaaaaaaaaa"), new TaskId("task1000aaaaaaaaaaaaaaaaaaaaaaaa"),
            "Task question", new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.FromHours(2)));
        using var json = JsonDocument.Parse(AttentionAgentReadView.Render([wait], [], new FixedTimeProvider()));
        var payload = AssertEnvelope(json.RootElement);
        Assert.Equal("2026-10-06T00:00:00.0000000Z", json.RootElement.GetProperty("generatedAt").GetString());
        var row = Assert.Single(payload.GetProperty("waits").EnumerateArray());
        Assert.Equal(wait.TaskId!.Value, row.GetProperty("taskId").GetString());
        Assert.Equal("2026-10-06T00:00:00.0000000Z", row.GetProperty("requestedAt").GetString());
    }

    [Fact]
    public void EmptyViewsPinOrdinaryBytesAndExplicitSummaryOmission()
    {
        Assert.Equal("{\"schema\":\"mcg.attention\",\"schemaVersion\":1,\"generatedAt\":\"2026-10-06T00:00:00.0000000Z\",\"payload\":{\"waits\":[],\"gateEvidence\":[],\"items\":[],\"providerHolds\":[],\"ownerQuestionHolds\":[]}}",
            AttentionAgentReadView.Render([], [], new FixedTimeProvider()));
        Assert.Equal("{\"schema\":\"mcg.attention-summary\",\"schemaVersion\":1,\"generatedAt\":\"2026-10-06T00:00:00.0000000Z\",\"omittedSections\":[\"gateEvidence\"],\"payload\":{\"waits\":[],\"items\":[],\"providerHolds\":[],\"ownerQuestionHolds\":[]}}",
            AttentionAgentReadView.RenderSummary([], [], new FixedTimeProvider()));
    }

    private static JsonElement AssertEnvelope(JsonElement envelope)
    {
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "payload" },
            envelope.EnumerateObject().Select(property => property.Name));
        Assert.Equal("mcg.attention", AgentReadEnvelope.AttentionSchema);
        Assert.Equal(1, AgentReadEnvelope.AttentionSchemaVersion);
        Assert.Equal("mcg.attention", envelope.GetProperty("schema").GetString());
        Assert.Equal(JsonValueKind.Number, envelope.GetProperty("schemaVersion").ValueKind);
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        var timestamp = envelope.GetProperty("generatedAt").GetString()!;
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture).Offset);
        Assert.EndsWith("Z", timestamp);
        var payload = envelope.GetProperty("payload");
        Assert.Equal(new[] { "waits", "gateEvidence", "items", "providerHolds", "ownerQuestionHolds" }, payload.EnumerateObject().Select(property => property.Name));
        return payload;
    }

    private static string Run(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository repository)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        return CaptureConsole(() =>
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
            Assert.False(changed);
            Assert.Null(goal);
        });
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 6, 2, 0, 0, TimeSpan.FromHours(2));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 6, 2, 0, 0, TimeSpan.FromHours(2));
    }
}
