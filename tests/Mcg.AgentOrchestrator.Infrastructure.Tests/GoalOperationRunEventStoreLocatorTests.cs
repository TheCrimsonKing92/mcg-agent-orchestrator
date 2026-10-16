using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its registry, data root, targets and SQLite files.
public sealed class GoalOperationRunEventStoreLocatorTests : IDisposable
{
    private readonly string _root = SharedTestSupport.CreateTempDirectory();
    private string Target => Path.Combine(_root, "target");
    private string DataRoot => Path.Combine(_root, "data");
    private OrchestratorProjectRegistry Registry { get; }
    private static DateTimeOffset Timestamp => new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public GoalOperationRunEventStoreLocatorTests()
    {
        Directory.CreateDirectory(Target);
        Registry = new OrchestratorProjectRegistry(Path.Combine(_root, "registry"), DataRoot);
    }

    [Fact]
    public void Resolve_RegisteredProject_ReturnsDataRootStoreForBothRootSpellings()
    {
        Registry.CreateProject("alpha", Target);

        var path = GoalOperationRunEventStoreLocator.Resolve(Target, Registry);

        Assert.Equal(Path.Combine(DataRoot, "projects", "alpha", "run-events.db"), path);
        Assert.Equal(Registry.GetRequiredProject("alpha").ResolveWorkspace().RunEventStorePath, path);
        Assert.False(OrchestratorWorkspace.IsWithinDirectory(path, Target));
        Assert.Equal(path, GoalOperationRunEventStoreLocator.Resolve(
            Target + Path.DirectorySeparatorChar, Registry));
    }

    [Fact]
    public async Task AppendRunEvent_RegisteredProject_WritesOnlyToProjectStore()
    {
        Registry.CreateProject("alpha", Target);
        var path = Registry.GetRequiredProject("alpha").ResolveWorkspace().RunEventStorePath;
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        var entry = Entry();

        GoalOperationJournal.AppendRunEvent(Target, entry, Registry);

        var row = Assert.Single(await new SqliteRunEventStore(path).ReadSinceAsync(goalId: entry.GoalId.Value));
        Assert.Equal(RunEventTypes.GoalOperation, row.EventType);
        Assert.Equal(entry.GoalId.Value, row.GoalId);
        Assert.Equal("conductor:dispatch", row.Operation);
        Assert.Equal("Begin", row.Status);
        Assert.Equal(entry.Detail, row.Detail);
        Assert.Equal(entry.At, row.OccurredAt);
        Assert.Contains(entry.IdempotencyKey, row.PayloadJson!);
        Assert.False(File.Exists(LegacyPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_UnregisteredRoot_PreservesLegacyRouteAndAppend(bool unrelatedProject)
    {
        if (unrelatedProject)
        {
            var other = Directory.CreateDirectory(Path.Combine(_root, "other-target")).FullName;
            Registry.CreateProject("beta", other);
        }

        Assert.Equal(LegacyPath, GoalOperationRunEventStoreLocator.Resolve(Target, Registry));
        var entry = Entry();
        GoalOperationJournal.AppendRunEvent(Target, entry, Registry);

        Assert.True(File.Exists(LegacyPath));
        var row = Assert.Single(await new SqliteRunEventStore(LegacyPath)
            .ReadSinceAsync(goalId: entry.GoalId.Value));
        Assert.Equal(RunEventTypes.GoalOperation, row.EventType);
        Assert.Equal(entry.Operation, row.Operation);
        Assert.Equal(entry.Status.ToString(), row.Status);
        var projects = Path.Combine(DataRoot, "projects");
        Assert.Empty(Directory.Exists(projects)
            ? Directory.EnumerateFiles(projects, "run-events.db", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public void Resolve_DuplicateRegisteredRoot_ReturnsLegacyPath()
    {
        Registry.CreateProject("alpha", Target);
        Registry.CreateProject("gamma", Target);
        Assert.Equal(2, Registry.ListProjects().Count);

        Assert.Equal(LegacyPath, GoalOperationRunEventStoreLocator.Resolve(Target, Registry));
    }

    [Fact]
    public void Resolve_MalformedRegistry_CachesFallbackUntilRepair()
    {
        Directory.CreateDirectory(Registry.RegistryDirectory);
        File.WriteAllText(Registry.RegistryPath, "not json");
        var parses = 0;
        IReadOnlyList<OrchestratorProject> List(OrchestratorProjectRegistry registry)
        {
            parses++;
            return registry.ListProjects();
        }

        Assert.Equal(LegacyPath, GoalOperationRunEventStoreLocator.Resolve(Target, Registry, List));
        Assert.Equal(LegacyPath, GoalOperationRunEventStoreLocator.Resolve(Target, Registry, List));
        Assert.Equal(1, parses);

        File.Delete(Registry.RegistryPath);
        var project = Registry.CreateProject("alpha", Target);
        Assert.Equal(project.ResolveWorkspace().RunEventStorePath,
            GoalOperationRunEventStoreLocator.Resolve(Target, Registry, List));
        Assert.Equal(2, parses);
    }

    [Fact]
    public void Resolve_RecordedDefaultLocation_UsesWorkspaceStoreBesideState()
    {
        Registry.RecordDefaultStateLocation(new DefaultProjectStateLocation(
            Target, DefaultProjectStateLocation.DestinationSubpath(Target),
            ".orchestrator.backup-fixture", Timestamp));
        var workspace = OrchestratorWorkspace.ForDirectory(Target, Target, null, Registry);

        var path = GoalOperationRunEventStoreLocator.Resolve(Target, Registry);

        Assert.Equal(workspace.RunEventStorePath, path);
        Assert.Equal(Path.GetDirectoryName(workspace.SqliteStatePath), Path.GetDirectoryName(path));
        Assert.NotEqual(LegacyPath, path);
        Assert.False(OrchestratorWorkspace.IsWithinDirectory(path, Target));
    }

    [Fact]
    public async Task AppendRunEvent_RegistrationAfterFirstAppend_RefreshesRoute()
    {
        Assert.False(File.Exists(Registry.RegistryPath));
        Assert.Equal(LegacyPath, GoalOperationRunEventStoreLocator.Resolve(Target, Registry));
        var before = Entry();
        GoalOperationJournal.AppendRunEvent(Target, before, Registry);

        var project = Registry.CreateProject("alpha", Target);
        var expected = project.ResolveWorkspace().RunEventStorePath;
        Assert.Equal(expected, GoalOperationRunEventStoreLocator.Resolve(Target, Registry));
        var after = Entry();
        GoalOperationJournal.AppendRunEvent(Target, after, Registry);

        var legacyRows = await new SqliteRunEventStore(LegacyPath).ReadSinceAsync();
        Assert.Equal(before.GoalId.Value, Assert.Single(legacyRows).GoalId);
        var projectRows = await new SqliteRunEventStore(expected).ReadSinceAsync();
        Assert.Equal(after.GoalId.Value, Assert.Single(projectRows).GoalId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_UnchangedRegistry_ListsProjectsOnce(bool registered)
    {
        if (registered) Registry.CreateProject("alpha", Target);
        var parses = 0;
        IReadOnlyList<OrchestratorProject> List(OrchestratorProjectRegistry registry)
        {
            parses++;
            return registry.ListProjects();
        }

        var first = GoalOperationRunEventStoreLocator.Resolve(Target, Registry, List);
        var second = GoalOperationRunEventStoreLocator.Resolve(
            Target + Path.DirectorySeparatorChar, Registry, List);
        Assert.Equal(first, second);
        Assert.Equal(registered ? Registry.GetRequiredProject("alpha").ResolveWorkspace().RunEventStorePath
            : LegacyPath, first);
        Assert.Equal(1, parses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_RegistryStampChanges_RefreshesOnTimeOrLength(bool changeLength)
    {
        Registry.CreateProject("alpha", Target);
        File.SetLastWriteTimeUtc(Registry.RegistryPath, Timestamp.UtcDateTime);
        var original = new FileInfo(Registry.RegistryPath);
        var originalTime = original.LastWriteTimeUtc;
        var originalLength = original.Length;
        var parses = 0;
        IReadOnlyList<OrchestratorProject> List(OrchestratorProjectRegistry registry)
        {
            parses++;
            return registry.ListProjects();
        }
        var first = GoalOperationRunEventStoreLocator.Resolve(Target, Registry, List);
        Assert.Equal(1, parses);

        if (changeLength)
        {
            var other = Directory.CreateDirectory(Path.Combine(_root, "other-target")).FullName;
            Registry.CreateProject("beta", other);
            File.SetLastWriteTimeUtc(Registry.RegistryPath, originalTime);
            Assert.NotEqual(originalLength, new FileInfo(Registry.RegistryPath).Length);
            Assert.Equal(originalTime, File.GetLastWriteTimeUtc(Registry.RegistryPath));
        }
        else
        {
            File.SetLastWriteTimeUtc(Registry.RegistryPath, originalTime.AddDays(1));
            Assert.Equal(originalLength, new FileInfo(Registry.RegistryPath).Length);
            Assert.NotEqual(originalTime, File.GetLastWriteTimeUtc(Registry.RegistryPath));
        }

        Assert.Equal(first, GoalOperationRunEventStoreLocator.Resolve(Target, Registry, List));
        Assert.Equal(2, parses);
    }

    [Fact]
    public void Resolve_SeparateRegistryFilesForSameTarget_KeepIndependentRoutes()
    {
        var alpha = Registry.CreateProject("alpha", Target);
        var otherRegistry = new OrchestratorProjectRegistry(Path.Combine(_root, "other-registry"), DataRoot);
        var beta = otherRegistry.CreateProject("beta", Target);

        Assert.Equal(alpha.ResolveWorkspace().RunEventStorePath,
            GoalOperationRunEventStoreLocator.Resolve(Target, Registry));
        Assert.Equal(beta.ResolveWorkspace().RunEventStorePath,
            GoalOperationRunEventStoreLocator.Resolve(Target, otherRegistry));
    }

    private string LegacyPath => Path.Combine(Path.GetFullPath(Target), ".orchestrator", "run-events.db");

    private static GoalOperationJournalEntry Entry() => new(
        Guid.NewGuid().ToString("N"), GoalId.New(), "conductor:dispatch", GoalOperationStatus.Begin,
        Timestamp, "Starting subscription dispatch.");

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
