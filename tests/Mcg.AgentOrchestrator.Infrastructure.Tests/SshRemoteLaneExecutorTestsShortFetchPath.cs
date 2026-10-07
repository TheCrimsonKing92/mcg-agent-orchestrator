using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fake owns its staging root, transport and poll events.
public sealed class SshRemoteLaneExecutorTestsShortFetchPath
{
    private const string FetchError = "scp.exe: open local \"x\": No such file or directory";

    [Xunit.Fact]
    public async Task LongTrxNameIsFetchedViaShortNameAndPublished()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var name = new string('a', 107) + ".trx";
        Assert.Equal(111, name.Length);
        fake.FetchContents[name] = "long-name payload";
        var handle = await fake.Submit();
        var status = FakeSshRemoteLaneTransport.Status("completed");
        status["trx"] = new[] { name };
        await fake.PollOnce(new(status));

        var fetch = Assert.Single(Fetches(fake));
        AssertShortDestination(fetch);
        var result = Assert.IsType<RemoteLaneResult>(handle.TryGetResult());
        Assert.Equal(new[] { Path.Combine(fetch.Directory, name) }, result.TestResultPaths);
        Assert.Equal("long-name payload", File.ReadAllText(result.TestResultPaths[0]));
        Assert.Empty(Directory.GetFiles(fetch.Directory, "*.part"));
    }

    [Xunit.Fact]
    public async Task FetchFailureReportsScpErrorAndRemovesPartialFile()
    {
        await using var fake = new FakeSshRemoteLaneTransport
        { FetchFailure = true, FetchStderr = FetchError, WritePartialFetch = true };
        var handle = await fake.Submit();
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("completed")));

        var error = Assert.Throws<RemoteLaneTransportException>(() => handle.TryGetResult());
        Assert.StartsWith("result-fetch-failed", error.Message);
        Assert.Contains("exit=1", error.Message);
        Assert.Contains(FetchError, error.Message);
        var fetch = Assert.Single(Fetches(fake));
        AssertShortDestination(fetch);
        Assert.Empty(Directory.GetFiles(fetch.Directory, "*.part"));
    }

    [Xunit.Fact]
    public async Task TwoTrxNamesHaveSeparateFetchesAndCorrectPayloads()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        fake.FetchContents["one.trx"] = "first payload";
        fake.FetchContents["two.trx"] = "second payload";
        var handle = await fake.Submit();
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("completed")));

        var fetches = Fetches(fake);
        Assert.Equal(2, fetches.Length);
        Assert.All(fetches, AssertShortDestination);
        Assert.EndsWith("/one.trx", fetches[0].Arguments[3]);
        Assert.EndsWith("/two.trx", fetches[1].Arguments[3]);
        Assert.NotEqual(fetches[0].Arguments[^1], fetches[1].Arguments[^1]);
        var result = Assert.IsType<RemoteLaneResult>(handle.TryGetResult());
        Assert.Equal(new[] { Path.Combine(fetches[0].Directory, "one.trx"),
            Path.Combine(fetches[1].Directory, "two.trx") }, result.TestResultPaths);
        Assert.Equal("first payload", File.ReadAllText(result.TestResultPaths[0]));
        Assert.Equal("second payload", File.ReadAllText(result.TestResultPaths[1]));
        Assert.Empty(Directory.GetFiles(fetches[0].Directory, "*.part"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData("stderr")]
    public async Task FailureKeepsOnlyTheDiagnosticTail(string? stderr)
    {
        await using var fake = new FakeSshRemoteLaneTransport
        { FetchFailure = true, FetchStderr = stderr == "stderr" ? "discard-stderr" + new string('s', 2048) : stderr,
            Noise = "discard-output" + new string('o', 2048) };
        var handle = await fake.Submit();
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("completed")));

        var error = Assert.Throws<RemoteLaneTransportException>(() => handle.TryGetResult());
        Assert.Equal("result-fetch-failed exit=1 timedOut=False stderr=" +
            new string(stderr == "stderr" ? 's' : 'o', 2048), error.Message);
    }

    private static FakeSshRemoteLaneTransport.Call[] Fetches(FakeSshRemoteLaneTransport fake) =>
        fake.Calls.Where(call => call.Arguments.Length >= 5 &&
            call.Arguments[3].EndsWith(".trx", StringComparison.Ordinal)).ToArray();

    private static void AssertShortDestination(FakeSshRemoteLaneTransport.Call fetch)
    {
        Assert.Equal(5, fetch.Arguments.Length);
        Assert.NotEqual(".", fetch.Arguments[^1]);
        Assert.Equal(Path.GetFileName(fetch.Arguments[^1]), fetch.Arguments[^1]);
        Assert.True(fetch.Arguments[^1].Length <= 16);
        Assert.Matches("^f-[0-9a-f]{8}\\.part$", fetch.Arguments[^1]);
    }
}
