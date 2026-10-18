using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique temporary files, AsyncLocal store scopes and an injected console clock.
public sealed class OwnerConsoleGateCheckMedianScenarioTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "check-median-scenario-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RealHistoryAndCheckCompletionDriveMedianFractionAndRemaining()
    {
        Directory.CreateDirectory(_root);
        var log = Path.Combine(_root, "conduct-events.log");
        var manifest = Path.Combine(_root, "acceptance-manifest.json");
        var history = AcceptanceLaneDurationStore.ResolveStorePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        File.WriteAllText(manifest, """{"engine":{"infrastructureTestLanes":[{"name":"A","estimatedSerialSeconds":300}]}}""");
        File.WriteAllLines(history, new[] { 90, 100, 110 }.Select(seconds => JsonSerializer.Serialize(new
        {
            version = 1, laneName = "core tests", filterHash = "check",
            durationSeconds = seconds, recordedAt = Now.AddSeconds(-seconds)
        })));
        File.WriteAllLines(log,
        [
            Progress("verification-check", "infrastructure tests: A"),
            Progress("verification-check", "core tests")
        ]);
        var loader = new OwnerConsoleGateProgressLoader(log, manifest, history, new OwnerConsoleTestClock { Now = Now });

        var running = Load(loader);

        Assert.True(running.Live);
        var core = Row(running, "core tests");
        Assert.Equal(OwnerConsoleGateLaneState.Running, core.State);
        Assert.Equal(100d, core.MedianSeconds);
        Assert.False(core.IsEstimate);
        Assert.False(core.IsManifestLane);
        Assert.Contains("/ 100s", OwnerConsoleGateFormatter.LaneLine(core));
        Assert.DoesNotContain("?", OwnerConsoleGateFormatter.LaneLine(core));
        Assert.Equal(0, running.Fraction);
        Assert.Equal(400, running.RemainingSeconds);
        AssertManifestCounts(running);

        File.AppendAllLines(log, [Progress("check-complete", "core tests", 95000, " outcome=passed failed=0")]);
        var completed = Load(loader);
        Assert.Equal(OwnerConsoleGateLaneState.Done, Row(completed, "core tests").State);
        Assert.Equal("passed", Row(completed, "core tests").Outcome);
        Assert.Equal(0.25, completed.Fraction, precision: 6);
        Assert.Equal(300, completed.RemainingSeconds);
        AssertManifestCounts(completed);

        File.AppendAllLines(log, [Progress("verification-check", "cli tests")]);
        var unknown = Load(loader);
        Assert.Null(Row(unknown, "cli tests").MedianSeconds);
        Assert.Contains("?", OwnerConsoleGateFormatter.LaneLine(Row(unknown, "cli tests")));
        Assert.Equal(0.25, unknown.Fraction, precision: 6);
        Assert.Equal(300, unknown.RemainingSeconds);
        AssertManifestCounts(unknown);

        var originalHistory = File.ReadAllText(history);
        using (AcceptanceLaneDurationStore.PushRecordingScope(_root))
        {
            AcceptanceLaneDurationStore.RecordCheck("cli tests", false, TimeSpan.FromSeconds(30));
            AcceptanceLaneDurationStore.Flush();
        }
        Assert.Equal(originalHistory, File.ReadAllText(history));
        Assert.Null(Row(Load(loader), "cli tests").MedianSeconds);

        using (AcceptanceLaneDurationStore.PushRecordingScope(_root))
        {
            AcceptanceLaneDurationStore.RecordCheck("cli tests", true, TimeSpan.FromSeconds(30));
            AcceptanceLaneDurationStore.Flush();
        }
        var rows = File.ReadAllLines(history);
        Assert.Equal(4, rows.Length);
        using var added = JsonDocument.Parse(rows[3]);
        Assert.Equal("cli tests", added.RootElement.GetProperty("laneName").GetString());
        Assert.Equal(30d, added.RootElement.GetProperty("durationSeconds").GetDouble());
        Assert.Equal("check", added.RootElement.GetProperty("filterHash").GetString());
        var learned = Load(loader);
        var cli = Row(learned, "cli tests");
        Assert.Equal(30d, cli.MedianSeconds);
        Assert.False(cli.IsEstimate);
        Assert.Contains("/ 30s", OwnerConsoleGateFormatter.LaneLine(cli));
        Assert.DoesNotContain("?", OwnerConsoleGateFormatter.LaneLine(cli));
        Assert.Equal(100d / 430, learned.Fraction, precision: 6);
        Assert.Equal(330, learned.RemainingSeconds);
        AssertManifestCounts(learned);
    }

    private static OwnerConsoleGateProgress Load(OwnerConsoleGateProgressLoader loader) =>
        Assert.IsType<OwnerConsoleGateProgress>(loader.Load(new Dictionary<string, string>()));

    private static OwnerConsoleGateLane Row(OwnerConsoleGateProgress gate, string name) =>
        Assert.Single(gate.Lanes, row => row.Name == name);

    private static void AssertManifestCounts(OwnerConsoleGateProgress gate)
    {
        Assert.Equal(0, gate.DoneCount);
        Assert.Equal(1, gate.TotalCount);
    }

    private static string Progress(string phase, string target, int elapsed = 0, string extra = "") =>
        JsonSerializer.Serialize(new
        {
            timestamp = Now, eventKind = "gate-progress", goalId = "776a132e",
            detail = $"PHASE_PROGRESS goal=776a132e phase={phase} elapsed_ms={elapsed} target=\"{target}\" child_pid=unknown output_bytes=0 heartbeat=x{extra}"
        });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
