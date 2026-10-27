using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its target and project-home directories and file handles.
public sealed class ProjectStackResolverDecisionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stack-resolver-");

    public static IEnumerable<object?[]> MarkerFallbackCases()
    {
        string[] markers = ["go.mod", "package.json", "App.sln", "src/App/App.csproj",
            "pyproject.toml", "requirements.txt", "", "go.mod|App.sln"];
        string?[] declarations = [null, "", "{\"schemaVersion\":4}", "{invalid", "{\"stack\":\"rust\"}",
            "[]", "null", "{\"stack\":null}", "{\"stack\":1}", "{\"stack\":true}",
            "{\"stack\":\"1\"}", "{\"stack\":\"0\"}", "{\"stack\":\"999\"}", "{\"Stack\":\"go\"}"];
        foreach (var marker in markers)
        foreach (var declaration in declarations)
            yield return [marker, declaration];
    }

    [Theory]
    [MemberData(nameof(MarkerFallbackCases))]
    public void Resolve_UnusableDeclaration_PreservesMarkerDetection(string markers, string? declaration)
    {
        var target = CreateDirectory();
        foreach (var marker in markers.Split('|', StringSplitOptions.RemoveEmptyEntries))
            Write(target, marker, string.Empty);
        var projectHome = declaration is null ? null : CreateDirectory();
        if (!string.IsNullOrEmpty(declaration))
            Write(projectHome!, "project-model.json", declaration);

        Assert.Equal(TargetToolchainDetector.Detect(target), ProjectStackResolver.Resolve(target, projectHome));
    }

    [Theory]
    [InlineData(ProjectStack.Go, "App.sln", Toolchain.Go)]
    [InlineData(ProjectStack.Dotnet, "go.mod", Toolchain.Dotnet)]
    public void Resolve_DeclaredStack_OverridesConflictingMarkers(
        ProjectStack stack, string marker, Toolchain expected)
    {
        var target = CreateDirectory();
        Write(target, marker, string.Empty);
        var projectHome = CreateDirectory();
        var model = new ProjectModel(ProjectModel.CurrentSchemaVersion, ".", [], [], [], [], [], [], [], [], Stack: stack);
        Write(projectHome, "project-model.json", ProjectModelJson.Serialize(model));
        Assert.NotEqual(expected, TargetToolchainDetector.Detect(target));

        Assert.Equal(expected, ProjectStackResolver.Resolve(target, projectHome));
    }

    [Theory]
    [InlineData("GO", Toolchain.Go)]
    [InlineData("Dotnet", Toolchain.Dotnet)]
    public void Resolve_ConverterRecognizedCase_UsesDeclaration(string stack, Toolchain expected)
    {
        var target = CreateDirectory();
        var projectHome = CreateDirectory();
        Write(projectHome, "project-model.json", $"{{\"stack\":\"{stack}\"}}");

        Assert.Equal(expected, ProjectStackResolver.Resolve(target, projectHome));
    }

    [Fact]
    public void Resolve_UnreadableModel_FallsBackToMarkers()
    {
        var target = CreateDirectory();
        Write(target, "go.mod", string.Empty);
        var projectHome = CreateDirectory();
        Write(projectHome, "project-model.json", "{\"stack\":\"dotnet\"}");
        using var lockedModel = new FileStream(Path.Combine(projectHome, "project-model.json"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(TargetToolchainDetector.Detect(target), ProjectStackResolver.Resolve(target, projectHome));
    }

    [Fact]
    public void Resolve_MixedMarkers_PreservesDiscoveryRefusal()
    {
        var target = CreateDirectory();
        Write(target, "go.mod", string.Empty);
        Write(target, "App.sln", string.Empty);

        var detection = ProjectDiscoveryAdapterSelector.Detect(target);

        Assert.Null(detection.Stack);
        Assert.StartsWith("Ambiguous stack markers", detection.Refusal, StringComparison.Ordinal);
        Assert.Equal(TargetToolchainDetector.Detect(target), ProjectStackResolver.Resolve(target, null));
    }

    [Fact]
    public void GuidanceStack_AllToolchains_PreservesHomeAndNeutralizesForeign()
    {
        foreach (var value in Enum.GetValues<Toolchain>())
        {
            Assert.Equal(value == Toolchain.Go ? ProjectStack.Go : ProjectStack.Dotnet,
                ProjectStackResolver.GuidanceStack(value, true));
            ProjectStack? expected = value switch
            {
                Toolchain.Dotnet => ProjectStack.Dotnet,
                Toolchain.Go => ProjectStack.Go,
                _ => null
            };
            Assert.Equal(expected, ProjectStackResolver.GuidanceStack(value, false));
        }
    }

    public void Dispose() => _root.Delete(recursive: true);

    private string CreateDirectory() => Directory.CreateDirectory(Path.Combine(_root.FullName, Guid.NewGuid().ToString("N"))).FullName;

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
