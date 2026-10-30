using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = LuaProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: every fixture and tool path is unique; process calls are injected.
public sealed class RootMarkerSelectionWithoutCrosswalkTests
{
    [Theory]
    [InlineData("go-module", typeof(GoProjectDiscoveryAdapter))]
    [InlineData("solution", typeof(DotnetProjectDiscoveryAdapter))]
    [InlineData("no-solution", typeof(DotnetProjectDiscoveryAdapter))]
    public void Select_RootMarkerFixture_DoesNotLaunchCrosswalk(string name, Type adapterType)
    {
        using var fixture = new ProjectOnboardingFixture(name);
        using var crosswalk = new FakeCrosswalkProcess();

        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader);

        Assert.Equal(adapterType, adapter.GetType());
        Assert.Equal(0, crosswalk.VersionCalls);
        Assert.Empty(crosswalk.Calls);
    }

    [Theory]
    [InlineData("package.json", null)]
    [InlineData("go.mod|Root.sln", "Ambiguous stack markers: go.mod and Root.sln.")]
    [InlineData("go.mod|Root.slnx", "Ambiguous stack markers: go.mod and Root.slnx.")]
    [InlineData("go.mod|package.json", "Ambiguous stack markers: go.mod and package.json.")]
    [InlineData("package.json|Root.sln", "Ambiguous stack markers: package.json and Root.sln.")]
    [InlineData("package.json|src/App.csproj", "Ambiguous stack markers: package.json and src/App.csproj.")]
    public void Select_NodeOrAmbiguity_DoesNotLaunchCrosswalk(string paths, string? refusal)
    {
        using var fixture = new Fixture();
        foreach (var path in paths.Split('|')) fixture.Write(path);
        using var crosswalk = new FakeCrosswalkProcess();

        if (refusal is null)
            Assert.IsType<NodeProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));
        else
        {
            Assert.Equal(refusal, ProjectDiscoveryAdapterSelector.Detect(fixture.Root).Refusal);
            var error = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));
            Assert.Equal($"No discovery adapter is available for this repository: {refusal}", error.Message);
        }
        Assert.Equal(0, crosswalk.VersionCalls);
        Assert.Empty(crosswalk.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Select_MissingTool_ReportsAttemptedPath(bool explicitPath)
    {
        using var fixture = new Fixture();
        var dataRoot = Path.Combine(fixture.Root, "data");
        var path = explicitPath ? Path.Combine(dataRoot, "missing.exe") : Path.Combine(dataRoot, "tools", "crosswalk", "crosswalk.exe");
        var resolver = new CrosswalkExecutableResolver(explicitPath ? path : null, dataRoot,
            (_, _) => throw new InvalidOperationException("Missing tool must never launch."));
        var reader = new CrosswalkStackReader(resolver, TimeSpan.FromSeconds(30),
            (_, _) => throw new InvalidOperationException("Missing tool must never launch."));

        var error = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(fixture.Root, reader));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }
}
