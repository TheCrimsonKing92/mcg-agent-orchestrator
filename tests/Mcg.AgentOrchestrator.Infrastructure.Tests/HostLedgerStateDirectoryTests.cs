using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its registry, data root and targets; no process route is installed.
public sealed class HostLedgerStateDirectoryTests : IDisposable
{
    private readonly string _root = SharedTestSupport.CreateTempDirectory();
    private string Target => Path.Combine(_root, "target");
    private string DataRoot => Path.Combine(_root, "data");
    private string LegacyDirectory => Path.Combine(Path.GetFullPath(Target), ".orchestrator");
    private OrchestratorProjectRegistry Registry { get; }
    private Func<string, string> Route => host => HostLedgerStateDirectoryLocator.Resolve(host, Registry);

    public HostLedgerStateDirectoryTests()
    {
        Directory.CreateDirectory(Target);
        Registry = new OrchestratorProjectRegistry(Path.Combine(_root, "registry"), DataRoot);
    }

    [Fact]
    public void Resolve_RegisteredProject_RoutesHostAndWorktreeToDataRoot()
    {
        Registry.CreateProject("alpha", Target);

        var directory = HostLedgerStateDirectory.Resolve(Target, Route);

        Assert.Equal(Path.Combine(DataRoot, "projects", "alpha"), directory);
        Assert.Equal(Registry.GetRequiredProject("alpha").ResolveWorkspace().OrchestratorDirectory, directory);
        Assert.False(OrchestratorWorkspace.IsWithinDirectory(directory, Target));
        Assert.Equal(directory, HostLedgerStateDirectory.Resolve(
            Path.Combine(Target, ".orchestrator-worktrees", "g1"), Route));
    }

    [Fact]
    public void Append_RegisteredProject_WritesAllThreeLedgersOnlyToDataRoot()
    {
        Registry.CreateProject("alpha", Target);
        var route = Route;
        var lanePath = GateLaneTimeLedger.ResolveStorePath(Target, route);
        var healthPath = GateHostHealthLedger.ResolveStorePath(Target, route);
        var cpuPath = TickCpuLedger.ResolveStorePath(Target, route);
        var expected = Path.Combine(DataRoot, "projects", "alpha");
        Assert.Equal(Path.Combine(expected, "gate-lane-time.jsonl"), lanePath);
        Assert.Equal(Path.Combine(expected, "host-health.jsonl"), healthPath);
        Assert.Equal(Path.Combine(expected, "tick-cpu.jsonl"), cpuPath);
        Assert.False(Directory.Exists(LegacyDirectory));
        var timestamp = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var health = new HostHealthLedgerRecord(timestamp, "attempt-alpha", 25, 100);
        var cpuLine = JsonSerializer.Serialize(new TickCpuLedgerRecord(timestamp, 1, 5, 1, 1, 3, true));

        GateLaneTimeLedger.Append(lanePath, new GateLaneTimeLedgerRecord(
            timestamp, "attempt-alpha", "goal-alpha", "passed", 100, 75, 25, 1));
        GateHostHealthLedger.Append(healthPath, health);
        Directory.CreateDirectory(Path.GetDirectoryName(cpuPath)!);
        File.AppendAllText(cpuPath, cpuLine + Environment.NewLine);

        Assert.Single(File.ReadAllLines(lanePath));
        Assert.Equal(health, Assert.Single(GateHostHealthLedger.ReadRecent(healthPath)));
        Assert.Equal(cpuLine, Assert.Single(File.ReadAllLines(cpuPath)));
        Assert.False(Directory.Exists(LegacyDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_UnregisteredRoot_PreservesLegacyLedgerPaths(bool unrelatedProject)
    {
        if (unrelatedProject)
        {
            var other = Directory.CreateDirectory(Path.Combine(_root, "other-target")).FullName;
            Registry.CreateProject("beta", other);
        }

        Assert.Equal(LegacyDirectory, HostLedgerStateDirectory.Resolve(Target, Route));
        Assert.Equal(LegacyDirectory, HostLedgerStateDirectory.Resolve(Target, route: null));
        Assert.Equal(LegacyDirectory, HostLedgerStateDirectory.Resolve(Target));
        Assert.Equal(Path.Combine(LegacyDirectory, "gate-lane-time.jsonl"),
            GateLaneTimeLedger.ResolveStorePath(Target));
        Assert.Equal(Path.Combine(LegacyDirectory, "host-health.jsonl"),
            GateHostHealthLedger.ResolveStorePath(Target));
        Assert.Equal(Path.Combine(LegacyDirectory, "tick-cpu.jsonl"),
            TickCpuLedger.ResolveStorePath(Target));
        var projects = Path.Combine(DataRoot, "projects");
        Assert.Empty(Directory.Exists(projects)
            ? Directory.EnumerateFiles(projects, "*", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public void Resolve_DuplicateRegisteredRoot_ReturnsLegacyDirectory()
    {
        Registry.CreateProject("alpha", Target);
        Registry.CreateProject("gamma", Target);
        Assert.Equal(2, Registry.ListProjects().Count);

        Assert.Equal(LegacyDirectory, HostLedgerStateDirectory.Resolve(Target, Route));
    }

    [Fact]
    public void Resolve_MalformedRegistry_ReturnsLegacyDirectory()
    {
        Directory.CreateDirectory(Registry.RegistryDirectory);
        File.WriteAllText(Registry.RegistryPath, "not json");

        Assert.Equal(LegacyDirectory, HostLedgerStateDirectory.Resolve(Target, Route));
    }

    [Fact]
    public void Resolve_ThrowingRoute_ReturnsLegacyDirectory()
    {
        Assert.Equal(LegacyDirectory, HostLedgerStateDirectory.Resolve(Target,
            _ => throw new InvalidOperationException("Route unavailable.")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyRouteResult_ReturnsLegacyDirectory(string? result)
    {
        Assert.Equal(LegacyDirectory, HostLedgerStateDirectory.Resolve(Target, _ => result!));
    }

    [Fact]
    public void Resolve_NewRegistration_RefreshesLegacyRoute()
    {
        var route = Route;
        Assert.False(File.Exists(Registry.RegistryPath));
        Assert.Equal(LegacyDirectory, HostLedgerStateDirectory.Resolve(Target, route));

        var project = Registry.CreateProject("alpha", Target);

        Assert.True(File.Exists(Registry.RegistryPath));
        Assert.Equal(project.ResolveWorkspace().OrchestratorDirectory,
            HostLedgerStateDirectory.Resolve(Target, route));
    }

    [Fact]
    public void Resolve_Worktree_FoldsHostBeforeRoutingAndPreservesResult()
    {
        var calls = 0;
        var expected = Path.Combine(DataRoot, "custom") + Path.DirectorySeparatorChar;
        string RouteHost(string host)
        {
            calls++;
            Assert.Equal(Path.GetFullPath(Target), host);
            return expected;
        }

        Assert.Equal(expected, HostLedgerStateDirectory.Resolve(
            Path.Combine(Target, ".orchestrator-worktrees", "g1"), RouteHost));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Locator_UnchangedRegistry_ReusesRunEventRouteCache(bool registered)
    {
        if (registered) Registry.CreateProject("alpha", Target);
        var expected = registered
            ? Registry.GetRequiredProject("alpha").ResolveWorkspace().OrchestratorDirectory
            : OrchestratorWorkspace.ForDirectory(Target, Target, null, Registry).OrchestratorDirectory;
        var parses = 0;
        IReadOnlyList<OrchestratorProject> List(OrchestratorProjectRegistry registry)
        {
            parses++;
            return registry.ListProjects();
        }

        var first = HostLedgerStateDirectoryLocator.Resolve(Target, Registry, List);
        var second = HostLedgerStateDirectoryLocator.Resolve(Target, Registry, List);

        Assert.Equal(expected, first);
        Assert.Equal(first, second);
        Assert.Equal(first, HostLedgerStateDirectoryLocator.Resolve(Target, Registry));
        Assert.Equal(Path.Combine(first, "run-events.db"),
            GoalOperationRunEventStoreLocator.Resolve(Target, Registry, List));
        Assert.Equal(1, parses);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
