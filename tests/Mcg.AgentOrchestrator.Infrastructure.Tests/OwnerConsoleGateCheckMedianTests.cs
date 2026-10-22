using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: immutable fixtures and explicit timestamps; no process or shared state.
public sealed class OwnerConsoleGateCheckMedianTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");
    private static readonly OwnerConsoleGateManifestLane[] Manifest = [new("A", 300)];
    private static readonly OwnerConsoleGateDuration[] History =
    [
        new("core tests", 90, Now.AddMinutes(-3)),
        new("core tests", 100, Now.AddMinutes(-2)),
        new("core tests", 110, Now.AddMinutes(-1))
    ];

    [Fact]
    public void NonManifestCheckUsesHistoryMedianAndFormatter()
    {
        var gate = Build([Progress("verification-check", "core tests")]);
        var row = Assert.Single(gate.Lanes, row => row.Name == "core tests");
        Assert.Equal(100d, row.MedianSeconds);
        Assert.False(row.IsEstimate);
        Assert.False(row.IsManifestLane);
        Assert.Contains("/ 100s", OwnerConsoleGateFormatter.LaneLine(row));
        Assert.DoesNotContain("?", OwnerConsoleGateFormatter.LaneLine(row));
        var manifestRow = Assert.Single(gate.Lanes, row => row.Name == "A");
        Assert.Equal(300d, manifestRow.MedianSeconds);
        Assert.True(manifestRow.IsEstimate);
    }

    [Fact]
    public void NoHistoryKeepsUnknownMedianAndFormatter()
    {
        var gate = Build([Progress("verification-check", "cli tests")]);
        var row = Assert.Single(gate.Lanes, row => row.Name == "cli tests");
        Assert.Null(row.MedianSeconds);
        Assert.True(row.IsEstimate);
        Assert.False(row.IsManifestLane);
        Assert.Contains("?", OwnerConsoleGateFormatter.LaneLine(row));
        Assert.Equal(0, gate.Fraction); // No gate wall time has elapsed; unknown checks add no model.
        Assert.Equal(300, gate.RemainingSeconds);
        Assert.Equal(0, gate.DoneCount);
        Assert.Equal(1, gate.TotalCount);
    }

    [Theory]
    [InlineData(false, false, false, 0, 400, 0)]
    [InlineData(true, false, false, 0, 400, 0)]
    [InlineData(true, true, false, 0, 300, 0)]
    [InlineData(true, true, true, 1, 0, 1)]
    public void FractionAndRemainingCountHistoryButCountsStayManifestOnly(
        bool unknownCheck, bool coreDone, bool manifestDone, double fraction, int remaining, int doneCount)
    {
        var events = new List<OwnerConductEvent>
        {
            Progress("verification-check", "infrastructure tests: A"),
            Progress("verification-check", "core tests")
        };
        if (unknownCheck) events.Add(Progress("verification-check", "cli tests"));
        if (coreDone) events.Add(Progress("check-complete", "core tests", 95000, " outcome=passed failed=0"));
        if (manifestDone) events.Add(Progress("check-complete", "infrastructure tests: A", 300000, " outcome=passed failed=0"));

        var gate = Build(events);

        // Completion elapsed_ms does not advance the injected gate clock.
        Assert.Equal(fraction, gate.Fraction, precision: 6);
        Assert.Equal(remaining, gate.RemainingSeconds);
        Assert.Equal(doneCount, gate.DoneCount);
        Assert.Equal(1, gate.TotalCount);
        Assert.Equal(coreDone ? OwnerConsoleGateLaneState.Done : OwnerConsoleGateLaneState.Running,
            Assert.Single(gate.Lanes, row => row.Name == "core tests").State);
        if (unknownCheck) Assert.Null(Assert.Single(gate.Lanes, row => row.Name == "cli tests").MedianSeconds);
    }

    [Fact]
    public void QueuedManifestAndRunningCheckBothExtendRemaining()
    {
        var gate = Build([Progress("verification-check", "core tests")]);
        Assert.Equal(OwnerConsoleGateLaneState.Queued, Assert.Single(gate.Lanes, row => row.Name == "A").State);
        Assert.Equal(OwnerConsoleGateLaneState.Running, Assert.Single(gate.Lanes, row => row.Name == "core tests").State);
        Assert.Equal(400, gate.RemainingSeconds);
        Assert.Equal(0, gate.Fraction); // Queued and running work at zero gate elapsed stays at zero.
        Assert.Equal(0, gate.DoneCount);
        Assert.Equal(1, gate.TotalCount);
    }

    [Fact]
    public void NonManifestMedianUsesNewestTenSamplesAndEvenMidpoint()
    {
        var history = Enumerable.Range(1, 10).Select(value => new OwnerConsoleGateDuration("core tests", value, Now.AddMinutes(-value)))
            .Append(new("core tests", 1000, Now.AddMinutes(-11)));
        var gate = OwnerConsoleGateProgress.Build(Now, [Progress("verification-check", "core tests")], Manifest, history);
        Assert.Equal(5.5, Assert.Single(gate.Lanes, row => row.Name == "core tests").MedianSeconds);
    }

    private static OwnerConsoleGateProgress Build(IEnumerable<OwnerConductEvent> events) =>
        OwnerConsoleGateProgress.Build(Now, events, Manifest, History);

    private static OwnerConductEvent Progress(string phase, string target, int elapsed = 0, string extra = "") =>
        OwnerConsoleGateProgress.ParseEvent(JsonSerializer.Serialize(new
        {
            timestamp = Now, eventKind = "gate-progress", goalId = "776a132e",
            detail = $"PHASE_PROGRESS goal=776a132e phase={phase} target=\"{target}\" elapsed_ms={elapsed}{extra}"
        }))!;
}
