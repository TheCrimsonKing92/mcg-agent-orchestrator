using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.CacheIdentityStageTiming;

// Parallel-safe: explicit environment, injected commands/clocks, independently owned files.
public sealed class CacheIdentityStageTimingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "identity-stages", Guid.NewGuid().ToString("N"));
    private static readonly string[] Projects = ["src/One.csproj", "src/Two.csproj"];
    private const string MainSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Accounting_IsExclusiveAndAggregatesRepeatedStagesAndPhysicalWork()
    {
        var clock = new Clock();
        var receipts = new List<CacheIdentityStageReceipt>();
        var timing = new CacheIdentityStageTiming(new(receipts.Add, clock));
        for (var i = 0; i < 2; i++)
            using (timing.Measure(Stage.RestoreCommand)) clock.Advance(2);
        using (timing.Measure(Stage.EvaluationCommand)) clock.Advance(3);
        using (timing.Measure(Stage.PreprocessingCommand)) clock.Advance(5);
        using (timing.Measure(Stage.DirectoryHash))
        {
            timing.TreeExecution();
            using var stream = timing.ObserveHashing(new MemoryStream([1, 2, 3]), tree: true);
            SHA256.HashData(stream);
            clock.Advance(4);
        }
        using (timing.Measure(Stage.DirectoryHash))
        {
            timing.MemoHit();
            clock.Advance(2);
        }
        using (timing.Measure(Stage.FileHash))
        {
            using var stream = timing.ObserveHashing(new MemoryStream([4, 5]), tree: false);
            SHA256.HashData(stream);
            clock.Advance(2);
        }
        clock.Advance(5);
        timing.Complete(true);
        timing.Dispose();
        timing.Dispose();
        var fields = Fields(Assert.Single(receipts).Fields);
        Assert.Equal("25", fields["total_ms"]);
        Assert.Equal("5", fields["residual_ms"]);
        AssertStage(fields, "restore_command", "4", "2", "measured");
        AssertStage(fields, "evaluation_command", "3", "1", "measured");
        AssertStage(fields, "preprocessing_command", "5", "1", "measured");
        AssertStage(fields, "directory_hash", "6", "2", "measured");
        AssertStage(fields, "file_hash", "2", "1", "measured");
        Assert.Equal("1", fields["directory_hash_executions"]);
        Assert.Equal("1", fields["directory_hash_memo_hits"]);
        Assert.Equal("1", fields["directory_hash_files"]);
        Assert.Equal("3", fields["directory_hash_bytes"]);
        Assert.Equal("1", fields["file_hash_files"]);
        Assert.Equal("2", fields["file_hash_bytes"]);
        Assert.Equal("complete", fields["accounting_status"]);
        Assert.Equal("measured", fields["availability"]);
    }

    [Fact]
    public void FractionalTicks_AggregateBeforeConversionAndDistinguishKnownZero()
    {
        var clock = new Clock();
        var receipts = new List<CacheIdentityStageReceipt>();
        using (var timing = new CacheIdentityStageTiming(new(receipts.Add, clock)))
        {
            for (var i = 0; i < 2; i++)
                using (timing.Measure(Stage.RestoreCommand)) clock.Advance(0.75);
            clock.Advance(0.75);
            timing.Complete(true);
        }
        var fields = Fields(Assert.Single(receipts).Fields);
        Assert.Equal("2", fields["total_ms"]);
        Assert.Equal("0", fields["residual_ms"]);
        AssertStage(fields, "restore_command", "1", "2", "measured");
        AssertStage(fields, "directory_hash", "0", "0", "not_run");
        Assert.Equal("0", fields["directory_hash_bytes"]);
        foreach (var availability in new[] { "disabled", "unavailable", "not_run" })
        {
            var missing = Fields(CacheIdentityStageReceipt.Missing(availability).Fields);
            Assert.Equal(availability, missing["availability"]);
            Assert.Equal("partial", missing["accounting_status"]);
            AssertStage(missing, "directory_hash", "unknown", "unknown", "unknown");
        }
    }

    [Theory]
    [InlineData("backwards")]
    [InlineData("throw")]
    [InlineData("frequency")]
    [InlineData("overlap")]
    [InlineData("unfinished")]
    public void InvalidClockOrIntervals_AreUnavailableRatherThanClamped(string failure)
    {
        var clock = new Clock { Failure = failure };
        var receipts = new List<CacheIdentityStageReceipt>();
        var timing = new CacheIdentityStageTiming(new(receipts.Add, clock));
        var first = timing.Measure(Stage.DirectoryHash);
        if (failure == "overlap")
        {
            using (timing.Measure(Stage.FileHash)) clock.Advance(2);
        }
        clock.Advance(failure == "backwards" ? -2 : 1);
        if (failure != "unfinished") first.Dispose();
        timing.Complete(true);
        timing.Dispose();
        var fields = Fields(Assert.Single(receipts).Fields);
        Assert.Equal("partial", fields["accounting_status"]);
        Assert.Equal("unavailable", fields["availability"]);
        Assert.Equal("unknown", fields["residual_ms"]);
        AssertStage(fields, "directory_hash", "unknown", "1", "unknown");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HashReads_CountReturnedBytesAndEmptyFilesWithoutExtraReads(bool tree)
    {
        var receipts = new List<CacheIdentityStageReceipt>();
        using (var timing = new CacheIdentityStageTiming(new(receipts.Add, new Clock())))
        {
            using (timing.Measure(tree ? Stage.DirectoryHash : Stage.FileHash))
            {
                if (tree) timing.TreeExecution();
                using (var empty = timing.ObserveHashing(new MemoryStream(), tree)) SHA256.HashData(empty);
                using var stream = timing.ObserveHashing(new PartialReadStream(), tree);
                Assert.Equal(2, stream.Read(new byte[8], 0, 8));
                Assert.Throws<IOException>(() => stream.Read(new byte[8], 0, 8));
            }
        }
        var fields = Fields(Assert.Single(receipts).Fields);
        var prefix = tree ? "directory_hash" : "file_hash";
        Assert.Equal("2", fields[prefix + "_files"]);
        Assert.Equal("2", fields[prefix + "_bytes"]);
        Assert.Equal("0", fields[(tree ? "file_hash" : "directory_hash") + "_files"]);
        Assert.Equal("partial", fields["accounting_status"]);
    }

    [Fact]
    public async Task Resolver_MeasuresLazyTreeWorkAndMemoHitsWithoutNestedFileAttribution()
    {
        Seed();
        var clock = new Clock { TickOnRead = true };
        var receipts = new List<CacheIdentityStageReceipt>();
        Assert.NotNull(await Resolve(new(receipts.Add, clock)));
        var fields = Fields(Assert.Single(receipts).Fields);
        Assert.Equal("complete", fields["accounting_status"]);
        Assert.Equal("2", fields["restore_command_count"]);
        Assert.Equal("2", fields["evaluation_command_count"]);
        Assert.Equal("2", fields["preprocessing_command_count"]);
        Assert.Equal("6", fields["directory_hash_count"]);
        Assert.Equal("3", fields["directory_hash_executions"]);
        Assert.Equal("3", fields["directory_hash_memo_hits"]);
        Assert.Equal("5", fields["directory_hash_files"]);
        Assert.Equal("18", fields["directory_hash_bytes"]);
        Assert.Equal("4", fields["file_hash_count"]);
        Assert.Equal("4", fields["file_hash_files"]);
        Assert.Equal("20", fields["file_hash_bytes"]);
        Assert.Equal("6", fields["directory_hash_ms"]);
        Assert.Equal("4", fields["file_hash_ms"]);
        Assert.Equal("command_boundary_internals_unknown", fields["command_scope"]);
        Assert.Equal(26, fields.Count); // Fixed aggregate size, independent of files/projects.
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("clock")]
    [InlineData("sink")]
    public async Task Diagnostics_PreserveExactIdentityCommandsAndCacheDecisions(string diagnostic)
    {
        Seed();
        var disabledCommands = new List<string[]>();
        var enabledCommands = new List<string[]>();
        var baseline = Assert.IsType<DotnetBaseBuildCacheIdentity>(await Resolve(commands: disabledCommands));
        var receipts = new List<CacheIdentityStageReceipt>();
        var publications = 0;
        var context = new CacheIdentityDiagnosticContext(receipt =>
        {
            publications++;
            receipts.Add(receipt);
            if (diagnostic == "sink") throw new ApplicationException("sink failure");
        }, new Clock { Failure = diagnostic == "clock" ? "throw" : null });
        var observed = Assert.IsType<DotnetBaseBuildCacheIdentity>(await Resolve(context, commands: enabledCommands));
        Assert.Equal(baseline, observed);
        Assert.Equal(baseline.Fingerprint, observed.Fingerprint);
        Assert.Equal(baseline.Components, observed.Components);
        Assert.Equal(baseline.ProjectKeys, observed.ProjectKeys);
        Assert.Equal(baseline.ProjectReferences, observed.ProjectReferences);
        Assert.NotEmpty(observed.ProjectKeys);
        Assert.Equal(disabledCommands.Select(CommandShape), enabledCommands.Select(CommandShape));
        Assert.Equal(6, enabledCommands.Count);
        Assert.Equal(1, publications);
        Assert.Single(receipts);
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        var miss = cache.Probe(MainSha, Projects, baseline);
        Assert.False(miss.AllHit);
        Assert.Equal(miss.Projects, cache.Probe(MainSha, Projects, observed).Projects);
        WriteOutputs();
        Assert.All(cache.Publish(MainSha, EnvironmentFor().ArtifactsPath, Projects, baseline).Projects,
            project => Assert.Equal("published", project.Status));
        var hit = cache.Probe(MainSha, Projects, baseline);
        Assert.True(hit.AllHit);
        Assert.Equal(hit.Projects, cache.Probe(MainSha, Projects, observed).Projects);
        var destination = Path.Combine(_root, "restored");
        Assert.True(cache.Restore(MainSha, destination, Projects, observed).AllHit);
        Assert.Equal(File.ReadAllBytes(Output(EnvironmentFor().ArtifactsPath, Projects[0], "bin")),
            File.ReadAllBytes(Output(destination, Projects[0], "bin")));
    }

    [Theory]
    [InlineData("failed", "restore")]
    [InlineData("timed-out", "evaluation")]
    [InlineData("capture-limited", "preprocessing")]
    [InlineData("malformed", "evaluation")]
    [InlineData("missing-assets", "evaluation")]
    [InlineData("missing-source", "evaluation")]
    [InlineData("missing-preprocess", "preprocessing")]
    [InlineData("unsupported", "none")]
    [InlineData("missing-project", "none")]
    [InlineData("handled-io", "restore")]
    [InlineData("handled-invalid", "evaluation")]
    public async Task UnsuccessfulResolution_PreservesNullAndPublishesOnePartial(string failure, string stage)
    {
        Seed();
        var commands = new List<string[]>();
        Assert.Null(await Resolve(failure: failure, failingStage: stage));
        var receipts = new List<CacheIdentityStageReceipt>();
        Assert.Null(await Resolve(new(receipts.Add, new Clock()), commands, failure, stage));
        var fields = Fields(Assert.Single(receipts).Fields);
        Assert.Equal("partial", fields["accounting_status"]);
        Assert.Equal("measured", fields["availability"]);
        Assert.Equal(stage == "none" ? "0" : "1", fields["restore_command_count"]);
        if (stage == "none") Assert.Empty(commands);
    }

    [Theory]
    [InlineData(false, "restore")]
    [InlineData(true, "evaluation")]
    [InlineData(false, "preprocessing")]
    public async Task UnhandledAndCancelledCommands_PreserveOriginalExceptionWithThrowingSink(bool cancelled, string stage)
    {
        Seed();
        Exception expected = cancelled ? new OperationCanceledException(new CancellationToken(true)) : new ApplicationException("command failure");
        var receipts = new List<CacheIdentityStageReceipt>();
        var context = new CacheIdentityDiagnosticContext(receipt =>
        {
            receipts.Add(receipt);
            throw new InvalidOperationException("sink failure");
        }, new Clock());
        Assert.Same(expected, await Record.ExceptionAsync(() => Resolve(exception: expected, failingStage: stage)));
        Assert.Same(expected, await Record.ExceptionAsync(() => Resolve(context, exception: expected, failingStage: stage)));
        Assert.Equal("partial", Fields(Assert.Single(receipts).Fields)["accounting_status"]);
    }

    [Fact]
    public async Task Wrapper_OwnsPreparationAndPublishesOnceWhileInjectedIdentityDoesNotReadClock()
    {
        Seed();
        var clock = new Clock();
        var receipts = new List<CacheIdentityStageReceipt>();
        var phase = CreatePhase();
        phase.IdentityDiagnostics = new(receipts.Add, clock);
        var environment = EnvironmentFor();
        var permits = 0;
        var identity = await DotnetBaseBuildCacheIdentityResolver.ResolveForBuildPhaseAsync(phase, environment, _root,
            (variables, _, _) =>
            {
                variables.Clear();
                clock.Advance(7);
            }, () => { permits++; return null; }, args => Run(args), null);
        Assert.NotNull(identity);
        Assert.Equal(1, permits);
        Assert.Equal("7", Fields(Assert.Single(receipts).Fields)["residual_ms"]);
        var reads = clock.Reads;
        Assert.Same(identity, await DotnetBaseBuildCacheIdentityResolver.ResolveForBuildPhaseAsync(phase, environment, _root,
            (_, _, _) => throw new ApplicationException(), () => throw new ApplicationException(),
            _ => throw new ApplicationException(), () => identity));
        Assert.Equal(reads, clock.Reads);
        Assert.Single(receipts);
    }

    [Theory]
    [InlineData("acquire")]
    [InlineData("configure")]
    [InlineData("dispose")]
    public async Task Wrapper_FailurePreservesExceptionAndPartialObservation(string stage)
    {
        Seed();
        var expected = new ApplicationException("wrapper failure");
        var receipts = new List<CacheIdentityStageReceipt>();
        var phase = CreatePhase();
        phase.IdentityDiagnostics = new(receipts.Add, new Clock());
        Assert.Same(expected, await Record.ExceptionAsync(() =>
            DotnetBaseBuildCacheIdentityResolver.ResolveForBuildPhaseAsync(phase, EnvironmentFor(), _root,
                (variables, _, _) =>
                {
                    if (stage == "configure") throw expected;
                    variables.Clear();
                }, () => stage == "acquire" ? throw expected : new Permit(() =>
                {
                    if (stage == "dispose") throw expected;
                }), args => Run(args), null)));
        var fields = Fields(Assert.Single(receipts).Fields);
        Assert.Equal("partial", fields["accounting_status"]);
        Assert.Equal(stage == "dispose" ? "2" : "0", fields["restore_command_count"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_LabelsRolesAndRestoresContextWithoutChangingCachePaths(bool enabled)
    {
        Seed();
        var environment = EnvironmentFor();
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        var previous = new CacheIdentityDiagnosticContext(_ => throw new ApplicationException("must not be invoked"));
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var phase = CreatePhase();
            phase.IdentityDiagnostics = previous;
            var builds = new List<string[]>();
            var hooks = new AcceptanceDotnetBuildPhaseHooks((args, _, prepare, owned) =>
            {
                Assert.Same(environment, owned);
                prepare?.Invoke(environment);
                builds.Add(args);
                WriteOutputs();
                return Task.FromResult((new AcceptanceCheckResult("build", true, 0, null, ResultSummary: "original"), false));
            }, _ => environment, name => name, () => cache, (candidate, _) =>
                DotnetBaseBuildCacheIdentityResolver.ResolveAsync(_root, candidate.CachePlan!.RestoreProjects,
                    candidate.BuildArguments, environment, args => Run(args), new Dictionary<string, string>(),
                    candidate.CachePlan.BuildProjects, enabled ? candidate.IdentityDiagnostics : null));
            var result = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks);
            Assert.True(result.Run.Result.Passed);
            Assert.Same(previous, phase.IdentityDiagnostics);
            var summary = result.Run.Result.ResultSummary!;
            Assert.Contains("evictions=none; original", summary);
            var fields = Fields(summary);
            Assert.Equal(enabled ? "measured" : "unavailable", fields["identity_initial_availability"]);
            Assert.Equal(cycle == 0 ? enabled ? "measured" : "unavailable" : "not_run", fields["identity_revalidation_availability"]);
            Assert.Equal(cycle == 0 ? "1" : "0", fields["identity_revalidation_count"]);
            Assert.Equal(cycle == 0 ? "src/One.csproj=miss,src/Two.csproj=changed" : "src/One.csproj=hit,src/Two.csproj=changed", fields["projects"]);
            Assert.Equal(cycle == 0 ? "src/One.csproj,src/Two.csproj" : "src/Two.csproj", fields["built_projects"]);
            Assert.Equal(cycle == 0 ? new[] { "Consumer.csproj" } : new[] { Projects[1] }, builds.Select(args => args[2]));
        }
    }

    [Fact]
    public async Task Caller_ExceptionRestoresContextWithoutPublishingCompletion()
    {
        Seed();
        var phase = CreatePhase();
        var previous = new CacheIdentityDiagnosticContext(_ => { });
        phase.IdentityDiagnostics = previous;
        var expected = new OperationCanceledException();
        var hooks = new AcceptanceDotnetBuildPhaseHooks((_, _, _, _) => throw new ApplicationException(),
            _ => EnvironmentFor(), name => name, () => new(Path.Combine(_root, "cache")),
            (candidate, _) => Resolve(candidate.IdentityDiagnostics, exception: expected, failingStage: "restore"));
        Assert.Same(expected, await Record.ExceptionAsync(() => AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks)));
        Assert.Same(previous, phase.IdentityDiagnostics);
        Assert.Null(phase.Run);
        Assert.False(phase.BuildCompleted.Task.IsCompleted);
    }

    private void Seed()
    {
        foreach (var (path, text) in new[] { (Projects[0], "one"), (Projects[1], "two"), ("src/Value.cs", "value"),
                     ("dotnet/sdk/10.0/compiler.dll", "sdk"), ("dotnet/packs/ref.dll", "pack"), ("Directory.Build.props", "props") })
        {
            var fullPath = Path.Combine(_root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, text);
        }
        var assets = Path.Combine(EnvironmentFor().ArtifactsPath, "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        File.WriteAllText(assets, "{\"packageFolders\":{},\"libraries\":{}}");
    }

    private DotnetBuildEnvironment EnvironmentFor() => new("fixture", Path.Combine(_root, "owned"),
        Path.Combine(_root, "owned/artifacts"), Path.Combine(_root, "owned/lock"), [], "fixture");

    private DotnetTestBuildPhase CreatePhase() => new(["dotnet", "build", "Consumer.csproj"],
        new(MainSha, [Projects[0]], [Projects[1]], Projects));

    private Task<DotnetBaseBuildCacheIdentity?> Resolve(CacheIdentityDiagnosticContext? diagnostics = null,
        List<string[]>? commands = null, string? failure = null, string? failingStage = null, Exception? exception = null) =>
        DotnetBaseBuildCacheIdentityResolver.ResolveAsync(_root, failure == "missing-project" ? ["missing.csproj"] : Projects,
            ["dotnet", "build", "Consumer.csproj", .. (failure == "unsupported" ? new[] { "--no-restore" } : Array.Empty<string>())],
            EnvironmentFor(), args =>
            {
                commands?.Add(args);
                return Run(args, failure, failingStage, exception);
            }, new Dictionary<string, string>(), diagnostics: diagnostics);

    private Task<GoalAcceptanceVerifier.CommandResult> Run(string[] args, string? failure = null,
        string? failingStage = null, Exception? exception = null)
    {
        var preprocessing = args.SingleOrDefault(arg => arg.StartsWith("-preprocess:", StringComparison.Ordinal));
        var stage = preprocessing is not null ? "preprocessing" : args.Contains("-t:Restore") ? "restore" : "evaluation";
        if (stage == failingStage)
        {
            if (exception is not null) return Task.FromException<GoalAcceptanceVerifier.CommandResult>(exception);
            if (failure == "handled-io") return Task.FromException<GoalAcceptanceVerifier.CommandResult>(new IOException("command IO"));
            if (failure == "handled-invalid") return Task.FromException<GoalAcceptanceVerifier.CommandResult>(new InvalidOperationException("command invalid"));
            if (failure == "failed") return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "failed"));
            if (failure == "timed-out") return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "", TimedOut: true));
            if (failure == "capture-limited") return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "", CaptureLimited: true));
            if (failure == "malformed") return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "{"));
        }
        if (preprocessing is not null && failure != "missing-preprocess") File.WriteAllText(preprocessing[12..], "imports");
        if (stage != "evaluation") return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
        var items = args.Single(arg => arg.StartsWith("-getItem:", StringComparison.Ordinal))[9..].Split(',')
            .ToDictionary(name => name, _ => (object)Array.Empty<object>());
        items["Compile"] = new[] { new { FullPath = Path.Combine(_root, failure == "missing-source" ? "missing" : "src/Value.cs") } };
        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, JsonSerializer.Serialize(new
        {
            Properties = new { TargetFramework = "net10.0", TargetFrameworks = "", NETCoreSdkVersion = "10.0",
                MSBuildSDKsPath = Path.Combine(_root, "dotnet/sdk/10.0/Sdks"),
                ProjectAssetsFile = Path.Combine(EnvironmentFor().ArtifactsPath, failure == "missing-assets" ? "missing.json" : "project.assets.json") },
            Items = items
        })));
    }

    private void WriteOutputs()
    {
        foreach (var project in Projects)
        foreach (var kind in new[] { "bin", "obj" })
        {
            var file = Output(EnvironmentFor().ArtifactsPath, project, kind);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "output:" + project);
        }
    }

    private static string Output(string root, string project, string kind) =>
        Path.Combine(root, kind, Path.GetFileNameWithoutExtension(project), "debug_net10.0", "cache.txt");
    private static string CommandShape(string[] args) => string.Join(" ", args.Select(arg =>
        arg.StartsWith("-preprocess:", StringComparison.Ordinal) ? "-preprocess:<invocation-temp>" : arg));
    private static Dictionary<string, string> Fields(string summary) => summary.Split(' ').Where(token => token.Contains('='))
        .Select(token => token.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
    private static void AssertStage(Dictionary<string, string> fields, string name, string ms, string count, string status)
    {
        Assert.Equal(ms, fields[name + "_ms"]);
        Assert.Equal(count, fields[name + "_count"]);
        Assert.Equal(status, fields[name + "_status"]);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        internal string? Failure { get; init; }
        internal bool TickOnRead { get; init; }
        internal int Reads { get; private set; }
        public override long TimestampFrequency => Failure == "frequency" ? throw new ApplicationException("clock frequency") : TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            Reads++;
            if (Failure == "throw") throw new ApplicationException("clock timestamp");
            var ticks = _ticks;
            if (TickOnRead) Advance(1);
            return ticks;
        }
        internal void Advance(double milliseconds) => _ticks += (long)(milliseconds * TimeSpan.TicksPerMillisecond);
    }

    private sealed class PartialReadStream : MemoryStream
    {
        private int _reads;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reads++ != 0) throw new IOException("read failed after two bytes");
            buffer[offset] = 1;
            buffer[offset + 1] = 2;
            return 2;
        }
    }

    private sealed class Permit(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
