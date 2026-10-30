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
    public void Print_TwoGateStatuses_PreservesExactText()
    {
        var running = new GateHeartbeatSnapshot("abcdef12abcdef12", "verification-check",
            "First gate", 0, 111, 222, "running", Now.AddSeconds(-31),
            Now.AddSeconds(-1), Now.AddSeconds(-10), 12, 3, 15);
        var stalled = new GateHeartbeatSnapshot("0123456789abcdef", "verification-check",
            "Second gate", 1, 333, 444, "running", Now.AddSeconds(-31),
            Now.AddSeconds(-1), Now.AddMinutes(-11), 16, 4, 20);
        GateHeartbeatStatus[] statuses =
        [
            new(0, "first-heartbeat.json", true, null, running,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)),
            new(1, "second-heartbeat.json", true, null, stalled,
                TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(11))
        ];

        var actual = CaptureConsole(() => GateStatusTextView.Print(statuses, Now));

        Assert.Equal(string.Join(Environment.NewLine,
            "Gate status:",
            "slot-0: goal=abcdef12 phase=verification-check state=running elapsed=30s target=\"First gate\" child_pid=222 heartbeat_age=1s idle_for=10s output_bytes=15 heartbeat=first-heartbeat.json verdict=RUNNING",
            "slot-1: goal=01234567 phase=verification-check state=running elapsed=30s target=\"Second gate\" child_pid=444 heartbeat_age=1s idle_for=11m output_bytes=20 heartbeat=second-heartbeat.json verdict=STALLED",
            "host: verdict=STALLED running=1 stalled=1 slots=2; covers per-goal and cohort gates alike",
            ""), actual);
    }

    [Fact]
    public void ExtractedOwner_LeavesNoPartialFileOrConsoleViewsForwarder()
    {
        var cliRoot = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.GateStatus.cs")));
        var methods = typeof(ConsoleViews).GetMethods(System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(methods, method => method.Name == "PrintGateStatus");
        Assert.DoesNotContain(Directory.EnumerateFiles(cliRoot, "*.cs"), path =>
            File.ReadAllText(path).Contains("ConsoleViews.PrintGateStatus", StringComparison.Ordinal));
    }

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
