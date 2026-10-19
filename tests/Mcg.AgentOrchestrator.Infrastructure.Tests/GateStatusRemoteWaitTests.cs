using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: shared console capture is AsyncLocal-routed; roots and clocks are test-local.
public sealed class GateStatusRemoteWaitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider Clock = new FixedTimeProvider();

    [Fact]
    public void Render_OnlyRemoteWait_ShowsRemoteHostInTextAndJson()
    {
        var hostLine = HostLine([], 1);
        Assert.Contains("verdict=RUNNING (remote)", hostLine);
        Assert.Contains("remote_waits=1", hostLine);
        Assert.DoesNotContain("no gate running on any build slot", hostLine);
        var host = JsonHost([], 1);
        Assert.Equal("RUNNING (remote)", host.GetProperty("verdict").GetString());
        Assert.Equal(1, host.GetProperty("remoteWaits").GetInt32());
    }

    [Fact]
    public void Render_ZeroRemoteWaits_PreservesDefaultBytes()
    {
        Assert.Equal(CaptureConsole(() => GateStatusTextView.Print([], Now)),
            CaptureConsole(() => GateStatusTextView.Print([], Now, remoteWaits: 0)));
        Assert.Equal(GateStatusAgentReadView.Render([], Now, Clock),
            GateStatusAgentReadView.Render([], Now, Clock, remoteWaits: 0));
        Assert.Equal("NONE", JsonHost([], 0).GetProperty("verdict").GetString());
        Assert.False(JsonHost([], 0).TryGetProperty("remoteWaits", out _));
    }

    [Theory]
    [InlineData(false, "RUNNING")]
    [InlineData(true, "STALLED")]
    public void Render_ActiveSlot_KeepsSlotVerdict(bool stalled, string expected)
    {
        var progress = stalled ? Now.AddMinutes(-11) : Now;
        var snapshot = new GateHeartbeatSnapshot("goal", "test", "lane", 0, null, null,
            "running", Now.AddMinutes(-20), Now, progress, 0, 0, 0);
        GateHeartbeatStatus[] slots = [new(0, "slot-heartbeat.json", true, null, snapshot,
            TimeSpan.Zero, Now - progress)];
        Assert.Contains($"verdict={expected} remote_waits=1", HostLine(slots, 1));
        var host = JsonHost(slots, 1);
        Assert.Equal(expected, host.GetProperty("verdict").GetString());
        Assert.Equal(1, host.GetProperty("remoteWaits").GetInt32());
        Assert.Equal(CaptureConsole(() => GateStatusTextView.Print(slots, Now)),
            CaptureConsole(() => GateStatusTextView.Print(slots, Now, remoteWaits: 0)));
        Assert.Equal(GateStatusAgentReadView.Render(slots, Now, Clock),
            GateStatusAgentReadView.Render(slots, Now, Clock, remoteWaits: 0));
    }

    [Fact]
    public void Count_WorkspaceHeartbeat_UsesInjectedTimeAndExcludesStaleWait()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var path = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts",
                "goal", "attempt.heartbeat.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "running", lastProgressAt = Now }));
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
            Assert.Equal(1, GateStatusRemoteWait.Count(workspace, Now));
            var storage = new DotnetBuildStorageRoot(Path.Combine(root, "build-storage"));
            var json = CaptureConsole(() => CliGateStatusJsonQueryCommand.Execute(Clock, storage, workspace));
            using (var document = JsonDocument.Parse(json))
            {
                var host = document.RootElement.GetProperty("payload").GetProperty("host");
                Assert.Equal("RUNNING (remote)", host.GetProperty("verdict").GetString());
                Assert.Equal(1, host.GetProperty("remoteWaits").GetInt32());
            }
            File.SetLastWriteTimeUtc(path, Now.AddMinutes(-11).UtcDateTime);
            var count = GateStatusRemoteWait.Count(workspace, Now);
            Assert.Equal(0, count);
            Assert.Contains("verdict=NONE ", HostLine([], count));
            Assert.Equal("NONE", JsonHost([], count).GetProperty("verdict").GetString());
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Count_MissingWorkspaceOrRoot_ReturnsZeroWithoutWriting()
    {
        Assert.Equal(0, GateStatusRemoteWait.Count(null, Now));
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Assert.Equal(0, GateStatusRemoteWait.Count(workspace, Now));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string HostLine(IReadOnlyList<GateHeartbeatStatus> slots, int remoteWaits) =>
        Assert.Single(CaptureConsole(() => GateStatusTextView.Print(slots, Now, remoteWaits))
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), line => line.StartsWith("host: "));

    private static JsonElement JsonHost(IReadOnlyList<GateHeartbeatStatus> slots, int remoteWaits)
    {
        using var document = JsonDocument.Parse(GateStatusAgentReadView.Render(slots, Now, Clock, remoteWaits));
        return document.RootElement.GetProperty("payload").GetProperty("host").Clone();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
