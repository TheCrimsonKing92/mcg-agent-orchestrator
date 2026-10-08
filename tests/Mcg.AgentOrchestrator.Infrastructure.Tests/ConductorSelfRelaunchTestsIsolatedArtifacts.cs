using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ConductorSelfRelaunchTestsIsolatedArtifacts
{
    private const string IsolationProperty = "-p:McgIsolatedArtifactsPath=";
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(2);

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_command_uses_unique_sibling_roots_adjacent_to_output(bool trailingSeparator)
    {
        using var fixture = new BuildFixture();
        var output = fixture.OutputDirectory + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : "");
        var first = ConductorSelfRelaunch.CreateAppBuildCommand(fixture.ProjectPath, output);
        var second = ConductorSelfRelaunch.CreateAppBuildCommand(fixture.ProjectPath, output);

        Assert.NotEqual(first.IsolatedArtifactsRoot, second.IsolatedArtifactsRoot);
        foreach (var command in new[] { first, second })
        {
            Assert.Equal(Path.GetDirectoryName(fixture.OutputDirectory), Path.GetDirectoryName(command.IsolatedArtifactsRoot));
            Assert.False(Directory.Exists(command.IsolatedArtifactsRoot));
            var property = Assert.Single(command.Arguments.Where(arg => arg.StartsWith(IsolationProperty, StringComparison.Ordinal)));
            Assert.Equal(IsolationProperty + command.IsolatedArtifactsRoot, property);
            Assert.Equal(new[]
            {
                "build", fixture.ProjectPath, "--nologo", "--output", output,
                property, "-v", "quiet", "-clp:ErrorsOnly"
            }, command.Arguments);
        }
    }

    [Fact]
    public void Store_build_command_fits_Windows_intermediate_path_budget()
    {
        var repository = InfrastructureTestSupport.FindRepositoryRoot();
        var longestProject = Directory.EnumerateFiles(Path.Combine(repository, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension).MaxBy(name => name!.Length)!;
        // Model the production 35-character temp parent, not the test host's redirected TEMP.
        var tempParent = Path.Combine(Path.GetPathRoot(repository)!, new string('t', 32));
        var output = Path.Combine(OrchestratorTempRoot.GetRoot(tempParent), "tmp", "landing-app-build",
            new string('a', 16), new string('b', 40) + ".partial-" + new string('c', 32));
        var command = ConductorSelfRelaunch.CreateAppBuildCommand("App.csproj", output);
        var intermediate = Path.Combine(command.IsolatedArtifactsRoot, "obj", longestProject,
            "Debug", longestProject + ".GeneratedMSBuildEditorConfig.editorconfig");
        Assert.True(intermediate.Length <= 259, $"Windows intermediate path exceeds MAX_PATH: {intermediate}");
        Assert.StartsWith("r", Path.GetFileName(command.IsolatedArtifactsRoot));
    }

    [Fact(Skip = "CS2012 negative control requires Windows mandatory exclusive file sharing.", SkipUnless = nameof(IsWindows))]
    public async Task Locked_in_tree_assembly_builds_only_with_isolated_artifacts()
    {
        using var fixture = new BuildFixture();
        var inTreeAssembly = Path.Combine(fixture.ProjectDirectory, "obj", "Debug", "net10.0", BuildFixture.ProjectName + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(inTreeAssembly)!);
        File.WriteAllBytes(inTreeAssembly, []);
        File.SetLastWriteTimeUtc(inTreeAssembly, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using var heldAssembly = new FileStream(inTreeAssembly, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var command = ConductorSelfRelaunch.CreateAppBuildCommand(fixture.ProjectPath, fixture.OutputDirectory);
        try
        {
            // The verifier's allow-listed child environment removes inherited McgIsolatedArtifactsPath.
            var isolated = await LocalProcessVerifier.RunCommandAsync("dotnet", command.Arguments,
                fixture.Root, BuildTimeout, CancellationToken.None);
            Assert.False(isolated.TimedOut, "Isolated dotnet build did not exit: " + isolated.Stdout + isolated.Stderr);
            Assert.True(isolated.ExitCode == 0, isolated.Stdout + isolated.Stderr);
            Assert.DoesNotContain("CS2012", isolated.Stdout + isolated.Stderr);
            Assert.True(Directory.Exists(command.IsolatedArtifactsRoot));
            Assert.True(File.Exists(Path.Combine(fixture.OutputDirectory, BuildFixture.ProjectName + ".dll")));
            AssertNoIntermediates(fixture.OutputDirectory);

            var withoutIsolation = command.Arguments.Where(arg => !arg.StartsWith(IsolationProperty, StringComparison.Ordinal)).ToArray();
            Assert.Equal(command.Arguments.Count - 1, withoutIsolation.Length);
            var control = await LocalProcessVerifier.RunCommandAsync("dotnet", withoutIsolation,
                fixture.Root, BuildTimeout, CancellationToken.None);
            Assert.False(control.TimedOut, "Non-isolated dotnet build did not exit: " + control.Stdout + control.Stderr);
            Assert.NotEqual(0, control.ExitCode);
            Assert.Contains("CS2012", control.Stdout + control.Stderr);
        }
        finally
        {
            if (Directory.Exists(command.IsolatedArtifactsRoot)) Directory.Delete(command.IsolatedArtifactsRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Store_build_removes_isolated_artifacts_on_success_and_failure(bool compileError)
    {
        using var fixture = new BuildFixture(compileError);
        var result = ConductorSelfRelaunch.RunAppBuildProcess("dotnet",
            new LandingAppBuildRequest(fixture.Root, fixture.OutputDirectory, BuildTimeout), CancellationToken.None);

        Assert.False(result.TimedOut, "Store dotnet build did not exit: " + result.Stdout + result.Stderr);
        if (compileError)
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("CS0246", result.Stdout + result.Stderr);
        }
        else
        {
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            Assert.True(File.Exists(Path.Combine(fixture.OutputDirectory, BuildFixture.ProjectName + ".dll")));
        }
        // A surviving sibling catches missing finally cleanup; no in-tree obj catches missing isolation.
        Assert.Equal(new[] { fixture.OutputDirectory }, Directory.GetDirectories(fixture.BuildParent));
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectDirectory, "obj")));
        AssertNoIntermediates(fixture.OutputDirectory);
    }

    private static void AssertNoIntermediates(string outputDirectory)
    {
        Assert.DoesNotContain(Directory.EnumerateDirectories(outputDirectory, "*", SearchOption.AllDirectories),
            directory => Path.GetFileName(directory) == "obj");
        Assert.DoesNotContain(Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories),
            file => Path.GetFileName(file) == "project.assets.json" ||
                file.EndsWith(".cache", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class BuildFixture : IDisposable
    {
        internal const string ProjectName = "Mcg.AgentOrchestrator.App";
        internal string Root { get; } = Directory.CreateTempSubdirectory("r-").FullName;
        internal string ProjectDirectory => Path.Combine(Root, "src", ProjectName);
        internal string ProjectPath => Path.Combine(ProjectDirectory, ProjectName + ".csproj");
        internal string BuildParent => Path.Combine(Root, "builds");
        internal string OutputDirectory => Path.Combine(BuildParent, "output");

        internal BuildFixture(bool compileError = false)
        {
            Directory.CreateDirectory(ProjectDirectory);
            Directory.CreateDirectory(OutputDirectory);
            var repository = InfrastructureTestSupport.FindRepositoryRoot();
            File.Copy(Path.Combine(repository, "Directory.Build.props"), Path.Combine(Root, "Directory.Build.props"));
            File.Copy(Path.Combine(repository, "Directory.Build.rsp"), Path.Combine(Root, "Directory.Build.rsp"));
            File.WriteAllText(ProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <NuGetAudit>false</NuGetAudit>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(ProjectDirectory, "Class.cs"), compileError
                ? "public class Sample : MissingType { }"
                : "public class Sample { }");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
