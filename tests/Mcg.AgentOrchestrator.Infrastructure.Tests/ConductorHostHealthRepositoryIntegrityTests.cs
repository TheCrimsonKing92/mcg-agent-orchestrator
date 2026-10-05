using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique temporary layouts, a recording labeler and a fixed clock; no host labels change.
public sealed class ConductorHostHealthRepositoryIntegrityTests
{
    [Fact]
    public void LowRootAndHooksWarnOnceThenClearWithoutChangingLabels()
    {
        using var fixture = new Fixture();
        fixture.CreateGitDirectory();
        fixture.Labeler.Labels[fixture.Root] = new(true, true, false);
        fixture.Labeler.Labels[fixture.Hooks] = new(true, true, false);
        fixture.Evaluate(3);
        var low = Assert.Single(fixture.Events());
        Assert.Equal("HOST_HEALTH_REPOSITORY_LOW_WRITABLE paths=.,.git/hooks " +
            "remedy=report-only; relabeling needs owner approval", Detail(low));
        Assert.Equal("decision", low.GetProperty("operator").GetString());
        Assert.Equal(fixture.Clock.GetUtcNow(), low.GetProperty("timestamp").GetDateTimeOffset());

        fixture.Labeler.Labels.Clear();
        fixture.Evaluate(3);
        var events = fixture.Events();
        Assert.Equal(2, events.Length);
        Assert.Equal("HOST_HEALTH_REPOSITORY_LOW_WRITABLE_CLEARED", Detail(events[1]));
        Assert.Equal("outcome", events[1].GetProperty("operator").GetString());
        Assert.Equal(Enumerable.Range(0, 6).SelectMany(_ => new[] { fixture.Root, fixture.Hooks, fixture.Refs }),
            fixture.Labeler.Queries);
        Assert.Equal(0, fixture.Labeler.SetCalls);
        using var state = JsonDocument.Parse(File.ReadAllText(fixture.State));
        Assert.False(state.RootElement.GetProperty("repositoryLowWritable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("pending").ValueKind);
    }

    [Theory]
    [InlineData(".", false)]
    [InlineData(".git/hooks", false)]
    [InlineData(".git/refs", false)]
    [InlineData(".", true)]
    [InlineData(".git/hooks", true)]
    [InlineData(".git/refs", true)]
    public void AnyNativeQueryErrorKeepsStateByteIdentical(string errorPath, bool active)
    {
        using var fixture = new Fixture();
        fixture.CreateGitDirectory();
        File.WriteAllText(fixture.State, active
            ? "{\"isDegraded\":false,\"pending\":null,\"repositoryLowWritable\":true}"
            : "{\"isDegraded\":false,\"pending\":null,\"foregroundLockArmed\":false,\"pagedPoolHigh\":false}");
        var before = File.ReadAllBytes(fixture.State);
        fixture.Labeler.Labels[fixture.Root] = new(true, true, false);
        fixture.Labeler.Labels[Path.GetFullPath(Path.Combine(fixture.Root, errorPath))] =
            new(true, false, false, NativeQueryError: 5);
        fixture.Evaluate(3);
        Assert.Empty(fixture.Events());
        Assert.Equal(before, File.ReadAllBytes(fixture.State));
        Assert.Equal(0, fixture.Labeler.SetCalls);
        Assert.Contains(Path.GetFullPath(Path.Combine(fixture.Root, errorPath)), fixture.Labeler.Queries);
    }

    [Fact]
    public void FileFormGitEntryQueriesOnlyRoot()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, ".git"), "gitdir: ../shared/worktrees/example");
        fixture.Evaluate(1);
        Assert.Equal(new[] { fixture.Root }, fixture.Labeler.Queries);
        Assert.Equal(0, fixture.Labeler.SetCalls);
        Assert.Empty(fixture.Events());
        Assert.False(File.Exists(fixture.State));
    }

    [Fact]
    public void MissingGitEntryQueriesOnlyRoot()
    {
        using var fixture = new Fixture();
        var reading = fixture.Probe.Read();
        Assert.True(reading.IsAvailable);
        Assert.Empty(reading.LowPaths!);
        Assert.Equal(new[] { fixture.Root }, fixture.Labeler.Queries);
        Assert.Equal(0, fixture.Labeler.SetCalls);
    }

    [Fact]
    public void LowPathsUseOrdinalOrderAndPortableSeparators()
    {
        using var fixture = new Fixture();
        fixture.CreateGitDirectory();
        foreach (var path in new[] { fixture.Refs, fixture.Root, fixture.Hooks })
            fixture.Labeler.Labels[path] = new(true, true, false);
        var reading = fixture.Probe.Read();
        Assert.True(reading.IsAvailable);
        Assert.Equal(new[] { ".", ".git/hooks", ".git/refs" }, reading.LowPaths);
        Assert.Equal(0, fixture.Labeler.SetCalls);
    }

    [Fact]
    public void MissingPathsWithoutNativeErrorAreNotLow()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        fixture.Labeler.Labels[fixture.Hooks] = new(false, false, false);
        fixture.Labeler.Labels[fixture.Refs] = new(false, false, false);
        var reading = fixture.Probe.Read();
        Assert.True(reading.IsAvailable);
        Assert.Empty(reading.LowPaths!);
        Assert.Equal(new[] { fixture.Root, fixture.Hooks, fixture.Refs }, fixture.Labeler.Queries);
    }

    [Fact]
    public void QueryExceptionIsUnavailableAndKeepsState()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.State, "{\"isDegraded\":false,\"pending\":null,\"repositoryLowWritable\":true}");
        var before = File.ReadAllBytes(fixture.State);
        fixture.Labeler.ThrowOnQuery = true;
        var reading = fixture.Probe.Read();
        Assert.False(reading.IsAvailable);
        fixture.Evaluate(3);
        Assert.Empty(fixture.Events());
        Assert.Equal(before, File.ReadAllBytes(fixture.State));
        Assert.Equal(0, fixture.Labeler.SetCalls);
    }

    private static string Detail(JsonElement record) => record.GetProperty("detail").GetString()!;

    private sealed class RecordingLabeler : IWorkerIntegrityLabeler
    {
        internal Dictionary<string, IntegrityLabelState> Labels { get; } = new(StringComparer.Ordinal);
        internal List<string> Queries { get; } = [];
        internal int SetCalls { get; private set; }
        internal bool ThrowOnQuery { get; set; }
        public IntegrityLabelState Query(string path)
        {
            var fullPath = Path.GetFullPath(path);
            Queries.Add(fullPath);
            if (ThrowOnQuery) throw new IOException("injected query failure");
            return Labels.TryGetValue(fullPath, out var reading) ? reading : new(true, false, false);
        }
        public bool SetIntegrity(string path, string level, bool recursive)
        {
            SetCalls++;
            return false;
        }
    }

    private sealed class UnavailableForegroundReader : IForegroundLockReader
    {
        public ForegroundLockReading Read() => ForegroundLockReading.Unavailable("not-configured");
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(123);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "host-health-integrity", Guid.NewGuid().ToString("N"));
        internal string Hooks => Path.Combine(Root, ".git", "hooks");
        internal string Refs => Path.Combine(Root, ".git", "refs");
        internal string State => Path.Combine(Root, ConductorHostHealthMonitor.StateFileName);
        internal string Log => Path.Combine(Root, "events.log");
        internal RecordingLabeler Labeler { get; } = new();
        internal FixedClock Clock { get; } = new();
        internal RepositoryIntegrityProbe Probe => new(Root, Labeler);
        internal Fixture() => Directory.CreateDirectory(Root);
        internal void CreateGitDirectory()
        {
            Directory.CreateDirectory(Hooks);
            Directory.CreateDirectory(Refs);
        }
        internal void Evaluate(int count)
        {
            var monitor = new ConductorHostHealthMonitor(Path.Combine(Root, "missing-ledger.jsonl"), State,
                new ConductEventLogWriter(Log), signals: new(new UnavailableForegroundReader(), Clock,
                    RepositoryIntegrity: Probe));
            for (var index = 0; index < count; index++) monitor.Evaluate();
        }
        internal JsonElement[] Events() => File.Exists(Log)
            ? File.ReadAllLines(Log).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).ToArray() : [];
        public void Dispose() => Directory.Delete(Root, true);
    }
}
