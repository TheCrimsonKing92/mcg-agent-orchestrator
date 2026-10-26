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

    [Fact]
    public void TraceFlagPassesStartupValidationAndResolvesExplicitDestination()
    {
        var path = Path.Combine(Path.GetTempPath(), "owner-input.json");
        CliCommandHelp.ThrowIfInvalidFlags(["console", "--input-trace", path]);
        Assert.Equal(Path.GetFullPath(path), OwnerConsoleInputTraceOptions.Parse(["console", "--input-trace", path], false, false));
        Assert.Null(OwnerConsoleInputTraceOptions.Parse(["console"], false, false));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void TraceCannotFallBackToPlainMode(bool plain, bool inputRedirected, bool outputRedirected)
    {
        string[] args = plain ? ["console", "--plain", "--input-trace", "trace.json"] : ["console", "--input-trace", "trace.json"];
        Assert.Throws<ArgumentException>(() => OwnerConsoleInputTraceOptions.Parse(args, inputRedirected, outputRedirected));
    }

    [Theory]
    [InlineData("--input-trace")]
    [InlineData("--input-trace", "")]
    [InlineData("--input-trace", "--plain")]
    [InlineData("--input-trace", "a.json", "--input-trace", "b.json")]
    [InlineData("--unknown")]
    public void MalformedTraceArgumentsAreRejected(params string[] options) =>
        Assert.Throws<ArgumentException>(() => OwnerConsoleInputTraceOptions.Parse(["console", .. options], false, false));

    [Fact]
    public void DestinationMustBeNewAndWritableBeforeOpeningUi()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "trace.json");
            File.WriteAllText(path, "prior receipt");
            Assert.Throws<IOException>(() => OwnerConsoleInputTraceOptions.OpenDestination(path));
            Assert.Equal("prior receipt", File.ReadAllText(path));
            Assert.Throws<DirectoryNotFoundException>(() => OwnerConsoleInputTraceOptions.OpenDestination(Path.Combine(root, "missing", "trace.json")));
            using var stream = OwnerConsoleInputTraceOptions.OpenDestination(Path.Combine(root, "new.json"));
            Assert.True(stream.CanWrite);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
