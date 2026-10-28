using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.BaseBuildCachePhaseTiming;

// Parallel-safe: operation-local clocks, async-local culture and private cache/artifact trees.
public sealed class CachePhaseTimingTests : IDisposable
{
    private const string Shared = "src/Shared/Shared.csproj";
    private const string Leaf = "src/Leaf/Leaf.csproj";
    private const string Consumer = "tests/Consumer/Consumer.csproj";
    private const string MainSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cache-phase-timing", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Format_NestedRestore_ReportsExclusiveTimeAndRawResidual()
    {
        var clock = new Clock();
        var timing = new BaseBuildCachePhaseTiming(clock);
        using (timing.Measure(Phase.IdentityResolution)) clock.Advance(20);
        clock.Advance(10);
        using (timing.Measure(Phase.RunBuildCommands))
        {
            clock.Advance(20);
            using (timing.Measure(Phase.CacheRestoreCopy)) clock.Advance(15);
            clock.Advance(15);
        }
        using (timing.Measure(Phase.IdentityRevalidation)) clock.Advance(10);
        using (timing.Measure(Phase.CacheCapturePublish)) clock.Advance(5);
        clock.Advance(15);
        timing.Stop();
        var receipt = timing.Format();
        var fields = Fields(receipt);
        Assert.Equal(110, timing.ElapsedMilliseconds);
        AssertPhase(fields, "identity_resolution", "20", "1", "measured");
        AssertPhase(fields, "identity_revalidation", "10", "1", "measured");
        AssertPhase(fields, "run_build_commands", "35", "1", "measured");
        AssertPhase(fields, "cache_restore_copy", "15", "1", "measured");
        AssertPhase(fields, "cache_capture_publish", "5", "1", "measured");
        Assert.Equal("25", fields["build_phase_residual_ms"]);
        Assert.Equal("complete", fields["build_phase_accounting_status"]);
        Assert.Equal("managed_check_exclusive_of_cache", fields["run_build_commands_scope"]);
        clock.Advance(100);
        Assert.Equal(receipt, timing.Format());
    }

    [Fact]
    public void Format_FractionalIntervals_AccumulatesBeforeInvariantConversion()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var clock = new Clock();
            var timing = new BaseBuildCachePhaseTiming(clock);
            using (timing.Measure(Phase.RunBuildCommands)) clock.Advance(0.75);
            using (timing.Measure(Phase.RunBuildCommands)) clock.Advance(0.75);
            using (timing.Measure(Phase.IdentityResolution)) clock.Advance(0.75);
            clock.Advance(0.75);
            timing.Stop();
            var fields = Fields(timing.Format());
            Assert.Equal(3, timing.ElapsedMilliseconds);
            AssertPhase(fields, "run_build_commands", "1", "2", "measured");
            AssertPhase(fields, "identity_resolution", "0", "1", "measured");
            AssertPhase(fields, "cache_restore_copy", "0", "0", "not_run");
            Assert.Equal("0", fields["build_phase_residual_ms"]);
            Assert.Equal("complete", fields["build_phase_accounting_status"]);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void Format_OpenInterval_LeavesUnknownWorkInResidual()
    {
        var clock = new Clock();
        var timing = new BaseBuildCachePhaseTiming(clock);
        using (timing.Measure(Phase.IdentityResolution)) clock.Advance(2);
        using var open = timing.Measure(Phase.RunBuildCommands);
        clock.Advance(8);
        var fields = Fields(timing.Format());
        AssertPhase(fields, "run_build_commands", "unknown", "1", "unknown");
        Assert.Equal("8", fields["build_phase_residual_ms"]);
        Assert.Equal("partial", fields["build_phase_accounting_status"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Format_InvalidIntervals_ReportsUnknownInsteadOfClamping(bool overlap)
    {
        var clock = new Clock();
        var timing = new BaseBuildCachePhaseTiming(clock);
        using (timing.Measure(Phase.IdentityResolution))
        {
            clock.Advance(5);
            if (overlap)
                using (timing.Measure(Phase.CacheCapturePublish)) clock.Advance(5);
            else clock.Advance(-6);
        }
        clock.Advance(10);
        timing.Stop();
        var fields = Fields(timing.Format());
        AssertPhase(fields, "identity_resolution", "unknown", "1", "unknown");
        Assert.Equal("unknown", fields["build_phase_residual_ms"]);
        Assert.Equal("partial", fields["build_phase_accounting_status"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EnsureAsync_CachePaths_PreservesCommandsAndAttemptCounts(bool warm, bool loseEntry)
    {
        var clock = new Clock { TickOnRead = true };
        var cache = Cache();
        var identity = new DotnetBaseBuildCacheIdentity("fixture");
        if (warm) SeedCache(cache, identity);
        var phase = Create();
        var environment = EnvironmentFor();
        var invocations = 0;
        var commands = new List<string[]>();
        var callbacks = 0;
        var resolutions = 0;
        var hooks = new AcceptanceDotnetBuildPhaseHooks((args, _, prepare, supplied) =>
        {
            invocations++;
            Assert.Same(environment, supplied);
            clock.Advance(20);
            if (prepare is not null)
            {
                callbacks++;
                if (loseEntry)
                    File.Delete(Directory.EnumerateFiles(Path.Combine(_root, "cache"), "manifest.json", SearchOption.AllDirectories).Single());
                prepare(environment);
                // A managed retry may prepare again; the already-restored guard owns non-entry.
                prepare(environment);
                Assert.Equal("seed", File.ReadAllText(OutputFile(environment.ArtifactsPath, Shared)));
            }
            else if (loseEntry) Assert.False(File.Exists(OutputFile(environment.ArtifactsPath, Shared)));
            commands.Add(args);
            if (prepare is null) WriteOutputs(environment.ArtifactsPath, Shared, "built");
            return Task.FromResult((new AcceptanceCheckResult("build", true, 0, null, ResultSummary: "original"), false));
        }, _ => environment, label => label, () => cache, (_, _) =>
        {
            resolutions++;
            clock.Advance(10);
            return Task.FromResult<DotnetBaseBuildCacheIdentity?>(identity);
        }, clock);

        var result = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks);
        Assert.True(result.Run.Result.Passed);
        Assert.Same(environment, phase.BuildEnvironment);
        var summary = result.Run.Result.ResultSummary!;
        Assert.EndsWith("original", summary);
        var fields = Fields(summary);
        Assert.Equal(long.Parse(fields["build_phase_ms"], CultureInfo.InvariantCulture), result.Run.Result.DurationMilliseconds);
        AssertPhase(fields, "identity_resolution", "11", "1", "measured");
        Assert.Equal(warm ? "2" : "1", fields["run_build_commands_count"]);
        Assert.Equal(invocations.ToString(CultureInfo.InvariantCulture), fields["run_build_commands_count"]);
        AssertPhase(fields, "cache_restore_copy", warm ? "1" : "0", warm ? "1" : "0", warm ? "measured" : "not_run");
        Assert.Equal(warm && !loseEntry ? "not_run" : "measured", fields["cache_capture_publish_status"]);
        Assert.Equal(warm && !loseEntry ? 1 : 2, resolutions);
        Assert.Equal(warm ? (loseEntry ? 1 : 2) : 0, callbacks);
        Assert.Equal("complete", fields["build_phase_accounting_status"]);
        Assert.Equal(warm && !loseEntry ? "hit" : "miss", fields["projects"].Split(',')[0].Split('=')[1]);
        if (warm && !loseEntry)
            Assert.Equal(new[] { Leaf, Consumer }, commands.Select(args => args[2]));
        else Assert.Equal(phase.BuildArguments, Assert.Single(commands));
    }

    [Fact]
    public void Format_OverlappingRestores_MarksEnclosingBuildUnknown()
    {
        var clock = new Clock();
        var timing = new BaseBuildCachePhaseTiming(clock);
        using (timing.Measure(Phase.RunBuildCommands))
        {
            var first = timing.Measure(Phase.CacheRestoreCopy);
            clock.Advance(4);
            var second = timing.Measure(Phase.CacheRestoreCopy);
            clock.Advance(4);
            first.Dispose();
            clock.Advance(4);
            second.Dispose();
            clock.Advance(4);
        }
        timing.Stop();
        var fields = Fields(timing.Format());
        AssertPhase(fields, "run_build_commands", "unknown", "1", "unknown");
        AssertPhase(fields, "cache_restore_copy", "unknown", "2", "unknown");
        Assert.Equal("unknown", fields["build_phase_residual_ms"]);
        Assert.Equal("partial", fields["build_phase_accounting_status"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureAsync_RevalidationUnavailableOrChanged_DoesNotPublish(bool unavailable)
    {
        var cache = Cache();
        var environment = EnvironmentFor();
        var resolutions = 0;
        var hooks = new AcceptanceDotnetBuildPhaseHooks((_, _, _, _) =>
        {
            WriteOutputs(environment.ArtifactsPath, Shared, "built");
            return Task.FromResult((new AcceptanceCheckResult("build", true, 0, null), false));
        }, _ => environment, label => label, () => cache, (_, _) =>
        {
            resolutions++;
            return Task.FromResult<DotnetBaseBuildCacheIdentity?>(resolutions == 1 ? new("original") : unavailable ? null : new("changed"));
        });
        var result = await AcceptanceDotnetBuildPhase.EnsureAsync(Create(), "attempt", hooks);
        Assert.True(result.Run.Result.Passed);
        Assert.Equal(2, resolutions);
        var fields = Fields(result.Run.Result.ResultSummary!);
        Assert.Equal("measured", fields["identity_revalidation_status"]);
        Assert.Equal("1", fields["identity_revalidation_count"]);
        AssertPhase(fields, "cache_capture_publish", "0", "0", "not_run");
        Assert.False(cache.Probe(MainSha, [Shared], new("original")).AllHit);
    }

    [Fact]
    public async Task EnsureAsync_FailedResult_ClosesBuildWithoutPublishing()
    {
        var clock = new Clock();
        var cache = Cache();
        var environment = EnvironmentFor();
        var original = new AcceptanceCheckResult("build", false, 7, "failure", ResultSummary: "failed", FailureClassification: "fixture-failure");
        var hooks = new AcceptanceDotnetBuildPhaseHooks((_, _, _, _) =>
        {
            clock.Advance(12);
            return Task.FromResult((original, true));
        }, _ => environment, label => label, () => cache,
            (_, _) => Task.FromResult<DotnetBaseBuildCacheIdentity?>(new("fixture")), clock);
        var result = await AcceptanceDotnetBuildPhase.EnsureAsync(Create(), "attempt", hooks);
        Assert.False(result.Run.Result.Passed);
        Assert.Equal(original.ExitCode, result.Run.Result.ExitCode);
        Assert.Equal(original.FailureClassification, result.Run.Result.FailureClassification);
        Assert.True(result.Run.Retried);
        var fields = Fields(result.Run.Result.ResultSummary!);
        AssertPhase(fields, "run_build_commands", "12", "1", "measured");
        AssertPhase(fields, "identity_revalidation", "0", "0", "not_run");
        AssertPhase(fields, "cache_capture_publish", "0", "0", "not_run");
    }

    [Theory]
    [InlineData("identity", false, false)]
    [InlineData("build", false, false)]
    [InlineData("build", true, false)]
    [InlineData("build", true, true)]
    [InlineData("revalidation", true, false)]
    public async Task EnsureAsync_InterruptedOperation_PropagatesWithoutCompletion(string stage, bool asynchronous, bool cancelled)
    {
        var clock = new Clock();
        var phase = Create();
        var cache = Cache();
        var environment = EnvironmentFor();
        Exception expected = cancelled ? new OperationCanceledException(new CancellationToken(true)) : new InvalidOperationException("fixture");
        var resolutions = 0;
        async Task<T> Fail<T>()
        {
            if (asynchronous) await Task.Yield();
            clock.Advance(5);
            throw expected;
        }
        var hooks = new AcceptanceDotnetBuildPhaseHooks((_, _, _, _) =>
        {
            if (stage != "build") return Task.FromResult((new AcceptanceCheckResult("build", true, 0, null), false));
            if (!asynchronous) throw expected;
            return Fail<(AcceptanceCheckResult, bool)>();
        }, _ => environment, label => label, () => cache, (_, _) =>
        {
            resolutions++;
            if (stage == "identity" || stage == "revalidation" && resolutions == 2)
            {
                if (!asynchronous) throw expected;
                return Fail<DotnetBaseBuildCacheIdentity?>();
            }
            return Task.FromResult<DotnetBaseBuildCacheIdentity?>(new("fixture"));
        }, clock);
        Assert.Same(expected, await Record.ExceptionAsync(() => AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks)));
        Assert.Null(phase.Run);
        Assert.False(phase.BuildCompleted.Task.IsCompleted);
        Assert.False(cache.Probe(MainSha, [Shared], new("fixture")).AllHit);
        // Start + one begin/end pair per entered phase; disposal also closes interrupted calls.
        Assert.Equal(stage == "identity" ? 3 : stage == "build" ? 5 : 7, clock.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureAsync_BypassOrUnresolvedIdentity_PreservesFallback(bool unresolved)
    {
        var phase = unresolved ? Create() : new DotnetTestBuildPhase(["dotnet", "build"], null);
        var environment = EnvironmentFor();
        var builds = 0;
        var resolutions = 0;
        var hooks = new AcceptanceDotnetBuildPhaseHooks((args, _, prepare, _) =>
        {
            Assert.Equal(phase.BuildArguments, args);
            Assert.Null(prepare);
            builds++;
            return Task.FromResult((new AcceptanceCheckResult("build", true, 0, null, ResultSummary: "fallback"), false));
        }, _ => environment, label => label, () => throw new InvalidOperationException("No cache authority"), (_, _) =>
        {
            resolutions++;
            return Task.FromResult<DotnetBaseBuildCacheIdentity?>(null);
        });
        var result = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks);
        Assert.Equal("fallback", result.Run.Result.ResultSummary);
        Assert.Equal(unresolved ? 1 : 0, resolutions);
        Assert.Equal(1, builds);
        var repeated = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks);
        Assert.False(repeated.ContributesToCheck);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task EnsureAsync_NoFocusedCommands_UsesOriginalFallbackWithoutSummary()
    {
        var identity = new DotnetBaseBuildCacheIdentity("fixture");
        var cache = Cache();
        SeedCache(cache, identity);
        var phase = new DotnetTestBuildPhase(["dotnet", "build", Consumer], new(MainSha, [Shared], [], [Shared]));
        var environment = EnvironmentFor();
        var calls = 0;
        var hooks = new AcceptanceDotnetBuildPhaseHooks((args, _, prepare, _) =>
        {
            calls++;
            Assert.Equal(phase.BuildArguments, args);
            Assert.Null(prepare);
            return Task.FromResult((new AcceptanceCheckResult("build", true, 0, null, ResultSummary: "fallback"), false));
        }, _ => environment, label => label, () => cache, (_, _) => Task.FromResult<DotnetBaseBuildCacheIdentity?>(identity));
        Assert.Equal("fallback", (await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks)).Run.Result.ResultSummary);
        Assert.Equal(1, calls);
    }

    private DotnetTestBuildPhase Create() => new(["dotnet", "build", Consumer], new(MainSha, [Shared], [Leaf, Consumer], [Shared, Leaf, Consumer]));
    private DotnetBaseBuildCache Cache() => new(Path.Combine(_root, "cache"));
    private DotnetBuildEnvironment EnvironmentFor() => new("owned", _root, Path.Combine(_root, "artifacts"), Path.Combine(_root, "lock"), [], "fixture");
    private void SeedCache(DotnetBaseBuildCache cache, DotnetBaseBuildCacheIdentity identity)
    {
        var seed = Path.Combine(_root, "seed");
        WriteOutputs(seed, Shared, "seed");
        Assert.Equal("published", Assert.Single(cache.Publish(MainSha, seed, [Shared], identity).Projects).Status);
    }
    private static string OutputFile(string root, string project) => Path.Combine(root, "bin", Path.GetFileNameWithoutExtension(project), "debug_net10.0", "cache.txt");
    private static void WriteOutputs(string root, string project, string value)
    {
        foreach (var kind in new[] { "bin", "obj" })
        {
            var directory = Path.Combine(root, kind, Path.GetFileNameWithoutExtension(project), "debug_net10.0");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "cache.txt"), value);
        }
    }
    private static Dictionary<string, string> Fields(string summary) => summary.Split(' ').Where(token => token.Contains('='))
        .Select(token => token.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
    private static void AssertPhase(Dictionary<string, string> fields, string phase, string ms, string count, string status)
    {
        Assert.Equal(ms, fields[phase + "_ms"]);
        Assert.Equal(count, fields[phase + "_count"]);
        Assert.Equal(status, fields[phase + "_status"]);
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        internal bool TickOnRead { get; init; }
        internal int Reads { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            Reads++;
            var value = _ticks;
            if (TickOnRead) Advance(1);
            return value;
        }
        internal void Advance(double milliseconds) => _ticks += (long)(milliseconds * TimeSpan.TicksPerMillisecond);
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
