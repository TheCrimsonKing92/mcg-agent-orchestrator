using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Pure report fixtures; no shared state or terminal initialization.
public sealed class OwnerFleetDiagramTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(100)]
    public void RendersHostAndBothExecutorBoxesWithinWidth(int width)
    {
        var model = OwnerFleetViewModelBuilder.Build(Fixture(), true, TimeZoneInfo.Utc);
        var text = OwnerFleetDiagram.Render(model, width);
        var alpha = text[text.IndexOf("Executor alpha", StringComparison.Ordinal)..text.IndexOf('╔')];
        var beta = text[text.IndexOf('╔')..];

        Assert.Equal(3, text.Count(ch => ch is '┌' or '╔'));
        Assert.Contains("Orchestrator host", text);
        Assert.Contains("conductor: running", text);
        Assert.Contains("reachability: reachable", alpha);
        Assert.Equal(2, CountCells(alpha));
        Assert.Contains("lane median 20.0s n=3", alpha);
        Assert.Contains("solo 30.0s conc 15.0s", alpha);
        Assert.Contains("last: accepted 2026-10-16 12:01 +00:00", alpha);
        Assert.Contains("Executor beta", beta);
        Assert.Contains("reachability: UNREACHABLE", beta);
        Assert.Contains("ssh-timeout", beta);
        Assert.Contains("last: none", beta);
        Assert.Contains("lanes: no accepted remote runs", beta);
        Assert.Equal(1, CountCells(beta));
        Assert.DoesNotContain('\t', text);
        Assert.All(text.Split('\n'), line => Assert.True(line.Length <= width, line));
    }

    [Fact]
    public void BuilderPreservesReportFieldsAndConvertsTimes()
    {
        var report = Fixture();
        var zone = TimeZoneInfo.CreateCustomTimeZone("fixture", TimeSpan.FromHours(-5), "fixture", "fixture");
        var model = OwnerFleetViewModelBuilder.Build(report, false, zone);
        Assert.False(model.Host.ConductorRunning);
        Assert.Equal(2, model.Host.ExecutorCount);
        var executor = model.Executors[0];
        Assert.Equal("alpha", executor.Id);
        Assert.Equal(OwnerFleetReachability.Reachable, executor.Reachability);
        Assert.Equal(2, executor.ConfiguredSlots);
        Assert.Equal(2, executor.PeakConcurrentAttempts);
        Assert.Equal(1d, executor.AcceptedShare);
        Assert.Equal("accepted", executor.LastOutcome);
        Assert.Equal(TimeSpan.FromHours(-5), executor.LastOutcomeAt!.Value.Offset);
        Assert.Equal(report.Executors[0].RecentAttempts[0].ObservedAt, executor.LastOutcomeAt);
        Assert.Equal(report.Executors[0].LastProbe!.ObservedAt, executor.ProbeAt);
        Assert.Equal(report.Executors[0].Lanes, executor.Lanes);
        Assert.Equal(new[] { "ssh-timeout" }, model.Executors[1].ProbeReasons);
        Assert.NotEqual(typeof(OwnerFleetViewModelBuilder), typeof(OwnerFleetDiagram));
    }

    [Fact]
    public void NoConfiguredExecutors_RendersHostBoxOnly()
    {
        var model = OwnerFleetViewModelBuilder.Build(RemoteExecutorReport.Build([], [], []), false, TimeZoneInfo.Utc);
        var text = OwnerFleetDiagram.Render(model);
        Assert.Single(text.Where(ch => ch is '┌' or '╔'));
        Assert.Contains("no executors configured", text);
        Assert.Contains("conductor: stopped", text);
    }

    [Fact]
    public void MissingProbeAndNullAcceptedShare_ShowUnknownAndNotApplicable()
    {
        var model = OwnerFleetViewModelBuilder.Build(RemoteExecutorReport.Build(["unprobed"], [], [],
            configuredSlots: new Dictionary<string, int> { ["unprobed"] = 1 }), true, TimeZoneInfo.Utc);
        var executor = Assert.Single(model.Executors);
        Assert.Equal(OwnerFleetReachability.Unknown, executor.Reachability);
        Assert.Null(executor.ProbeAt);
        Assert.Null(executor.AcceptedShare);
        Assert.Null(executor.LastOutcomeAt);
        var text = OwnerFleetDiagram.Render(model);
        Assert.Contains("reachability: unknown (no probe)", text);
        Assert.Contains("accepted: n/a", text);
        Assert.Contains("[ ]", text);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(40)]
    public void FitsWidthAndPreservesAllSlotCells(int width)
    {
        var report = Fixture();
        var row = report.Executors[1] with
        {
            ExecutorId = new string('x', 200), ConfiguredSlots = 16,
            LastProbe = report.Executors[1].LastProbe! with { Reasons = [new string('r', 200) + "\n\t\u001b"] }
        };
        var model = OwnerFleetViewModelBuilder.Build(report with { Executors = [row] }, false, TimeZoneInfo.Utc);
        var text = OwnerFleetDiagram.Render(model, width);
        Assert.Equal(16, CountCells(text));
        Assert.Contains('…', text);
        Assert.DoesNotContain('\t', text);
        Assert.DoesNotContain('\u001b', text);
        Assert.All(text.Split('\n'), line => Assert.True(line.Length <= width, line));
    }

    [Fact]
    public void LedgerOnlyExecutorShowsUnconfiguredSlotsAndUnreadableCounts()
    {
        var report = Fixture();
        var row = report.Executors[0] with { ConfiguredSlots = null };
        var model = OwnerFleetViewModelBuilder.Build(new([row], 2, 3), true, TimeZoneInfo.Utc);
        var text = OwnerFleetDiagram.Render(model);
        Assert.Contains("slots: not configured", text);
        Assert.Contains("unreadable ledger lines: outcomes 2", text);
        Assert.Contains("unreadable ledger lines: probes 3", text);
        Assert.DoesNotContain("no executors configured", text);
    }

    private static int CountCells(string text) => text.Split("[ ]", StringSplitOptions.None).Length - 1;

    private static RemoteExecutorReport Fixture()
    {
        var at = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        return RemoteExecutorReport.Build(["alpha", "beta"],
        [
            new(at.AddSeconds(20), "alpha", "a", "lane", "accepted", null, 10, at),
            new(at.AddSeconds(30), "alpha", "b", "lane", "accepted", null, 20, at.AddSeconds(10)),
            new(at.AddSeconds(90), "alpha", "c", "lane", "accepted", null, 30, at.AddSeconds(60))
        ],
        [
            new(at, "alpha", true, 0, false, "Ready", true, [], ""),
            new(at, "beta", false, 1, true, null, null, ["ssh-timeout"], "")
        ], configuredSlots: new Dictionary<string, int> { ["alpha"] = 2, ["beta"] = 1 });
    }
}
