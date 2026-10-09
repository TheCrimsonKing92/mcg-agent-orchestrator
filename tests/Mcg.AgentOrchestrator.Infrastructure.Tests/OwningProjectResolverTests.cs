using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case uses a unique temporary directory and no global state.
public sealed class OwningProjectResolverTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void NestedProject_SeparatesParentAndCliSources()
    {
        var root = CreateTempDirectory();
        WriteProject(root, "Parent/Parent.csproj", "<Compile Remove=\"Cli\\**\\*.cs\" />");
        WriteProject(root, "Parent/Cli/Nested.csproj");

        Xunit.Assert.Equal(
            ["Parent/Cli/Nested.csproj", "Parent/Parent.csproj"],
            Required(root, "Parent/Cli/NewTests.cs", "Parent/Other.cs"));
        Xunit.Assert.Equal(["Parent/Cli/Nested.csproj"], Required(root, "Parent/Cli/NewTests.cs"));
        Xunit.Assert.Equal(["Parent/Cli/Nested.csproj"], Required(root, "Parent/Cli/Sub/Deep.cs"));

        // With no nested project, the parent's Compile Remove must prevent ownership.
        File.Delete(Path.Combine(root, "Parent", "Cli", "Nested.csproj"));
        Xunit.Assert.Empty(Required(root, "Parent/Cli/NewTests.cs", "Parent/Cli/Sub/Deep.cs"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Generated\\**\\*.cs")]
    [Xunit.InlineData("generated/**/*.CS")]
    [Xunit.InlineData("./Generated/**/*.cs")]
    public void CompileRemove_RecursiveGlobExcludesDirectAndDeepFiles(string pattern)
    {
        var root = CreateTempDirectory();
        WriteProject(root, "Lib/Lib.csproj", $"<Compile Remove=\"{pattern}\" />");

        Xunit.Assert.Empty(Required(root, "Lib/Generated/Gen.cs", "Lib/Generated/Sub/Deep.cs"));
    }

    [Xunit.Fact]
    public void CompileInclude_AfterRemoveReaddsOnlyExactFile()
    {
        var root = CreateTempDirectory();
        WriteProject(root, "Lib/Lib.csproj", """
            <Compile Remove="Generated\**\*.cs" />
            <Compile Include="Generated\Keep.cs" />
            """);

        Xunit.Assert.Equal(["Lib/Lib.csproj"], Required(root, "Lib/Generated/Keep.cs"));
        Xunit.Assert.Empty(Required(root, "Lib/Generated/Gen.cs"));
    }

    [Xunit.Fact]
    public void CompileRemove_AfterIncludeExcludesFile()
    {
        var root = CreateTempDirectory();
        WriteProject(root, "Lib/Lib.csproj", """
            <Compile Include="Keep.cs" />
            <Compile Remove="Keep.cs" />
            """);

        Xunit.Assert.Empty(Required(root, "Lib/Keep.cs"));
    }

    [Xunit.Fact]
    public void NearestProject_RemovesFileAncestorOwnsIt()
    {
        var root = CreateTempDirectory();
        WriteProject(root, "Outer/Outer.csproj");
        WriteProject(root, "Outer/Inner/Inner.csproj", "<Compile Remove=\"Skipped.cs\" />");

        Xunit.Assert.Equal(["Outer/Outer.csproj"], Required(root, "Outer/Inner/Skipped.cs"));
    }

    [Xunit.Fact]
    public void SameDirectory_SkipsExcludedProjectThenUsesOrdinalFirstOwner()
    {
        var root = CreateTempDirectory();
        WriteProject(root, "Lib/A.csproj", "<Compile Remove=\"File.cs\" />");
        WriteProject(root, "Lib/C.csproj");
        WriteProject(root, "Lib/B.csproj");

        Xunit.Assert.Equal(["Lib/B.csproj"], Required(root, "Lib/File.cs"));
    }

    [Xunit.Fact]
    public void CompileRemove_SemicolonListAndSingleStarStayWithinDirectory()
    {
        var root = CreateTempDirectory();
        WriteProject(root, "Lib/Lib.csproj", "<Compile Remove=\"A.cs; B*.cs\" />");

        Xunit.Assert.Empty(Required(root, "Lib/A.cs", "Lib/Beta.cs"));
        Xunit.Assert.Equal(["Lib/Lib.csproj"], Required(root, "Lib/Sub/Beta.cs"));
    }

    [Xunit.Fact]
    public void NamespacedCompileItems_RemoveAndIncludeChangeMembership()
    {
        var root = CreateTempDirectory();
        WriteProject(root, "Lib/Lib.csproj");
        File.WriteAllText(Path.Combine(root, "Lib", "Lib.csproj"), """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Compile Remove="A.cs;B.cs" />
                <Compile Include="A.cs; C.cs" />
              </ItemGroup>
            </Project>
            """);

        Xunit.Assert.Equal(["Lib/Lib.csproj"], Required(root, "Lib/A.cs", "Lib/C.cs"));
        Xunit.Assert.Empty(Required(root, "Lib/B.cs"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("<Compile Update=\"File.cs\" />")]
    [Xunit.InlineData("<Compile Remove=\"$(Generated)/**/*.cs\" />")]
    [Xunit.InlineData("<Compile Remove=\"@(Sources)\" />")]
    [Xunit.InlineData("<Compile Remove=\"%(Sources.Identity)\" />")]
    public void MetadataOrUnevaluatedExpression_DoesNotChangeDefaultInclude(string item)
    {
        var root = CreateTempDirectory();
        WriteProject(root, "Lib/Lib.csproj", item);

        Xunit.Assert.Equal(["Lib/Lib.csproj"], Required(root, "Lib/File.cs"));
    }

    [Xunit.Fact]
    public void MalformedProject_DefaultIncludeStillRequiresBuild()
    {
        var root = CreateTempDirectory();
        WriteProject(root, "Lib/Lib.csproj");
        File.WriteAllText(Path.Combine(root, "Lib", "Lib.csproj"), "<Project>");

        Xunit.Assert.Equal(["Lib/Lib.csproj"], Required(root, "Lib/File.cs"));
    }

    private static IReadOnlyList<string> Required(string root, params string[] paths) =>
        WorkerBuildEvidenceRequirement.FindRequiredProjects(root, AgentRole.Developer, paths, []);

    private static void WriteProject(string root, string relativePath, string items = "")
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<Project><ItemGroup>{items}</ItemGroup></Project>");
    }
}
