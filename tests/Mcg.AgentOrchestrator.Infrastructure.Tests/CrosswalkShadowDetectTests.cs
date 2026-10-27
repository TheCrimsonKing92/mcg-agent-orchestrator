using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private temporary roots, injected environment/runner/clock, no real processes.
public sealed class CrosswalkShadowDetectTests : IDisposable
{
    private readonly DirectoryInfo _sandbox = Directory.CreateTempSubdirectory("crosswalk-shadow-");
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    private const string GoJson = """{"stack":"go","evidence":[{"path":"go.mod","reason":"found go marker"}]}""";
    private int _calls;

    [Fact]
    public void Resolve_Disagreement_PreservesDetectorAndLogsOncePerRoot()
    {
        var root = CreateRoot("package.json");
        var home = CreateHome();
        var shadow = CreateShadow();
        var baseline = TargetToolchainDetector.Detect(root);
        Assert.Equal(Toolchain.Node, baseline);

        for (var i = 0; i < 3; i++)
            Assert.Equal(baseline, ProjectStackResolver.Resolve(root, home, shadow));

        Assert.Equal(1, _calls);
        var record = Assert.Single(ReadEvents(home));
        Assert.Equal(CrosswalkShadowDetect.DisagreementEventKind, record.EventKind);
        Assert.Null(record.GoalId);
        Assert.Equal(Now, record.Timestamp);
        Assert.StartsWith("CROSSWALK_SHADOW_DISAGREEMENT root=", record.Detail);
        Assert.Contains("root=" + JsonSerializer.Serialize(Path.GetFullPath(root)), record.Detail);
        Assert.Contains("orchestrator=node crosswalk=go", record.Detail);
        Assert.Contains("\"path\":\"go.mod\"", record.Detail);
        Assert.Contains("\"reason\":\"found go marker\"", record.Detail);

        var second = CreateRoot("package.json");
        Assert.Equal(TargetToolchainDetector.Detect(second), ProjectStackResolver.Resolve(second, home, shadow));
        Assert.Equal(2, _calls);
        Assert.Equal(2, ReadEvents(home).Length);
    }

    [Fact]
    public async Task Resolve_ParallelSameRoot_RunsAndLogsOnce()
    {
        var root = CreateRoot("package.json");
        var home = CreateHome();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WorkerProcessRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var start = new ManualResetEventSlim();
        var callers = 0;
        var shadow = CreateShadow((_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        var baseline = TargetToolchainDetector.Detect(root);
        // Dedicated threads ensure all eight callers can reach the gate while Detect blocks synchronously.
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            if (Interlocked.Increment(ref callers) == 8)
                ready.TrySetResult();
            Assert.True(start.Wait(TimeSpan.FromSeconds(30)), "Parallel caller start gate was not released.");
            return ProjectStackResolver.Resolve(root, home, shadow);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang guard: all callers reached the start gate.
            start.Set();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang guard: fake runner was entered.
        }
        finally
        {
            start.Set();
            release.TrySetResult(new WorkerProcessRunResult(0, GoJson, ""));
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.All(tasks, task => Assert.Equal(baseline, task.Result));
        Assert.Equal(1, _calls);
        Assert.Single(ReadEvents(home));
    }

    [Fact]
    public void Resolve_Agreement_DoesNotCreateLogs()
    {
        var root = CreateRoot("package.json");
        var home = CreateHome();
        var shadow = CreateShadow((_, _) => Task.FromResult(
            new WorkerProcessRunResult(0, """{"stack":"node","evidence":[]}""", "")));

        for (var i = 0; i < 3; i++)
            Assert.Equal(Toolchain.Node, ProjectStackResolver.Resolve(root, home, shadow));

        Assert.Equal(1, _calls);
        Assert.False(Directory.Exists(Path.Combine(home, "logs")));
    }

    [Fact]
    public void Resolve_DeclaredStack_BypassesCrosswalk()
    {
        var root = CreateRoot("App.sln");
        var home = CreateHome("""{"schemaVersion":4,"stack":"go"}""");
        var shadow = CreateShadow();
        Assert.Equal(Toolchain.Dotnet, TargetToolchainDetector.Detect(root));

        Assert.Equal(Toolchain.Go, ProjectStackResolver.Resolve(root, home, shadow));

        Assert.Equal(0, _calls);
        Assert.False(Directory.Exists(Path.Combine(home, "logs")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NoProjectHome_BypassesCrosswalk(string? home)
    {
        var root = CreateRoot("package.json");
        var shadow = CreateShadow();

        Assert.Equal(TargetToolchainDetector.Detect(root), ProjectStackResolver.Resolve(root, home, shadow));
        Assert.Equal(0, _calls);
    }

    [Theory]
    [InlineData("go.mod")]
    [InlineData("package.json")]
    [InlineData("App.sln")]
    [InlineData("src/App/App.csproj")]
    [InlineData("pyproject.toml")]
    [InlineData("")]
    public void Resolve_Unconfigured_PreservesBaselineAndProjectHome(string marker)
    {
        var root = CreateRoot(marker);
        var home = CreateHome();
        var reads = 0;
        var shadow = new CrosswalkShadowDetect(new CrosswalkClient(_ => { reads++; return null; }, (_, _) =>
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new WorkerProcessRunResult(0, GoJson, ""));
        }));
        var before = Directory.GetFileSystemEntries(home, "*", SearchOption.AllDirectories).Order().ToArray();
        var baseline = TargetToolchainDetector.Detect(root);

        Assert.Equal(baseline, ProjectStackResolver.Resolve(root, home, shadow));
        Assert.Equal(baseline, ProjectStackResolver.Resolve(root, home, shadow));

        Assert.Equal(1, reads);
        Assert.Equal(0, _calls);
        Assert.Equal(before, Directory.GetFileSystemEntries(home, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("start-failed")]
    [InlineData("exit-nonzero")]
    [InlineData("invalid-json")]
    [InlineData("missing")]
    public void Resolve_Failure_PreservesBaselineAndLogsOncePerStatus(string status)
    {
        var root = CreateRoot("package.json");
        var home = CreateHome();
        var shadow = CreateShadow((_, _) => status switch
        {
            "timeout" => throw new OperationCanceledException(),
            "start-failed" => throw new InvalidOperationException(),
            "exit-nonzero" => Task.FromResult(new WorkerProcessRunResult(2, "", "failed")),
            _ => Task.FromResult(new WorkerProcessRunResult(0, "garbage", ""))
        }, missing: status == "missing");

        Assert.Equal(TargetToolchainDetector.Detect(root), ProjectStackResolver.Resolve(root, home, shadow));
        var record = Assert.Single(ReadEvents(home));
        Assert.Equal(CrosswalkShadowDetect.UnavailableEventKind, record.EventKind);
        Assert.Null(record.GoalId);
        var detail = status switch { "start-failed" => "InvalidOperationException", "exit-nonzero" => "exit=2", _ => "" };
        Assert.Equal($"CROSSWALK_SHADOW_UNAVAILABLE reason={status} root={JsonSerializer.Serialize(root)} " +
            $"detail={JsonSerializer.Serialize(detail)}", record.Detail);

        var second = CreateRoot("package.json");
        Assert.Equal(TargetToolchainDetector.Detect(second), ProjectStackResolver.Resolve(second, home, shadow));
        Assert.Equal(TargetToolchainDetector.Detect(root), ProjectStackResolver.Resolve(root, home, shadow));
        Assert.Single(ReadEvents(home));
        Assert.Equal(status == "missing" ? 0 : 2, _calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_BlockedLogAppend_PreservesBaselineAndConsumesOpportunity(bool unavailable)
    {
        var root = CreateRoot("package.json");
        var home = CreateHome();
        var logs = Path.Combine(home, "logs");
        File.WriteAllText(logs, "blocked");
        var shadow = CreateShadow((_, _) => Task.FromResult(unavailable
            ? new WorkerProcessRunResult(2, "", "") : new WorkerProcessRunResult(0, GoJson, "")));
        var baseline = TargetToolchainDetector.Detect(root);

        Assert.Equal(baseline, ProjectStackResolver.Resolve(root, home, shadow));
        File.Delete(logs);
        Assert.Equal(baseline, ProjectStackResolver.Resolve(root, home, shadow));
        Assert.Equal(1, _calls);
        if (unavailable)
        {
            var second = CreateRoot("package.json");
            Assert.Equal(TargetToolchainDetector.Detect(second), ProjectStackResolver.Resolve(second, home, shadow));
            Assert.Equal(2, _calls);
        }
        Assert.False(Directory.Exists(logs));
    }

    [Fact]
    public void Observe_FullPathAliases_ShareOneObservation()
    {
        var root = CreateRoot("package.json");
        var home = CreateHome();
        var shadow = CreateShadow();

        Assert.Equal(Toolchain.Node, shadow.Observe(Path.Combine(root, "."), home, Toolchain.Node));
        Assert.Equal(Toolchain.Python, shadow.Observe(root.ToUpperInvariant(), home, Toolchain.Python));
        Assert.Equal(1, _calls);
        Assert.Single(ReadEvents(home));
    }

    [Fact]
    public void Observe_InvalidRootOrThrowingClock_DoesNotEscape()
    {
        var root = CreateRoot("package.json");
        var home = CreateHome();
        var shadow = CreateShadow(utcNow: () => throw new InvalidOperationException());

        Assert.Equal(Toolchain.Node, shadow.Observe("\0", home, Toolchain.Node));
        Assert.Equal(0, _calls);
        Assert.Equal(Toolchain.Node, shadow.Observe(root, home, Toolchain.Node));
        Assert.Equal(Toolchain.Node, shadow.Observe(root, home, Toolchain.Node));
        Assert.Equal(1, _calls);
        Assert.False(Directory.Exists(Path.Combine(home, "logs")));
    }

    private CrosswalkShadowDetect CreateShadow(
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runner = null,
        bool missing = false, Func<DateTimeOffset>? utcNow = null)
    {
        var path = Path.Combine(_sandbox.FullName, "crosswalk.exe");
        if (!missing)
            File.WriteAllText(path, "");
        return new(new CrosswalkClient(_ => path, (request, token) =>
        {
            Interlocked.Increment(ref _calls);
            return runner is null ? Task.FromResult(new WorkerProcessRunResult(0, GoJson, "")) : runner(request, token);
        }), utcNow ?? (() => Now));
    }

    private string CreateRoot(string marker)
    {
        var root = Directory.CreateDirectory(Path.Combine(_sandbox.FullName, Guid.NewGuid().ToString("N"))).FullName;
        if (marker.Length > 0)
        {
            var path = Path.Combine(root, marker);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        return root;
    }

    private string CreateHome(string model = """{"schemaVersion":4}""")
    {
        var home = CreateRoot("");
        File.WriteAllText(Path.Combine(home, "project-model.json"), model);
        return home;
    }

    private static ConductEventMirrorRecord[] ReadEvents(string home) =>
        File.ReadAllLines(Path.Combine(home, "logs", "conduct-events.log"))
            .Select(line => JsonSerializer.Deserialize<ConductEventMirrorRecord>(line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();

    public void Dispose() => _sandbox.Delete(recursive: true);
}
