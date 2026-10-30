using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = NodeProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: each case owns its target and project-home files; no processes are spawned.
public sealed class NodeStackSelectionTests
{
    [Fact]
    public void Detect_NecromancerMarkers_SelectsNodeAdapter()
    {
        using var fixture = new Fixture();

        var detection = ProjectDiscoveryAdapterSelector.Detect(fixture.Root);

        Assert.Equal(ProjectStack.Node, detection.Stack);
        Assert.Equal("package.json", detection.Marker);
        Assert.Null(detection.Refusal);
        using var crosswalk = new FakeCrosswalkProcess();
        Assert.IsType<NodeProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));
    }

    [Fact]
    public void Detect_PackageJsonAlone_SelectsNode()
    {
        using var fixture = new Fixture();
        fixture.Write("standalone/package.json", "{}");

        Assert.Equal(ProjectStack.Node, ProjectDiscoveryAdapterSelector.Detect(Path.Combine(fixture.Root, "standalone")).Stack);
    }

    [Theory]
    [InlineData("go.mod")]
    [InlineData("Root.sln")]
    [InlineData("Root.slnx")]
    [InlineData("src/App/App.csproj")]
    [InlineData("src/App/App.fsproj")]
    [InlineData("src/App/App.vbproj")]
    public void Detect_ConflictingStack_RefusesAmbiguity(string marker)
    {
        using var fixture = new Fixture();
        fixture.Write(marker, marker == "go.mod" ? "module example.com/app\n" : "");

        var detection = ProjectDiscoveryAdapterSelector.Detect(fixture.Root);

        Assert.Null(detection.Stack);
        Assert.Null(detection.Marker);
        Assert.StartsWith("Ambiguous stack markers", detection.Refusal, StringComparison.Ordinal);
        Assert.Contains("package.json", detection.Refusal, StringComparison.Ordinal);
        Assert.Contains(marker, detection.Refusal, StringComparison.Ordinal);
        using var crosswalk = new FakeCrosswalkProcess();
        var refusal = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));
        Assert.Contains(detection.Refusal, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_ExcludedProjectMarker_PreservesNodeSelection()
    {
        using var fixture = new Fixture();
        fixture.Write("vendor/App.csproj", "");

        Assert.Equal(ProjectStack.Node, ProjectDiscoveryAdapterSelector.Detect(fixture.Root, ["vendor"]).Stack);
    }

    [Fact]
    public void Detect_EmptyRoot_RefusalNamesPackageJson()
    {
        using var fixture = new Fixture();
        var empty = Directory.CreateDirectory(Path.Combine(fixture.Root, "empty")).FullName;

        var detection = ProjectDiscoveryAdapterSelector.Detect(empty);

        Assert.Null(detection.Stack);
        Assert.Contains("package.json", detection.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_DeclaredNode_OverridesDotnetMarker(bool fullModel)
    {
        using var fixture = new Fixture();
        fixture.Write("target/App.sln", "");
        var target = Path.Combine(fixture.Root, "target");
        var model = fixture.Discover();
        var json = fullModel ? ProjectModelJson.Serialize(model) : """{"stack":"node"}""";
        fixture.Write("home/project-model.json", json);
        Assert.Equal(Toolchain.Dotnet, TargetToolchainDetector.Detect(target));

        var toolchain = ProjectStackResolver.Resolve(target, Path.Combine(fixture.Root, "home"));

        Assert.Equal(Toolchain.Node, toolchain);
        Assert.NotEqual(Toolchain.Dotnet, toolchain);
    }

    [Theory]
    [InlineData(ProjectStack.Dotnet, "dotnet", 0)]
    [InlineData(ProjectStack.Go, "go", 1)]
    [InlineData(ProjectStack.Node, "node", 2)]
    public void Serialize_StackIdentity_PreservesNamesAndValues(ProjectStack stack, string name, int value)
    {
        Assert.Equal(value, (int)stack);
        Assert.Equal($"\"{name}\"", JsonSerializer.Serialize(stack));
        using var fixture = new Fixture();
        var model = fixture.Discover() with { Stack = stack };
        var json = ProjectModelJson.Serialize(model);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(name, document.RootElement.GetProperty("stack").GetString());
        Assert.Equal(stack, ProjectModelJson.Deserialize(json).Stack);
    }
}
