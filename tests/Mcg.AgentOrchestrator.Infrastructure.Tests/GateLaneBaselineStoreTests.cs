using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: store paths and their mutexes are unique per case.
public sealed class GateLaneBaselineStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lane-baseline-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryAppend_PassingRuns_BoundsIndependentLaneHistory()
    {
        var tests = Enumerable.Range(0, 160).ToDictionary(index => "Test" + index, index => 1000d + index);
        for (var index = 0; index < 6; index++)
            Assert.Null(GateLaneBaselineStore.TryAppend(_root, "Alpha", new(DateTimeOffset.UnixEpoch.AddMinutes(index), 60000 + index, tests), true));
        Assert.Null(GateLaneBaselineStore.TryAppend(_root, "Beta", new(DateTimeOffset.UnixEpoch, 70000, tests), true));
        var alpha = GateLaneBaselineStore.ReadHistory(_root, "Alpha");
        Assert.Equal(5, alpha.Count);
        Assert.Equal(Enumerable.Range(1, 5).Select(index => 60000d + index), alpha.Select(run => run.WallMs));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(1), alpha[0].RecordedAt);
        Assert.All(alpha, run =>
        {
            Assert.Equal(150, run.Tests.Count);
            Assert.Equal(1010, run.Tests.Values.Min());
        });
        Assert.Equal(70000, Assert.Single(GateLaneBaselineStore.ReadHistory(_root, "Beta")).WallMs);
        var path = GateLaneBaselineStore.ResolveStorePath(_root);
        Assert.Equal(Path.Combine(HostLedgerStateDirectory.Resolve(_root), "gate-lane-test-baselines.json"), path);
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void TryAppend_FailedRun_DoesNotCreateOrChangeHistory()
    {
        var run = new GateLaneBaselineStore.Run(DateTimeOffset.UnixEpoch, 60000, new Dictionary<string, double>());
        Assert.Null(GateLaneBaselineStore.TryAppend(_root, "Alpha", run, false));
        Assert.False(File.Exists(GateLaneBaselineStore.ResolveStorePath(_root)));
        Assert.Null(GateLaneBaselineStore.TryAppend(_root, "Alpha", run, true));
        var original = File.ReadAllText(GateLaneBaselineStore.ResolveStorePath(_root));
        Assert.Null(GateLaneBaselineStore.TryAppend(_root, "Alpha", run with { WallMs = 120000 }, false));
        Assert.Equal(original, File.ReadAllText(GateLaneBaselineStore.ResolveStorePath(_root)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-json")]
    [InlineData("{\"version\":99,\"lanes\":{}}")]
    [InlineData("{\"version\":1,\"lanes\":{\"Alpha\":null}}")]
    public void ReadHistory_MissingOrCorruptStore_ReturnsEmpty(string? content)
    {
        var path = GateLaneBaselineStore.ResolveStorePath(_root);
        if (content is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        Assert.Empty(GateLaneBaselineStore.ReadHistory(_root, "Alpha"));
        Assert.Null(GateLaneBaselineStore.TryAppend(_root, "Alpha",
            new(DateTimeOffset.UnixEpoch, 60000, new Dictionary<string, double>()), true));
        Assert.Single(GateLaneBaselineStore.ReadHistory(_root, "Alpha"));
    }

    [Fact]
    public void TryAppend_UnreadableExistingStore_PreservesAllLaneHistory()
    {
        var run = new GateLaneBaselineStore.Run(DateTimeOffset.UnixEpoch, 60000,
            new Dictionary<string, double> { ["Git.Commit"] = 4000 });
        Assert.Null(GateLaneBaselineStore.TryAppend(_root, "Alpha", run, true));
        Assert.Null(GateLaneBaselineStore.TryAppend(_root, "Beta", run, true));
        var path = GateLaneBaselineStore.ResolveStorePath(_root);
        var original = File.ReadAllBytes(path);
        // Deny reads while permitting replacement: without the read guard, the
        // old implementation successfully overwrites both lanes with only Gamma.
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Write | FileShare.Delete))
        {
            Assert.Throws<IOException>(() => File.ReadAllText(path));
            Assert.Empty(GateLaneBaselineStore.ReadHistory(_root, "Alpha"));
            Assert.Equal("baseline-write:IOException", GateLaneBaselineStore.TryAppend(_root, "Gamma", run, true));
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(GateLaneBaselineStore.ReadHistory(_root, "Alpha"));
        Assert.Single(GateLaneBaselineStore.ReadHistory(_root, "Beta"));
        Assert.Empty(GateLaneBaselineStore.ReadHistory(_root, "Gamma"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void Record_ThrowingAppender_LeavesVerdictAndPassingBaselineIntact()
    {
        var calls = 0;
        var result = new AcceptanceCheckResult("Alpha", true, 0, null);
        var slowdown = GateLaneReceiptLedger.Record(_root, null, "Alpha", result, TimeSpan.FromSeconds(60), DateTimeOffset.UnixEpoch,
            (_, _) => { calls++; throw new IOException("injected receipt failure"); });
        Assert.Equal(1, calls);
        Assert.Equal("insufficient-history", slowdown!.Status);
        Assert.True(result.Passed);
        Assert.Single(GateLaneBaselineStore.ReadHistory(_root, "Alpha"));
    }

    [Fact]
    public void Record_SlowdownAppendFails_StillWritesReceiptAndUpdatesBaseline()
    {
        var tests = new Dictionary<string, double> { ["Git.Commit"] = 4000 };
        for (var index = 0; index < 3; index++)
            Assert.Null(GateLaneBaselineStore.TryAppend(_root, "Alpha", new(DateTimeOffset.UnixEpoch, 60000, tests), true));
        var trx = Path.Combine(_root, "duration.trx");
        File.WriteAllText(trx, """
            <TestRun><Results><UnitTestResult testName="Git.Commit" outcome="Passed" duration="00:00:12" /></Results></TestRun>
            """);
        string? receipt = null;
        var calls = 0;
        var result = GateLaneReceiptLedger.Record(_root, "goal", "Alpha",
            new("Alpha", true, 0, null, TestResultPaths: [trx]), TimeSpan.FromSeconds(120), DateTimeOffset.UnixEpoch,
            (path, row) =>
            {
                calls++;
                if (path.EndsWith(GateLaneSlowdownDiff.FileName, StringComparison.Ordinal)) throw new IOException("injected slowdown write failure");
                receipt = row;
            });
        Assert.Equal(2, calls);
        Assert.Equal("slow", result!.Status);
        Assert.NotNull(receipt);
        using var document = System.Text.Json.JsonDocument.Parse(receipt);
        Assert.Equal("slow", document.RootElement.GetProperty("slowdown_status").GetString());
        Assert.Equal(26, document.RootElement.EnumerateObject().Count());
        Assert.Equal(2, document.RootElement.GetProperty("slowdown_ratio").GetDouble());
        Assert.Equal("slowdown-write:IOException", result.UnavailableReason);
        Assert.Equal(4, GateLaneBaselineStore.ReadHistory(_root, "Alpha").Count);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
