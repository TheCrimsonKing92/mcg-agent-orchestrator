using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = ExecutorScreeningRunnerTests.ExecutorScreeningFixture;

public sealed class CliExecutorScreenCommandTests
{
    [Theory]
    [InlineData("off", "screening-off")]
    [InlineData("on", "screening-invalid")]
    public async Task RefusalDoesNotSubmit(string mode, string reason)
    {
        using var fixture = new Fixture();
        var path = WriteConfiguration(fixture, mode);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await Run(fixture, path, [fixture.Candidate], output, error));
        Assert.Equal("SCREEN refused reason=" + reason + Environment.NewLine, output.ToString());
        Assert.Empty(fixture.Executor.Requests);
        Assert.False(File.Exists(fixture.ReceiptPath));
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task DisabledRemoteLanesTakePrecedenceOverInvalidScreening()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "missing.json");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await Run(fixture, path, [], output, error));
        Assert.Equal("SCREEN refused reason=remote-lanes-disabled" + Environment.NewLine, output.ToString());
        Assert.Empty(fixture.Executor.Requests);
    }

    [Fact]
    public async Task PassWritesOneReceiptAndSecondCallFindsNoCandidate()
    {
        using var fixture = new Fixture();
        var path = WriteConfiguration(fixture, "shadow");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await Run(fixture, path, [fixture.Candidate], output, error));
        var receipt = Assert.Single(ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath));
        Assert.Equal(ExecutorScreeningVerdict.Pass, receipt.Verdict);
        Assert.Equal($"SCREEN goal=01234567 commit=aaaaaaaaa executor=laptop verdict=pass lanes=1 failing=0 excluded=0 receipt={receipt.ReceiptId}" +
            Environment.NewLine, output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(fixture, path, [fixture.Candidate], output, error));
        Assert.Equal("SCREEN idle reason=no-candidate" + Environment.NewLine, output.ToString());
        Assert.Single(fixture.Executor.Requests);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task MaximumTwoScreensBothOldestFirst()
    {
        using var fixture = new Fixture();
        var path = WriteConfiguration(fixture, "shadow");
        var older = fixture.Candidate with { GoalId = "older-goal", CommittedAt = fixture.Candidate.CommittedAt.AddHours(-1) };
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await Run(fixture, path, [fixture.Candidate, older], output, error,
            ["executor-screen", "--max-candidates", "2", "--executor", "laptop"]));
        Assert.Equal(new[] { older.GoalId, fixture.Candidate.GoalId },
            ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath).Select(receipt => receipt.GoalId));
        Assert.Equal(2, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    [InlineData("invalid")]
    public async Task InvalidCandidateBoundsAreUsageErrors(string maximum)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await Run(fixture, WriteConfiguration(fixture, "shadow"), [], output, error,
            ["executor-screen", "--max-candidates", maximum]));
        Assert.Equal("", output.ToString());
        Assert.Equal(CliCommandHelp.ExecutorScreenUsage + Environment.NewLine, error.ToString());
        Assert.Empty(fixture.Executor.Requests);
    }

    [Fact]
    public async Task UnknownExecutorIsAUsageError()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await Run(fixture, WriteConfiguration(fixture, "shadow"), [], output, error,
            ["executor-screen", "--executor", "typo"]));
        Assert.Equal("", output.ToString());
        Assert.Equal(CliCommandHelp.ExecutorScreenUsage + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task BusyExecutorAndLostClaimBothPrintIdleWithoutAReceipt()
    {
        using var fixture = new Fixture();
        var path = WriteConfiguration(fixture, "shadow");
        using var output = new StringWriter();
        using var error = new StringWriter();
        RemoteExecutorOccupancy.Claim(fixture.Root, fixture.Entry.Id, "other");
        try
        {
            Assert.Equal(0, await Run(fixture, path, [fixture.Candidate], output, error));
            Assert.Equal("SCREEN idle reason=no-idle-executor" + Environment.NewLine, output.ToString());
        }
        finally { RemoteExecutorOccupancy.Release(fixture.Root, fixture.Entry.Id, "other"); }
        output.GetStringBuilder().Clear();
        using var held = RemoteExecutorOccupancy.TryClaimExclusive(fixture.Root, fixture.Entry.Id, 1);
        Assert.NotNull(held);
        Assert.Equal(0, await Run(fixture, path, [fixture.Candidate], output, error));
        Assert.Equal("SCREEN idle reason=no-idle-executor" + Environment.NewLine, output.ToString());
        Assert.Empty(fixture.Executor.Requests);
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task UnreadableManifestCreatesIncompleteReceiptAndDoesNotStarveTheLoop()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliExecutorScreenCommand.RunAsync(["executor-screen", "--max-candidates", "2"],
            WriteConfiguration(fixture, "shadow"), () => [fixture.Candidate],
            _ => throw new IOException("fixture manifest unavailable"), (_, _, _) => fixture.Executor,
            fixture.Clock, TimeSpan.FromMilliseconds(1), fixture.Root, fixture.ReceiptPath, output, error);
        Assert.Equal(0, exit);
        Assert.Equal(ExecutorScreeningVerdict.Incomplete,
            Assert.Single(ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath)).Verdict);
        Assert.Contains("verdict=incomplete", output.ToString());
        Assert.Contains("SCREEN idle reason=no-candidate", output.ToString());
        Assert.Empty(fixture.Executor.Requests);
    }

    [Fact]
    public void HelpAcceptsDocumentedFlagsAndRejectsUnknownFlags()
    {
        CliCommandHelp.ThrowIfInvalidFlags(["executor-screen", "--max-candidates", "2", "--executor", "mcg-exec-1"]);
        Assert.ThrowsAny<Exception>(() => CliCommandHelp.ThrowIfInvalidFlags(["executor-screen", "--unknown"]));
        Assert.Contains("executor-screen", CliArgumentParser.RecognizedCommands);
        Assert.True(CliExecutorScreenCommand.IsCommand(["executor-screen"]));
    }

    private static Task<int> Run(Fixture fixture, string config, IReadOnlyList<ExecutorScreeningCandidate> candidates,
        TextWriter output, TextWriter error, string[]? args = null) => CliExecutorScreenCommand.RunAsync(
            args ?? ["executor-screen"], config, () => candidates,
            _ => (new[] { new AcceptanceTestLane("A", "FullyQualifiedName~A") }, TimeSpan.FromHours(1)),
            (_, _, prefix) =>
            {
                Assert.StartsWith(Path.Combine(fixture.Root, ".orchestrator", "executor-screening"), prefix);
                return fixture.Executor;
            }, fixture.Clock, TimeSpan.FromMilliseconds(1), fixture.Root, fixture.ReceiptPath, output, error);

    private static string WriteConfiguration(Fixture fixture, string mode)
    {
        var path = Path.Combine(fixture.Root, "remote-lanes.json");
        File.WriteAllText(path, """
            {"executors":[{"id":"laptop","transport":"ssh","runnerAlias":"runner","adminAlias":"admin",
              "remoteRepository":"C:/repo","slots":2}],"lanes":["*"],"screening":{"mode":"MODE"}}
            """.Replace("MODE", mode, StringComparison.Ordinal));
        return path;
    }
}
