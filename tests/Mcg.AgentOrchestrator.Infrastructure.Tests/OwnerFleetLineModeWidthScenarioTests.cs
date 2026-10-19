using Mcg.AgentOrchestrator.App.OwnerConsole;
using static OwnerFleetFullScreenWidthScenarioTests;

// Unique filesystem roots and per-session fakes; safe to run in parallel.
public sealed class OwnerFleetLineModeWidthScenarioTests
{
    [Theory]
    [InlineData(100, 100)]
    [InlineData(null, 60)]
    [InlineData(20, 40)]
    public async Task FleetUsesTerminalWidthAndRealLedger(int? available, int expected)
    {
        using var fixture = new FleetFixture();
        var harness = new OwnerConsoleHarness();
        var reads = 0;
        using var session = new OwnerConsoleSession(harness.State, harness.Questions, harness.Answers,
            harness.Liveness, harness.Digest, harness.Tail, harness.Output, harness.Clock,
            fleet: () => OwnerFleetViewModelBuilder.Load(fixture.Workspace, harness.Liveness, new FleetFixture.Clock()),
            terminalWidth: () => { reads++; return available; });

        Assert.True(await session.HandleCommandAsync("fleet", CancellationToken.None));

        Assert.Equal(1, reads);
        FleetFixture.AssertDiagram(harness.Output.Text.TrimEnd('\r', '\n').Replace("\r\n", "\n"), expected);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    public void TerminalAdapterValidatesRead(int value, int? expected) =>
        Assert.Equal(expected, OwnerConsoleVerb.TerminalWidth(() => value));

    [Fact]
    public void TerminalAdapterHandlesUnavailableConsole()
    {
        Assert.Null(OwnerConsoleVerb.TerminalWidth(() => throw new IOException("redirected")));
        Assert.Null(OwnerConsoleVerb.TerminalWidth(() => throw new ArgumentOutOfRangeException("width")));
    }
}
