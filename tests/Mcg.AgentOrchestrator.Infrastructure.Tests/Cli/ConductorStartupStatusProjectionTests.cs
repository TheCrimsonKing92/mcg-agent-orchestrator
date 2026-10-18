using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: each fixture owns its files; process identity, boot and clock are injected.
public sealed class ConductorStartupStatusProjectionTests
{
    private const int Pid = 39752;
    private static readonly DateTimeOffset Boot = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProcessStart = new(2026, 10, 13, 16, 11, 40, TimeSpan.Zero);
    private static readonly DateTimeOffset Acquired = new(2026, 10, 13, 16, 11, 46, TimeSpan.Zero);
    private static readonly DateTimeOffset Ready = new(2026, 10, 13, 16, 11, 47, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 16, 12, 19, TimeSpan.Zero);
    private static readonly DateTimeOffset Stop = new DateTimeOffset(2026, 10, 13, 15, 41, 15, TimeSpan.Zero).AddTicks(8870694);

    [Fact]
    public void Status_IncidentReplay_TransitionsFromStartingToRunningWithoutWrites()
    {
        using var fixture = new Fixture();
        fixture.Events(Stopped(), Event(Ready, "loop-ready", "LOOP_READY lock=acquired state=loaded goals=7"));
        var before = AssertStatus(fixture, "starting", Acquired, "loop-ready", Stop);
        Assert.Contains("Conductor: starting (pid 39752)", before.Human);
        Assert.Contains("Starting since 2026-10-13T16:11:46.0000000+00:00; stage=loop-ready", before.Human);
        Assert.Contains("Previous stop: 2026-10-13T15:41:15.8870694+00:00; reason=stop-file (earlier generation)", before.Human);
        Assert.DoesNotContain("Stopped since ", before.Human);
        Assert.DoesNotContain("Conductor: running", before.Human);

        fixture.Append(Event(Start, "loop-start", "LOOP_START"));
        var after = AssertStatus(fixture, "running", Start, null);
        Assert.Contains("Conductor: running (pid 39752)", after.Human);
        Assert.Contains("Running since 2026-10-13T16:12:19.0000000+00:00", after.Human);
    }

    [Fact]
    public void Status_NoReadiness_ReportsLockAcquiredWithPreviousStop()
    {
        using var fixture = new Fixture();
        fixture.Events(Stopped());
        AssertStatus(fixture, "starting", Acquired, "lock-acquired", Stop);
    }

    [Fact]
    public void Status_RotatedStop_MatchesLiveIncidentOutput()
    {
        using var fixture = new Fixture();
        var ready = Event(Ready, "loop-ready", "LOOP_READY lock=acquired state=loaded goals=7");
        fixture.Events(Stopped(), ready);
        var live = AssertStatus(fixture, "starting", Acquired, "loop-ready", Stop);
        fixture.Events(ready);
        File.WriteAllText(Path.Combine(fixture.Workspace.LogDirectory, "conduct-events-20261013154200.log"), Stopped() + "\r\n");
        var rotated = AssertStatus(fixture, "starting", Acquired, "loop-ready", Stop);
        Assert.Equal(live, rotated);
    }

    [Fact]
    public void Status_Handoff_WaitsForSuccessorStart()
    {
        using var fixture = new Fixture();
        fixture.Events(Event(new DateTimeOffset(2026, 10, 13, 16, 0, 0, TimeSpan.Zero), "loop-start", "LOOP_START"));
        AssertStatus(fixture, "starting", Acquired, "lock-acquired");
        fixture.Append(Event(Start, "loop-start", "LOOP_START"));
        AssertStatus(fixture, "running", Start, null);
    }

    [Fact]
    public void Status_ReacquiredLease_PreservesOwnStart()
    {
        using var fixture = new Fixture();
        fixture.Lock(new DateTimeOffset(2026, 10, 13, 16, 30, 0, TimeSpan.Zero));
        fixture.Events(Event(Start, "loop-start", "LOOP_START"));
        AssertStatus(fixture, "running", Start, null);
    }

    [Fact]
    public void Status_MissingEvents_ReportsStartingWithoutPrevious()
    {
        using var fixture = new Fixture();
        AssertStatus(fixture, "starting", Acquired, "lock-acquired");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Status_InactiveOrReusedPid_MatchesPidOnlyBaseline(bool running, bool same)
    {
        using var fixture = new Fixture();
        fixture.Events(Stopped());
        var result = AssertBaseline(fixture, new StubPidProbe(running, same), null, Boot);
        Assert.Contains("Conductor: stopped", result.Human);
        Assert.Contains("Stopped since 2026-10-13T15:41:15.8870694+00:00; reason=stop-file", result.Human);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1694)]
    public void Status_OwnStop_MatchesPidOnlyBaseline(int seconds)
    {
        using var fixture = new Fixture();
        var stop = Acquired.AddSeconds(seconds);
        fixture.Events(Event(stop, "loop-stop", "LOOP_STOP tick=9 reason=stop-file"));
        var result = AssertBaseline(fixture, new StubPidProbe(), Pid, Boot);
        Assert.Contains("Conductor: running (pid 39752)", result.Human);
        Assert.Contains($"Stopped since {Format(stop)}; reason=stop-file", result.Human);
        using var json = JsonDocument.Parse(result.Json);
        Assert.Equal(Pid, json.RootElement.GetProperty("payload").GetProperty("conductor").GetProperty("pid").GetInt32());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Status_EqualLifecycleTimes_PreservesFirstLogEvent(bool stopFirst, bool owned)
    {
        using var fixture = new Fixture();
        var stop = Event(Acquired, "loop-stop", "LOOP_STOP tick=9 reason=stop-file");
        var start = Event(Acquired, "loop-start", "LOOP_START");
        fixture.Events(stopFirst ? [stop, start] : [start, stop]);
        var result = AssertBaseline(fixture, new StubPidProbe(owned, owned), owned ? Pid : null, Boot);
        var state = owned ? "running (pid 39752)" : "stopped";
        Assert.Contains($"Conductor: {state}", result.Human);
        Assert.Contains(stopFirst ? $"Stopped since {Format(Acquired)}; reason=stop-file"
            : $"Running since {Format(Acquired)}", result.Human);
        Assert.DoesNotContain(stopFirst ? "Running since " : "Stopped since ", result.Human);
        using var json = JsonDocument.Parse(result.Json);
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(owned ? "running" : "stopped", payload.GetProperty("conductor").GetProperty("state").GetString());
        var since = payload.GetProperty("since");
        Assert.Equal(stopFirst ? "stopped" : "running", since.GetProperty("kind").GetString());
        Assert.Equal(Format(Acquired), since.GetProperty("at").GetString());
        Assert.Equal(stopFirst ? "stop-file" : null, since.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, since.GetProperty("previous").ValueKind);
        Assert.DoesNotContain("Previous stop:", result.Human);
    }

    [Fact]
    public void Status_HostRestart_MatchesPidOnlyBaseline()
    {
        using var fixture = new Fixture();
        fixture.Events(Stopped());
        var result = AssertBaseline(fixture, new StubPidProbe(false, false), null, Acquired);
        Assert.Contains("Conductor: stopped", result.Human);
        Assert.Contains("stopped since host restart at 2026-10-13T16:11:46.0000000+00:00", result.Human);
    }

    private static (string Human, string Json) AssertBaseline(Fixture fixture, StubPidProbe pidProbe,
        int? owner, DateTimeOffset boot)
    {
        var real = new SystemConductorLockProbe(pidProbe);
        var actual = (fixture.Status(false, real, boot), fixture.Status(true, real, boot));
        var baseline = new PidOnlyProbe(owner);
        Assert.Equal(fixture.Status(false, baseline, boot), actual.Item1);
        Assert.Equal(fixture.Status(true, baseline, boot), actual.Item2);
        Assert.True(pidProbe.IdentityChecks > 0);
        return actual;
    }

    private static (string Human, string Json) AssertStatus(Fixture fixture, string state,
        DateTimeOffset at, string? reason, DateTimeOffset? previous = null)
    {
        var pidProbe = new StubPidProbe();
        var probe = new SystemConductorLockProbe(pidProbe);
        var human = fixture.Status(false, probe, Boot);
        var jsonText = fixture.Status(true, probe, Boot);
        Assert.True(pidProbe.IdentityChecks > 0);
        using var json = JsonDocument.Parse(jsonText);
        var payload = json.RootElement.GetProperty("payload");
        var conductor = payload.GetProperty("conductor");
        Assert.Equal(state, conductor.GetProperty("state").GetString());
        Assert.Equal(Pid, conductor.GetProperty("pid").GetInt32());
        Assert.Contains($"Conductor: {state} (pid {Pid})", human);
        var since = payload.GetProperty("since");
        Assert.Equal(state, since.GetProperty("kind").GetString());
        Assert.Equal(Format(at), since.GetProperty("at").GetString());
        Assert.Equal(reason, since.GetProperty("reason").GetString());
        Assert.Contains(state == "starting" ? $"Starting since {Format(at)}; stage={reason}"
            : $"Running since {Format(at)}", human);
        var prior = since.GetProperty("previous");
        if (previous is { } stoppedAt)
        {
            Assert.Equal(new[] { "at", "reason" }, prior.EnumerateObject().Select(item => item.Name));
            Assert.Equal(Format(stoppedAt), prior.GetProperty("at").GetString());
            Assert.Equal("stop-file", prior.GetProperty("reason").GetString());
            Assert.Contains($"Previous stop: {Format(stoppedAt)}; reason=stop-file (earlier generation)", human);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, prior.ValueKind);
            Assert.DoesNotContain("Previous stop:", human);
        }
        Assert.DoesNotContain("Stopped since ", human);
        return (human, jsonText);
    }

    private static string Format(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);
    private static string Stopped() => Event(Stop, "loop-stop", "LOOP_STOP tick=9 reason=stop-file");
    private static string Event(DateTimeOffset at, string kind, string detail) =>
        JsonSerializer.Serialize(new { timestamp = at, eventKind = kind, detail });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "startup-status-" + Guid.NewGuid().ToString("N"));
        internal OrchestratorWorkspace Workspace { get; }
        private string LockPath => Path.Combine(Workspace.OrchestratorDirectory, "conduct-loop.lock");

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Directory.CreateDirectory(Workspace.OrchestratorDirectory);
            Directory.CreateDirectory(Workspace.LogDirectory);
            Lock(Acquired);
        }

        internal void Lock(DateTimeOffset acquired) =>
            File.WriteAllLines(LockPath, [Pid.ToString(CultureInfo.InvariantCulture), Format(acquired), Format(ProcessStart)]);

        internal void Events(params string[] lines) => File.WriteAllLines(Workspace.ConductEventsLogPath, lines);
        internal void Append(string line) => File.AppendAllText(Workspace.ConductEventsLogPath, line + "\r\n");

        internal string Status(bool json, IConductorLockProbe probe, DateTimeOffset boot)
        {
            var paths = Directory.GetFiles(Workspace.LogDirectory, "conduct-events*.log").Append(LockPath).ToArray();
            var before = paths.ToDictionary(path => path, File.ReadAllBytes);
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            var exit = CliConductorCommand.Run(json ? ["conductor", "status", "--json"] : ["conductor", "status"],
                Workspace, lockProbe: probe, bootTime: () => boot, mainCommit: () => "abc123",
                output: output, error: error, time: new FixedTimeProvider());
            Assert.Equal("", error.ToString());
            Assert.Equal(0, exit);
            foreach (var path in paths) Assert.Equal(before[path], File.ReadAllBytes(path));
            Assert.Equal(paths.Order(), Directory.GetFiles(Workspace.LogDirectory, "conduct-events*.log").Append(LockPath).Order());
            return output.ToString();
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class PidOnlyProbe(int? pid) : IConductorLockProbe
    {
        public int? ActiveOwnerPid(OrchestratorWorkspace workspace) => pid;
    }

    private sealed class StubPidProbe(bool running = true, bool same = true) : IConductLockPidProbe
    {
        internal int IdentityChecks { get; private set; }
        public bool IsRunning(int processId)
        {
            Assert.Equal(Pid, processId);
            return running;
        }

        public bool IsSameProcess(int processId, DateTimeOffset processStartedAt)
        {
            Assert.Equal(Pid, processId);
            Assert.Equal(ProcessStart, processStartedAt);
            IdentityChecks++;
            return running && same;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 13, 16, 40, 0, TimeSpan.Zero);
    }
}
