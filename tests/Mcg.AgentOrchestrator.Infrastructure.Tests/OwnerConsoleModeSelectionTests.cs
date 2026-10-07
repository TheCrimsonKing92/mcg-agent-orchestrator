using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: routing delegates replace terminal I/O; no process globals are mutated.
public sealed class OwnerConsoleModeSelectionTests
{
    [Theory]
    [InlineData(true, false, false, "plain")]
    [InlineData(false, true, false, "plain")]
    [InlineData(false, false, true, "plain")]
    [InlineData(false, false, false, "full-screen")]
    public async Task RoutesPlainFlagRedirectedStreamsAndInteractiveTerminal(
        bool plain, bool inputRedirected, bool outputRedirected, string expected)
    {
        var calls = new List<string>();
        var runners = new OwnerConsoleModeRunners(
            _ => { calls.Add("plain"); return Task.FromResult(11); },
            _ => { calls.Add("full-screen"); return Task.FromResult(22); });

        var result = await OwnerConsoleModeSelector.RunAsync(plain ? ["console", "--plain"] : ["console"],
            inputRedirected, outputRedirected, runners);

        Assert.Equal([expected], calls);
        Assert.Equal(expected == "plain" ? 11 : 22, result);
    }

    [Fact]
    public void PlainFlagPassesStartupValidationAndIsDocumented()
    {
        CliCommandHelp.ThrowIfInvalidFlags(["console", "--plain"]);
        Assert.True(CliCommandHelp.IsCommandSpecificHelp(["console", "--help"]));
    }

    [Fact]
    public void UnknownConsoleFlagIsRejected() =>
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["console", "--unknown"]));
}
