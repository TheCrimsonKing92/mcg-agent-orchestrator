using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private heartbeat roots, an injected clock and console collection isolate each case.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGateStatusAgentReadEnvelopeContractTests : CliTaskQueryTestSupport
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private const string GoalId = "abcdef12abcdef12abcdef12abcdef12";

    [Fact]
    public void SeededCommand_PinsOrderedContractFullValuesAndHumanVerdicts()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = new DotnetBuildStorageRoot(root);
            var moving = new GateHeartbeatSnapshot(GoalId, "verification-check", "WorkerDispatchTests",
                0, 111, 222, "running", ObservedAt.AddSeconds(-31), ObservedAt.AddSeconds(-1),
                ObservedAt.AddSeconds(-10), 12, 3, 15);
            GateHeartbeatArtifacts.Write(GateHeartbeatArtifacts.GetStableSlotPath(0, storage), moving);
            GateHeartbeatArtifacts.Write(GateHeartbeatArtifacts.GetStableSlotPath(1, storage), moving with
            {
                SlotIndex = 1,
                LastProgressAt = ObservedAt - GateLivenessVerdict.SilenceBound - TimeSpan.FromMinutes(1)
            });

            var output = CaptureConsole(() => CliGateStatusJsonQueryCommand.Execute(new FixedTimeProvider(), storage));
            Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            using var json = JsonDocument.Parse(output);
            var payload = AssertEnvelope(json.RootElement);
            var slots = payload.GetProperty("slots").EnumerateArray().ToArray();
            var statuses = GateHeartbeatArtifacts.ReadStableSlots(storage, ObservedAt);
            Assert.Equal(statuses.Select(status => status.SlotIndex), slots.Select(slot => slot.GetProperty("slot").GetInt32()));
            Assert.All(slots, AssertSlotNames);
            var live = slots[0];
            Assert.Equal(GoalId, live.GetProperty("goal").GetString());
            Assert.Equal("verification-check", live.GetProperty("phase").GetString());
            Assert.Equal("running", live.GetProperty("state").GetString());
            Assert.Equal("WorkerDispatchTests", live.GetProperty("target").GetString());
            Assert.Equal(30d, live.GetProperty("elapsedSeconds").GetDouble());
            Assert.Equal(222, live.GetProperty("childPid").GetInt32());
            Assert.Equal(1d, live.GetProperty("heartbeatAgeSeconds").GetDouble());
            Assert.Equal(10d, live.GetProperty("idleSeconds").GetDouble());
            Assert.Equal(15, live.GetProperty("outputBytes").GetInt64());
            Assert.Equal("RUNNING", live.GetProperty("verdict").GetString());
            Assert.Equal("STALLED", slots[1].GetProperty("verdict").GetString());
            var host = payload.GetProperty("host");
            AssertHostNames(host);
            Assert.Equal("STALLED", host.GetProperty("verdict").GetString());
            Assert.Equal(1, host.GetProperty("running").GetInt32());
            Assert.Equal(1, host.GetProperty("stalled").GetInt32());
            Assert.Equal(slots.Length, host.GetProperty("slots").GetInt32());

            var human = CaptureConsole(() => GateStatusTextView.Print(statuses, ObservedAt));
            foreach (var slot in slots)
            {
                var index = slot.GetProperty("slot").GetInt32();
                var line = Assert.Single(human.Split("\r\n"), line => line.StartsWith($"slot-{index}:", StringComparison.Ordinal));
                var label = slot.GetProperty("verdict").GetString();
                Assert.Equal(GateLivenessVerdict.Classify(statuses[index], ObservedAt).Label, label);
                Assert.EndsWith($" verdict={label}", line);
            }
            Assert.Contains("elapsed=30s ", human);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EmptyCommand_KeepsEverySlotAndNullDetailProperties()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = new DotnetBuildStorageRoot(root);
            using var json = JsonDocument.Parse(CaptureConsole(() =>
                CliGateStatusJsonQueryCommand.Execute(new FixedTimeProvider(), storage)));
            var payload = AssertEnvelope(json.RootElement);
            var slots = payload.GetProperty("slots").EnumerateArray().ToArray();
            Assert.NotEmpty(slots);
            Assert.Equal(GateHeartbeatArtifacts.ReadStableSlots(storage, ObservedAt).Select(status => status.SlotIndex),
                slots.Select(slot => slot.GetProperty("slot").GetInt32()));
            foreach (var slot in slots)
            {
                AssertSlotNames(slot);
                Assert.Equal(JsonValueKind.Number, slot.GetProperty("slot").ValueKind);
                Assert.Equal("NONE", slot.GetProperty("verdict").GetString());
                foreach (var name in new[] { "goal", "phase", "state", "target", "elapsedSeconds", "childPid",
                    "heartbeatAgeSeconds", "idleSeconds", "outputBytes" })
                    Assert.Equal(JsonValueKind.Null, slot.GetProperty(name).ValueKind);
            }
            var host = payload.GetProperty("host");
            AssertHostNames(host);
            Assert.Equal("NONE", host.GetProperty("verdict").GetString());
            Assert.Equal(0, host.GetProperty("running").GetInt32());
            Assert.Equal(0, host.GetProperty("stalled").GetInt32());
            Assert.Equal(slots.Length, host.GetProperty("slots").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void View_RoundsSecondsClampsElapsedAndKeepsAbsentValues()
    {
        var snapshot = new GateHeartbeatSnapshot(null, "verification-check", "target", 7, 111, null,
            "completed", ObservedAt.AddSeconds(-60.12345), ObservedAt.AddSeconds(-1), ObservedAt, 0, 0, 0);
        var status = new GateHeartbeatStatus(7, "unused", true, null, snapshot,
            TimeSpan.FromSeconds(1.23456), TimeSpan.FromSeconds(2.34567));
        using var json = JsonDocument.Parse(GateStatusAgentReadView.Render([status], ObservedAt, new FixedTimeProvider()));
        var slot = Assert.Single(AssertEnvelope(json.RootElement).GetProperty("slots").EnumerateArray());
        AssertSlotNames(slot);
        Assert.Equal(7, slot.GetProperty("slot").GetInt32());
        Assert.Equal(59.123, slot.GetProperty("elapsedSeconds").GetDouble());
        Assert.Equal(1.235, slot.GetProperty("heartbeatAgeSeconds").GetDouble());
        Assert.Equal(2.346, slot.GetProperty("idleSeconds").GetDouble());
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("goal").ValueKind);
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("childPid").ValueKind);
        Assert.Equal("NONE", slot.GetProperty("verdict").GetString());
        foreach (var edge in new[]
        {
            status with { Snapshot = snapshot with { StartedAt = ObservedAt.AddSeconds(1) } },
            status with { HeartbeatAge = null, IdleDuration = null }
        })
        {
            using var edgeJson = JsonDocument.Parse(GateStatusAgentReadView.Render([edge], ObservedAt));
            var row = Assert.Single(edgeJson.RootElement.GetProperty("payload").GetProperty("slots").EnumerateArray());
            Assert.Equal(0d, row.GetProperty("elapsedSeconds").GetDouble());
            if (edge.HeartbeatAge is null)
            {
                Assert.Equal(JsonValueKind.Null, row.GetProperty("heartbeatAgeSeconds").ValueKind);
                Assert.Equal(JsonValueKind.Null, row.GetProperty("idleSeconds").ValueKind);
            }
        }
    }

    private static JsonElement AssertEnvelope(JsonElement envelope)
    {
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "payload" },
            envelope.EnumerateObject().Select(property => property.Name));
        Assert.Equal("mcg.gate-status", AgentReadEnvelope.GateStatusSchema);
        Assert.Equal(1, AgentReadEnvelope.GateStatusSchemaVersion);
        Assert.Equal("mcg.gate-status", envelope.GetProperty("schema").GetString());
        Assert.Equal(JsonValueKind.Number, envelope.GetProperty("schemaVersion").ValueKind);
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-10-06T00:00:00.0000000Z", envelope.GetProperty("generatedAt").GetString());
        var payload = envelope.GetProperty("payload");
        Assert.Equal(new[] { "slots", "host" }, payload.EnumerateObject().Select(property => property.Name));
        return payload;
    }

    private static void AssertSlotNames(JsonElement slot) =>
        Assert.Equal(new[] { "slot", "goal", "phase", "state", "target", "elapsedSeconds", "childPid",
            "heartbeatAgeSeconds", "idleSeconds", "outputBytes", "verdict" },
            slot.EnumerateObject().Select(property => property.Name));

    private static void AssertHostNames(JsonElement host) =>
        Assert.Equal(new[] { "verdict", "running", "stalled", "slots" }, host.EnumerateObject().Select(property => property.Name));

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ObservedAt;
    }
}
