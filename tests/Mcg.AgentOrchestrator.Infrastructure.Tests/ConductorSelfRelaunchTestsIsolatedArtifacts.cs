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
    public void Build_command_reuses_source_cache_outside_successor_output(bool trailingSeparator)
    {
        using var fixture = new BuildFixture();
        var output = fixture.OutputDirectory + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : "");
        var first = ConductorSelfRelaunch.CreateAppBuildCommand(fixture.ProjectPath, output);
        var second = ConductorSelfRelaunch.CreateAppBuildCommand(fixture.ProjectPath, output + "-next");
        using var otherTree = new BuildFixture();
        var other = ConductorSelfRelaunch.CreateAppBuildCommand(otherTree.ProjectPath, output);

        Assert.Equal(first.IsolatedArtifactsRoot, second.IsolatedArtifactsRoot);
        Assert.NotEqual(first.IsolatedArtifactsRoot, other.IsolatedArtifactsRoot);
        foreach (var command in new[] { first, second })
        {
            Assert.True(Path.IsPathRooted(command.IsolatedArtifactsRoot));
            Assert.Equal("..", Path.GetRelativePath(fixture.OutputDirectory, command.IsolatedArtifactsRoot)
                .Split(Path.DirectorySeparatorChar)[0]);
            Assert.False(Directory.Exists(command.IsolatedArtifactsRoot));
            var property = Assert.Single(command.Arguments.Where(arg => arg.StartsWith(IsolationProperty, StringComparison.Ordinal)));
            Assert.Equal(IsolationProperty + command.IsolatedArtifactsRoot, property);
            Assert.Equal(new[]
            {
                "build", fixture.ProjectPath, "--nologo", "--output", command == first ? output : output + "-next",
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
        var modeledCache = Path.Combine(OrchestratorTempRoot.GetRoot(tempParent), "tmp", "relaunch-build",
            Path.GetFileName(command.IsolatedArtifactsRoot));
        var intermediate = Path.Combine(modeledCache, "obj", longestProject,
            "Debug", longestProject + ".GeneratedMSBuildEditorConfig.editorconfig");
        Assert.True(intermediate.Length <= 259, $"Windows intermediate path exceeds MAX_PATH: {intermediate}");
        Assert.Equal(16, Path.GetFileName(command.IsolatedArtifactsRoot).Length);
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
    public void Store_build_retains_only_cache_intermediates_on_success_and_failure(bool compileError)
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
        var command = ConductorSelfRelaunch.CreateAppBuildCommand(fixture.ProjectPath, fixture.OutputDirectory);
        // Retry correction retains the cache; it must stay outside the successor and store retention.
        Assert.True(Directory.Exists(Path.Combine(command.IsolatedArtifactsRoot, "obj")));
        Assert.Equal(new[] { fixture.OutputDirectory }, Directory.GetDirectories(fixture.BuildParent));
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectDirectory, "obj")));
        AssertNoIntermediates(fixture.OutputDirectory);
    }

    [Fact]
    public void Store_build_reuses_compilation_for_a_new_successor_output()
    {
        using var fixture = new BuildFixture();
        var first = ConductorSelfRelaunch.RunAppBuildProcess("dotnet",
            new(fixture.Root, fixture.OutputDirectory, BuildTimeout), CancellationToken.None);
        Assert.False(first.TimedOut, first.Stdout + first.Stderr);
        Assert.True(first.ExitCode == 0, first.Stdout + first.Stderr);
        var command = ConductorSelfRelaunch.CreateAppBuildCommand(fixture.ProjectPath, fixture.OutputDirectory);
        var intermediate = Assert.Single(Directory.GetFiles(Path.Combine(command.IsolatedArtifactsRoot, "obj"),
            BuildFixture.ProjectName + ".dll", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) is not "ref" and not "refint"));
        // Deny a compiler write while allowing MSBuild to read/copy the cached assembly.
        using var heldIntermediate = new FileStream(intermediate, FileMode.Open, FileAccess.Read, FileShare.Read);
        File.WriteAllText(Path.Combine(command.IsolatedArtifactsRoot, "output", "stale-payload.txt"), "old build");
        var nextOutput = Path.Combine(fixture.BuildParent, "next-output");
        var second = ConductorSelfRelaunch.RunAppBuildProcess("dotnet",
            new(fixture.Root, nextOutput, BuildTimeout), CancellationToken.None);
        Assert.False(second.TimedOut, second.Stdout + second.Stderr);
        Assert.True(second.ExitCode == 0, second.Stdout + second.Stderr);
        Assert.False(File.Exists(Path.Combine(nextOutput, "stale-payload.txt")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.OutputDirectory, BuildFixture.ProjectName + ".dll")),
            File.ReadAllBytes(Path.Combine(nextOutput, BuildFixture.ProjectName + ".dll")));
        AssertNoIntermediates(nextOutput);
    }

    [Fact]
    public void Store_build_lock_wait_preserves_timeout_and_cancellation()
    {
        using var fixture = new BuildFixture();
        var command = ConductorSelfRelaunch.CreateAppBuildCommand(fixture.ProjectPath, fixture.OutputDirectory);
        Directory.CreateDirectory(command.IsolatedArtifactsRoot);
        using var heldLock = File.Open(Path.Combine(command.IsolatedArtifactsRoot, "build.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // An unavailable executable proves neither call starts the compiler while the cache is owned.
        var timedOut = ConductorSelfRelaunch.RunAppBuildProcess("missing-dotnet-executable",
            new(fixture.Root, fixture.OutputDirectory, TimeSpan.FromMilliseconds(50)), CancellationToken.None);
        Assert.True(timedOut.TimedOut);
        Assert.Equal(-1, timedOut.ExitCode);
        Assert.Contains("waiting for isolated App build cache", timedOut.Stderr);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ConductorSelfRelaunch.RunAppBuildProcess(
            "missing-dotnet-executable", new(fixture.Root, fixture.OutputDirectory, BuildTimeout), cancellation.Token));
    }

    [Fact]
    public void Store_build_failure_does_not_copy_stale_cached_output_and_can_recover()
    {
        using var fixture = new BuildFixture();
        var first = ConductorSelfRelaunch.RunAppBuildProcess("dotnet",
            new(fixture.Root, fixture.OutputDirectory, BuildTimeout), CancellationToken.None);
        Assert.True(first.ExitCode == 0, first.Stdout + first.Stderr);
        var source = Path.Combine(fixture.ProjectDirectory, "Class.cs");
        File.WriteAllText(source, "public class Sample : MissingType { }");
        var nextOutput = Path.Combine(fixture.BuildParent, "next-output");
        Directory.CreateDirectory(nextOutput);
        var failed = ConductorSelfRelaunch.RunAppBuildProcess("dotnet",
            new(fixture.Root, nextOutput, BuildTimeout), CancellationToken.None);
        Assert.False(failed.TimedOut, failed.Stdout + failed.Stderr);
        Assert.NotEqual(0, failed.ExitCode);
        Assert.Contains("CS0246", failed.Stdout + failed.Stderr);
        Assert.Empty(Directory.GetFiles(nextOutput, "*", SearchOption.AllDirectories));
        File.WriteAllText(source, "public class ChangedSample { }");
        var recovered = ConductorSelfRelaunch.RunAppBuildProcess("dotnet",
            new(fixture.Root, nextOutput, BuildTimeout), CancellationToken.None);
        Assert.False(recovered.TimedOut, recovered.Stdout + recovered.Stderr);
        Assert.True(recovered.ExitCode == 0, recovered.Stdout + recovered.Stderr);
        Assert.NotEqual(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(fixture.OutputDirectory, BuildFixture.ProjectName + ".dll"))),
            Convert.ToBase64String(File.ReadAllBytes(Path.Combine(nextOutput, BuildFixture.ProjectName + ".dll"))));
        AssertNoIntermediates(nextOutput);
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

        public void Dispose()
        {
            var cacheRoot = ConductorSelfRelaunch.CreateAppBuildCommand(ProjectPath, OutputDirectory).IsolatedArtifactsRoot;
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
            Directory.Delete(Root, recursive: true);
        }
    }
}
