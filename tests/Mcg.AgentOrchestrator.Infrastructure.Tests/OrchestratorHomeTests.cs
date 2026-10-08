using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: environment values are injected and no directories are modified.
public sealed class OrchestratorHomeTests
{
    private static readonly string DefaultRoot = Path.Combine(Path.GetTempPath(), "orchestrator-home-tests");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void UnsetHomeUsesUnprojectedRoot(string? configured)
    {
        var home = OrchestratorHome.Resolve(DefaultRoot, name =>
        {
            Assert.Equal("MCG_ORCHESTRATOR_HOME", name);
            return configured;
        });
        Assert.Equal(Path.GetFullPath(DefaultRoot), home.RootDirectory);
        Assert.True(home.IsHome(OrchestratorWorkspace.ForDirectory(DefaultRoot)));
        Assert.False(home.IsHome(OrchestratorWorkspace.ForProject("alpha", DefaultRoot + "-target")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredHomeIsExpandedAndIndependentOfDefault(bool relative)
    {
        var configured = relative ? Path.Combine("relative-home", "nested") : DefaultRoot + "-configured";
        var home = OrchestratorHome.Resolve(DefaultRoot, _ => "  " + configured + "  ");
        Assert.Equal(Path.GetFullPath(configured), home.RootDirectory);
        Assert.True(home.IsHome(OrchestratorWorkspace.ForDirectory(Path.GetFullPath(configured))));
        Assert.False(home.IsHome(OrchestratorWorkspace.ForDirectory(DefaultRoot)));
    }

    [Fact]
    public void HomeComparisonUsesFullExecutionPathsAndIgnoresCaseAndSeparators()
    {
        var home = OrchestratorHome.Resolve(DefaultRoot, _ => null);
        var equivalent = Path.Combine(DefaultRoot, "child", "..").ToUpperInvariant() + Path.DirectorySeparatorChar;
        Assert.True(home.IsHome(equivalent));
        if (OperatingSystem.IsWindows())
            Assert.True(home.IsHome(equivalent.Replace('\\', '/')));
        Assert.False(home.IsHome(DefaultRoot + "-sibling"));
        Assert.False(home.IsHome(OrchestratorWorkspace.ForDirectory(DefaultRoot, DefaultRoot + "-execution")));
        Assert.True(home.IsHome(OrchestratorWorkspace.ForProject("alpha", DefaultRoot + "-state", DefaultRoot)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("relative-home")]
    public void DescendantsReceiveResolvedHomeBeforeChangingDirectory(string? configured)
    {
        var exported = new Dictionary<string, string?>();
        OrchestratorHome.ExportForDescendants(DefaultRoot, _ => configured,
            (name, value) => exported.Add(name, value));
        Assert.Single(exported);
        Assert.Equal(Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? DefaultRoot : configured),
            exported[OrchestratorHome.EnvironmentVariable]);
        var childHome = OrchestratorHome.Resolve(DefaultRoot + "-target", name => exported[name]);
        Assert.Equal(exported[OrchestratorHome.EnvironmentVariable], childHome.RootDirectory);
    }
}
