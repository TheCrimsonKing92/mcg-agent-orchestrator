using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private heartbeat roots and a fixed observation instant isolate all values.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGateStatusTextViewCharacterizationTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HumanOutput_KeepsLiteralBytes(int populatedSlots)
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = new DotnetBuildStorageRoot(root);
            var now = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
            for (var slot = 0; slot < populatedSlots; slot++)
                GateHeartbeatArtifacts.Write(GateHeartbeatArtifacts.GetStableSlotPath(slot, storage),
                    new GateHeartbeatSnapshot("abcdef12abcdef12abcdef12abcdef12", "verification-check",
                        "Worker Dispatch Tests", slot, 111, 222, "running", now.AddSeconds(-31),
                        now.AddSeconds(-1), slot == 0 ? now.AddSeconds(-10) : now.AddMinutes(-11), 12, 3, 15));

            var statuses = GateHeartbeatArtifacts.ReadStableSlots(storage, now);
            var actual = CaptureConsole(() => GateStatusTextView.Print(statuses, now));
            foreach (var status in statuses)
                actual = actual.Replace(status.Path, $"<heartbeat-{status.SlotIndex}>", StringComparison.Ordinal);

            var expected = "Gate status:\r\n";
            if (populatedSlots >= 1)
                expected += "slot-0: goal=abcdef12 phase=verification-check state=running elapsed=30s target=\"Worker Dispatch Tests\" child_pid=222 heartbeat_age=1s idle_for=10s output_bytes=15 heartbeat=<heartbeat-0> verdict=RUNNING\r\n";
            if (populatedSlots == 2)
                expected += "slot-1: goal=abcdef12 phase=verification-check state=running elapsed=30s target=\"Worker Dispatch Tests\" child_pid=222 heartbeat_age=1s idle_for=11m output_bytes=15 heartbeat=<heartbeat-1> verdict=STALLED\r\n";
            for (var slot = populatedSlots; slot < DotnetBuildEnvironmentManager.StableSlotCount; slot++)
                expected += $"slot-{slot}: unavailable reason=missing heartbeat=<heartbeat-{slot}> verdict=NONE\r\n";
            expected += populatedSlots switch
            {
                0 => $"host: verdict=NONE running=0 stalled=0 slots={DotnetBuildEnvironmentManager.StableSlotCount}; covers per-goal and cohort gates alike; no gate running on any build slot; no process check needed\r\n",
                1 => $"host: verdict=RUNNING running=1 stalled=0 slots={DotnetBuildEnvironmentManager.StableSlotCount}; covers per-goal and cohort gates alike\r\n",
                _ => $"host: verdict=STALLED running=1 stalled=1 slots={DotnetBuildEnvironmentManager.StableSlotCount}; covers per-goal and cohort gates alike\r\n"
            };
            Assert.Equal(expected, actual);
        }
        finally { Directory.Delete(root, true); }
    }
}
