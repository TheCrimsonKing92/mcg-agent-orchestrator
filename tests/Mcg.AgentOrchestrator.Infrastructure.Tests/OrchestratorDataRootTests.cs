using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: environment access is injected and each fact owns its directories.
public sealed class OrchestratorDataRootTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void UnsetDataRootUsesExistingRegistryParent(string? configured)
    {
        var root = OrchestratorDataRoot.Resolve(_ => configured);
        var registry = OrchestratorProjectRegistry.CreateDefault(_ => null);
        Assert.Equal(Path.GetDirectoryName(registry.RegistryDirectory), root.RootDirectory);
        Assert.Equal(Path.GetFullPath(OrchestratorDataRoot.ResolveDefaultDirectory()), root.RootDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("relative-data")]
    public void DescendantsReceiveAnAbsoluteDataRoot(string? configured)
    {
        var exported = new Dictionary<string, string?>();
        OrchestratorDataRoot.ExportForDescendants(_ => configured,
            (name, value) => exported.Add(name, value));
        Assert.Single(exported);
        Assert.Equal(Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? OrchestratorDataRoot.ResolveDefaultDirectory() : configured),
            exported[OrchestratorDataRoot.EnvironmentVariable]);
    }

    [Fact]
    public void DataOverrideDoesNotMoveTheRegistry()
    {
        var custom = Path.Combine(Path.GetTempPath(), "custom-data");
        var registry = OrchestratorProjectRegistry.CreateDefault(name =>
            name == OrchestratorDataRoot.EnvironmentVariable ? custom : null);
        Assert.Equal(Path.Combine(OrchestratorDataRoot.ResolveDefaultDirectory(), "projects", "projects.json"),
            registry.RegistryPath);
        Assert.Equal(custom, registry.DataRootDirectory);
    }

    [Fact]
    public void RegisteredProjectResolvesStoresOutsideItsExecutionRoot()
    {
        var temp = SharedTestSupport.CreateTempDirectory();
        try
        {
            var target = Path.Combine(temp, "target");
            Directory.CreateDirectory(target);
            var data = OrchestratorDataRoot.Resolve(name =>
                name == "MCG_ORCHESTRATOR_DATA" ? Path.Combine(temp, "data") : null);
            var registry = new OrchestratorProjectRegistry(Path.Combine(temp, "registry"), data.RootDirectory);
            registry.CreateProject("alpha", target);
            var workspace = registry.GetRequiredProject("alpha").ResolveWorkspace();
            var projectRoot = Path.Combine(data.RootDirectory, "projects", "alpha");
            Assert.Equal(target, workspace.RootDirectory);
            Assert.Equal(target, workspace.ExecutionDirectory);
            Assert.Equal(Path.Combine(projectRoot, "state.db"), workspace.SqliteStatePath);
            Assert.Equal(Path.Combine(projectRoot, "backlog.db"), workspace.BacklogStorePath);
            Assert.Equal(Path.Combine(projectRoot, "logs"), workspace.LogDirectory);
            Assert.Equal(Path.Combine(projectRoot, "dogfood-log.db"), workspace.DogfoodLogStorePath);
            Assert.True(OrchestratorWorkspace.IsWithinDirectory(workspace.ConductEventsLogPath, projectRoot));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally { SharedTestSupport.RemoveTempDirectory(temp); }
    }

    [Fact]
    public void DataRootInsideTargetIsRejectedBeforeAnyWrite()
    {
        var target = Path.Combine(Path.GetTempPath(), "data-root-target");
        Assert.Throws<InvalidOperationException>(() => OrchestratorWorkspace.ForProject("alpha", target,
            dataRootDirectory: Path.Combine(target, "data")));
    }
}
