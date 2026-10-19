using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each case owns its temp directories and registry; no environment mutation.
public sealed class ConductorAttentionStoreDirectoryTests
{
    [Fact]
    public void ProjectWorkspace_ResolvesUnderDataRootOutsideTarget()
    {
        using var fixture = new RepositoryFixture();
        var workspace = OrchestratorWorkspace.ForProject(
            "alpha", fixture.Home, fixture.Target, dataRootDirectory: fixture.DataRoot);

        var result = ConductorAttentionStoreDirectory.Resolve(workspace, fixture.Target);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(fixture.DataRoot, "projects", "alpha"), result, StringComparer.Ordinal);
        Assert.Equal(workspace.OrchestratorDirectory, result, StringComparer.Ordinal);
        Assert.False(OrchestratorWorkspace.IsWithinDirectory(result, fixture.Target));
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, ".orchestrator")));
        Assert.False(Directory.Exists(result));
    }

    [Fact]
    public void DefaultWorkspace_ResolvesRootOrchestratorDirectory()
    {
        using var fixture = new RepositoryFixture();
        var registry = new OrchestratorProjectRegistry(fixture.RegistryDirectory, fixture.DataRoot);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Target, null, null, registry);

        var result = ConductorAttentionStoreDirectory.Resolve(workspace, fixture.Target);

        Assert.Equal(Path.Combine(fixture.Target, ".orchestrator"), result, StringComparer.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, ".orchestrator")));
    }

    [Fact]
    public void NoWorkspace_FallsBackToExecutionDirectoryStore()
    {
        using var fixture = new RepositoryFixture();

        var result = ConductorAttentionStoreDirectory.Resolve(null, fixture.Target);

        Assert.Equal(Path.Combine(fixture.Target, ".orchestrator"), result, StringComparer.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, ".orchestrator")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoWorkspace_BlankExecutionDirectory_ReturnsNull(string? executionDirectory)
    {
        Assert.Null(ConductorAttentionStoreDirectory.Resolve(null, executionDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Workspace_BlankExecutionDirectory_UsesWorkspaceStore(string? executionDirectory)
    {
        using var fixture = new RepositoryFixture();
        var workspace = OrchestratorWorkspace.ForProject(
            "alpha", fixture.Home, fixture.Target, dataRootDirectory: fixture.DataRoot);

        var result = ConductorAttentionStoreDirectory.Resolve(workspace, executionDirectory);

        Assert.Equal(workspace.OrchestratorDirectory, result, StringComparer.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, ".orchestrator")));
        Assert.False(Directory.Exists(workspace.OrchestratorDirectory));
    }

    private sealed class RepositoryFixture : IDisposable
    {
        public string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Target => Path.Combine(Root, "target");
        public string Home => Path.Combine(Root, "home");
        public string DataRoot => Path.Combine(Root, "data");
        public string RegistryDirectory => Path.Combine(Root, "registry");

        public RepositoryFixture()
        {
            Directory.CreateDirectory(Target);
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(RegistryDirectory);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
