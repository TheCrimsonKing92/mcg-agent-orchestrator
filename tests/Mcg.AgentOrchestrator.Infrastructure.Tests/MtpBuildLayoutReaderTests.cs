using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every case owns a unique temporary source tree and its ancestors.
public sealed class MtpBuildLayoutReaderTests
{
    private const string Project = "tests/A/A.csproj";

    [Fact]
    public void ReadRefusal_PlainSingleFramework_ReturnsNull()
    {
        using var tree = new SourceTree();
        Assert.Null(MtpBuildLayoutReader.ReadRefusal(tree.Root, Project));
    }

    [Theory]
    [InlineData("TargetFrameworks")]
    [InlineData("RuntimeIdentifier")]
    [InlineData("RuntimeIdentifiers")]
    [InlineData("OutputPath")]
    [InlineData("BaseOutputPath")]
    [InlineData("ArtifactsPath")]
    [InlineData("UseArtifactsOutput")]
    [InlineData("ArtifactsPivots")]
    [InlineData("AppendTargetFrameworkToOutputPath")]
    [InlineData("AppendRuntimeIdentifierToOutputPath")]
    public void ReadRefusal_LayoutElement_IdentifiesProjectFileAndElement(string element)
    {
        using var tree = new SourceTree();
        tree.Write(Project, $"<Project><PropertyGroup><{element}>value</{element}></PropertyGroup></Project>");
        var reason = MtpBuildLayoutReader.ReadRefusal(tree.Root, Project);
        Assert.NotNull(reason);
        Assert.Contains(Project, reason);
        Assert.Contains(Path.GetFullPath(Path.Combine(tree.Root, Project)), reason);
        Assert.Contains(element, reason);
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("tests/Directory.Build.targets")]
    public void ReadRefusal_AncestorOverride_IdentifiesBuildFile(string file)
    {
        using var tree = new SourceTree();
        tree.Write(file, "<Project><PropertyGroup><OutputPath>custom</OutputPath></PropertyGroup></Project>");
        var reason = MtpBuildLayoutReader.ReadRefusal(tree.Root, Project);
        Assert.NotNull(reason);
        Assert.Contains(Project, reason);
        Assert.Contains(Path.GetFullPath(Path.Combine(tree.Root, file)), reason);
        Assert.Contains("OutputPath", reason);
    }

    [Fact]
    public void ReadRefusal_OutsideRootProps_AreIgnored()
    {
        using var tree = new SourceTree();
        File.WriteAllText(Path.Combine(tree.Container, "Directory.Build.props"),
            "<Project><OutputPath>outside</OutputPath></Project>");
        Assert.Null(MtpBuildLayoutReader.ReadRefusal(tree.Root, Project));
    }

    [Fact]
    public void ReadRefusal_OutsideRootProject_OnlyReadsProject()
    {
        using var tree = new SourceTree();
        var sibling = tree.Root + "-sibling";
        Directory.CreateDirectory(sibling);
        var project = Path.Combine(sibling, "A.csproj");
        File.WriteAllText(project, "<Project><TargetFramework>net10.0</TargetFramework></Project>");
        File.WriteAllText(Path.Combine(sibling, "Directory.Build.props"),
            "<Project><OutputPath>outside</OutputPath></Project>");
        Assert.Null(MtpBuildLayoutReader.ReadRefusal(tree.Root, project));
        File.WriteAllText(project, "<Project><RuntimeIdentifier>win-x64</RuntimeIdentifier></Project>");
        Assert.Contains("RuntimeIdentifier", MtpBuildLayoutReader.ReadRefusal(tree.Root, project)!);
    }

    [Fact]
    public void ReadRefusal_MissingProject_IdentifiesUnreadableFile()
    {
        using var tree = new SourceTree();
        const string missing = "tests/Missing/Missing.csproj";
        var reason = MtpBuildLayoutReader.ReadRefusal(tree.Root, missing);
        Assert.NotNull(reason);
        Assert.Contains(missing, reason);
        Assert.Contains(Path.GetFullPath(Path.Combine(tree.Root, missing)), reason);
        Assert.Contains("cannot read XML file", reason);
    }

    [Theory]
    [InlineData("tests/A/A.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("tests/Directory.Build.targets")]
    public void ReadRefusal_MalformedXml_RefusesRatherThanSkipping(string file)
    {
        using var tree = new SourceTree();
        tree.Write(file, "<Project>");
        var reason = MtpBuildLayoutReader.ReadRefusal(tree.Root, Project);
        Assert.NotNull(reason);
        Assert.Contains(Project, reason);
        Assert.Contains(Path.GetFullPath(Path.Combine(tree.Root, file)), reason);
        Assert.Contains("cannot read XML file", reason);
    }

    [Fact]
    public void ReadRefusal_ConditionedNamespacedEmptyElement_MatchesIgnoringCase()
    {
        using var tree = new SourceTree();
        tree.Write(Project, "<Project xmlns='urn:msbuild'><Choose><When Condition='false'>"
            + "<PropertyGroup><outputpath /></PropertyGroup></When></Choose></Project>");
        Assert.Contains("outputpath", MtpBuildLayoutReader.ReadRefusal(tree.Root, Project)!);
    }

    [Fact]
    public void ReadRefusal_MultipleOverrides_ProjectThenNearestPropsWins()
    {
        using var tree = new SourceTree();
        tree.Write("Directory.Build.props", "<Project><ArtifactsPath>root</ArtifactsPath></Project>");
        tree.Write("tests/A/Directory.Build.targets", "<Project><OutputPath>targets</OutputPath></Project>");
        tree.Write("tests/A/Directory.Build.props", "<Project><RuntimeIdentifier>props</RuntimeIdentifier></Project>");
        Assert.Contains("RuntimeIdentifier", MtpBuildLayoutReader.ReadRefusal(tree.Root, Project)!);
        tree.Write(Project, "<Project><TargetFrameworks>net8.0;net9.0</TargetFrameworks></Project>");
        Assert.Contains("TargetFrameworks", MtpBuildLayoutReader.ReadRefusal(tree.Root, Project)!);
    }

    private sealed class SourceTree : IDisposable
    {
        public string Container { get; } = Path.Combine(Path.GetTempPath(), "mtp-layout", Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Container, "source");

        public SourceTree() => Write(Project,
            "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        public void Write(string path, string xml)
        {
            var file = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, xml);
        }

        public void Dispose() => Directory.Delete(Container, recursive: true);
    }
}
