using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns all directories, SQLite stores and console capture.
public sealed class ProjectStateRelocationCliTests : HostCapacityBoundTestBase
{
    [Fact]
    public void FreshCreateAndShowPrintDataPathsWithoutWritingIntoTarget()
    {
        using var fixture = new Fixture();
        var before = Snapshot(fixture.Target);
        var output = InfrastructureTestSupport.CaptureConsole(() => Assert.Equal(0, fixture.Create()));
        var workspace = fixture.Registry.GetRequiredProject("alpha").ResolveWorkspace();
        Assert.Equal(before, Snapshot(fixture.Target));
        Assert.Equal(Path.Combine(fixture.Data, "projects", "alpha"), workspace.OrchestratorDirectory);
        Assert.True(File.Exists(workspace.SqliteStatePath));
        Assert.True(File.Exists(workspace.BacklogStorePath));
        Assert.Contains($"Workspace: {workspace.OrchestratorDirectory}", output);
        Assert.Contains($"State: {workspace.SqliteStatePath}", output);
        var shown = InfrastructureTestSupport.CaptureConsole(() => Assert.Equal(0,
            ProjectCliCommand.Execute(["project", "show", "alpha"], fixture.Registry, fixture.Home, null)));
        Assert.Contains($"Workspace: {workspace.OrchestratorDirectory}", shown);
        Assert.Contains($"State: {workspace.SqliteStatePath}", shown);
    }

    [Fact]
    public void LegacyRegistrationRequiresFlagWithoutMutation()
    {
        using var fixture = new Fixture();
        fixture.SeedLegacy();
        var before = Snapshot(fixture.Target);
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Create());
        Assert.Contains("--relocate-state", error.Message);
        Assert.Equal(before, Snapshot(fixture.Target));
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public void RelocationPreservesConfigurationStoresAndRemovesEmptyLegacyParents()
    {
        using var fixture = new Fixture();
        fixture.SeedLegacy();
        var before = Snapshot(fixture.Legacy);
        Assert.Equal(0, fixture.Create(relocate: true));
        Assert.Equal(before, Snapshot(fixture.Destination));
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, ".orchestrator")));
        Assert.True(File.Exists(Path.Combine(fixture.Destination, "state.db")));
        Assert.True(File.Exists(Path.Combine(fixture.Destination, "backlog.db")));
        Assert.Equal(fixture.Destination, fixture.Registry.GetRequiredProject("alpha").ResolveWorkspace().OrchestratorDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelocationRefusesGoalsInRootOrTenantWithoutMutation(bool tenant)
    {
        using var fixture = new Fixture();
        fixture.SeedLegacy();
        var database = tenant ? Path.Combine(fixture.Legacy, "tenants", "t1", "state.db")
            : Path.Combine(fixture.Legacy, "state.db");
        StateDbMigrations.EnsureUpToDate(database);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("Existing goal blocks relocation");
        await new SqliteOrchestratorStateRepository(database).SaveAsync(kernel);
        var before = Snapshot(fixture.Target);
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Create(relocate: true));
        Assert.Contains("contains goals", error.Message);
        Assert.Equal(before, Snapshot(fixture.Target));
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public void RelocationRefusesUnreadableSchemaAndOccupiedDestination()
    {
        using var fixture = new Fixture();
        fixture.SeedLegacy();
        Directory.CreateDirectory(fixture.Destination);
        File.WriteAllText(Path.Combine(fixture.Destination, "keep.txt"), "destination");
        var before = Snapshot(fixture.Target);
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Create(relocate: true));
        Assert.Contains("directory is not empty", error.Message);
        Assert.Equal(before, Snapshot(fixture.Target));
        Assert.Equal("destination", File.ReadAllText(Path.Combine(fixture.Destination, "keep.txt")));
        Directory.Delete(fixture.Destination, recursive: true);
        File.WriteAllText(Path.Combine(fixture.Legacy, "state.db"), "not a SQLite database");
        before = Snapshot(fixture.Target);
        error = Assert.Throws<InvalidOperationException>(() => fixture.Create(relocate: true));
        Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(error.InnerException);
        Assert.Equal(before, Snapshot(fixture.Target));
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public void RelocateFlagRequiresRegistrationAndInsideTargetDataRootIsRejected()
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Create(relocate: true));
        Assert.Contains("existing registration", error.Message);
        Assert.Empty(fixture.Registry.ListProjects());
        var registry = new OrchestratorProjectRegistry(Path.Combine(fixture.Root, "other-registry"),
            Path.Combine(fixture.Target, "data"));
        Assert.Throws<InvalidOperationException>(() => ProjectCliCommand.Execute(
            ["project", "create", "alpha", "--root", fixture.Target], registry, fixture.Home, null));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Target));
        Assert.False(File.Exists(registry.RegistryPath));
    }

    [Fact]
    public void RelocationPreservesUnrelatedTargetArtifacts()
    {
        using var fixture = new Fixture();
        fixture.SeedLegacy();
        var other = Path.Combine(fixture.Target, ".orchestrator", "keep.txt");
        File.WriteAllText(other, "unrelated");
        Assert.Equal(0, fixture.Create(relocate: true));
        Assert.False(Directory.Exists(fixture.Legacy));
        Assert.Equal("unrelated", File.ReadAllText(other));
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .Concat(Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => "directory:" + Path.GetRelativePath(root, path))).ToArray();

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Home => Path.Combine(Root, "home");
        public string Target => Path.Combine(Root, "target");
        public string Data => Path.Combine(Root, "data");
        public string Legacy => OrchestratorWorkspace.LegacyProjectDirectory(Target, "alpha");
        public string Destination => Path.Combine(Data, "projects", "alpha");
        public OrchestratorProjectRegistry Registry { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Target);
            Registry = new OrchestratorProjectRegistry(Path.Combine(Root, "registry"), Data);
            var source = OrchestratorWorkspace.ForDirectory(Home);
            ModelFunctionCatalogStore.Save(source.ModelFunctionCatalogPath, new ModelFunctionCatalog([
                new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                    new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                    Name: ModelFunctionPurposes.SpecRefiner)
            ]));
            AgentCatalogStore.Save(source.AgentCatalogPath, AgentCatalog.Default());
            WorkerProfileStore.Save(source.WorkerProfilePath, WorkerProfileCatalog.Default());
        }

        public void SeedLegacy()
        {
            Registry.CreateProject("alpha", Target);
            Directory.CreateDirectory(Legacy);
            foreach (var name in new[] { "agents.json", "workers.json", "model-functions.json" })
                File.Copy(Path.Combine(Home, ".orchestrator", name), Path.Combine(Legacy, name));
            StateDbMigrations.EnsureUpToDate(Path.Combine(Legacy, "state.db"));
            _ = new BacklogStore(Path.Combine(Legacy, "backlog.db"));
        }

        public int Create(bool relocate = false)
        {
            var args = new List<string> { "project", "create", "alpha", "--root", Target };
            if (relocate) args.Add("--relocate-state");
            return ProjectCliCommand.Execute(args, Registry, Home, null);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
