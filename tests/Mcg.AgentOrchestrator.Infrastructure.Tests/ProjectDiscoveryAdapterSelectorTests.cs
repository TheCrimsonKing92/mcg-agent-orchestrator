using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its fixture or temporary root; no process is launched.
public sealed class ProjectDiscoveryAdapterSelectorTests
{
    [Theory]
    [InlineData("solution", "Sample.sln")]
    [InlineData("no-solution", "src/Loose.Library/Loose.Library.csproj")]
    public void Detect_DotnetFixture_ReturnsExactMarker(string fixtureName, string marker)
    {
        using var fixture = new ProjectOnboardingFixture(fixtureName);

        var detection = ProjectDiscoveryAdapterSelector.Detect(fixture.Root, RepositorySourceInventory.ExcludedDirectoryNames);

        Assert.Equal(ProjectStack.Dotnet, detection.Stack);
        Assert.Equal(marker, detection.Marker);
        Assert.Null(detection.Refusal);
    }

    [Fact]
    public void Detect_GoModOnly_ReturnsGoMarker()
    {
        using var root = new TemporaryRoot();
        root.Write("go.mod");

        var detection = ProjectDiscoveryAdapterSelector.Detect(root.Path);

        Assert.Equal(ProjectStack.Go, detection.Stack);
        Assert.Equal("go.mod", detection.Marker);
        Assert.Null(detection.Refusal);
    }

    [Theory]
    [InlineData("Root.sln")]
    [InlineData("Root.slnx")]
    public void Detect_GoModAndRootSolution_RefusesBothMarkers(string solution)
    {
        using var root = new TemporaryRoot();
        root.Write("go.mod");
        root.Write(solution);

        var detection = ProjectDiscoveryAdapterSelector.Detect(root.Path);

        Assert.Null(detection.Stack);
        Assert.Null(detection.Marker);
        Assert.Contains("Ambiguous", detection.Refusal);
        Assert.Contains("go.mod", detection.Refusal);
        Assert.Contains(solution, detection.Refusal);
    }

    [Fact]
    public void Detect_EmptyRoot_RefusesAndListsMarkers()
    {
        using var root = new TemporaryRoot();

        AssertNoMarker(ProjectDiscoveryAdapterSelector.Detect(root.Path));
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".git")]
    [InlineData("custom-excluded")]
    public void Detect_ProjectUnderExcludedDirectory_Refuses(string directory)
    {
        using var root = new TemporaryRoot();
        root.Write($"src/{directory}/Only.csproj");

        AssertNoMarker(ProjectDiscoveryAdapterSelector.Detect(root.Path, ["custom-excluded"]));
    }

    [Fact]
    public void Detect_MissingRoot_ThrowsDirectoryNotFound()
    {
        using var root = new TemporaryRoot();
        var missing = System.IO.Path.Combine(root.Path, "missing");

        var exception = Assert.Throws<DirectoryNotFoundException>(() => ProjectDiscoveryAdapterSelector.Detect(missing));

        Assert.Equal($"Repository root does not exist: {missing}", exception.Message);
    }

    [Fact]
    public void Detect_MultipleSolutions_ChoosesOrdinalFirstBeforeProjects()
    {
        using var root = new TemporaryRoot();
        root.Write("Z.sln");
        root.Write("A.slnx");
        root.Write("0.csproj");

        var detection = ProjectDiscoveryAdapterSelector.Detect(root.Path);

        Assert.Equal(ProjectStack.Dotnet, detection.Stack);
        Assert.Equal("A.slnx", detection.Marker);
        Assert.Null(detection.Refusal);
    }

    [Theory]
    [InlineData("csproj")]
    [InlineData("fsproj")]
    [InlineData("vbproj")]
    public void Detect_DeepProjects_OrdersNormalizedRelativePaths(string extension)
    {
        using var root = new TemporaryRoot();
        root.Write($"src/aZ.{extension}");
        root.Write($"src/a/deep/First.{extension}");

        var detection = ProjectDiscoveryAdapterSelector.Detect(root.Path);

        Assert.Equal(ProjectStack.Dotnet, detection.Stack);
        Assert.Equal($"src/a/deep/First.{extension}", detection.Marker);
        Assert.Null(detection.Refusal);
    }

    [Fact]
    public void Detect_GoModWithNestedDotnetMarkers_PrefersGo()
    {
        using var root = new TemporaryRoot();
        root.Write("go.mod");
        root.Write("nested/Other.sln");
        root.Write("nested/Other.csproj");

        var detection = ProjectDiscoveryAdapterSelector.Detect(root.Path);

        Assert.Equal(ProjectStack.Go, detection.Stack);
        Assert.Equal("go.mod", detection.Marker);
        Assert.Null(detection.Refusal);
    }

    [Theory]
    [InlineData("go.mod", "go.mod")]
    [InlineData("ambiguous", "Root.sln")]
    public void Select_UnsupportedOrRefusedStack_ThrowsWithDetectionReason(string setup, string reason)
    {
        using var root = new TemporaryRoot();
        if (setup.Length > 0)
            root.Write("go.mod");
        if (setup == "ambiguous")
            root.Write("Root.sln");

        using var crosswalk = new FakeCrosswalkProcess();
        var exception = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(root.Path, crosswalk.Reader));

        Assert.Contains("No discovery adapter is available", exception.Message);
        Assert.Contains(reason, exception.Message);
    }

    [Fact]
    public void Select_NoMarker_ReturnsDeclaredAdapter()
    {
        using var root = new TemporaryRoot();
        using var crosswalk = new FakeCrosswalkProcess();
        Assert.IsType<DeclaredProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(root.Path, crosswalk.Reader));
        AssertNoMarker(ProjectDiscoveryAdapterSelector.Detect(root.Path));
    }

    [Theory]
    [InlineData("solution")]
    [InlineData("no-solution")]
    [InlineData("answer-key-repository")]
    public void Select_DotnetFixture_PreservesDirectAdapterModel(string fixtureName)
    {
        using var fixture = new ProjectOnboardingFixture(fixtureName);
        var exclusions = RepositorySourceInventory.ExcludedDirectoryNames;

        using var crosswalk = new FakeCrosswalkProcess();
        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader, exclusions);
        var selected = adapter.Discover(fixture.Root, exclusions, null, UnitCommandKinds.None);
        var direct = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root, exclusions, null, UnitCommandKinds.None);

        Assert.IsType<DotnetProjectDiscoveryAdapter>(adapter);
        Assert.Equal(ProjectStack.Dotnet, selected.Stack);
        Assert.Equal(ProjectModelJson.Serialize(direct), ProjectModelJson.Serialize(selected));
    }

    private static void AssertNoMarker(ProjectDiscoveryAdapterSelector.Detection detection)
    {
        Assert.Null(detection.Stack);
        Assert.Null(detection.Marker);
        Assert.Contains("No stack marker", detection.Refusal);
        foreach (var marker in new[] { "go.mod", "*.sln", "*.slnx", "*.csproj", "*.fsproj", "*.vbproj" })
            Assert.Contains(marker, detection.Refusal);
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "project-selector", Guid.NewGuid().ToString("N"));

        public TemporaryRoot() => Directory.CreateDirectory(Path);

        public void Write(string relativePath)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
