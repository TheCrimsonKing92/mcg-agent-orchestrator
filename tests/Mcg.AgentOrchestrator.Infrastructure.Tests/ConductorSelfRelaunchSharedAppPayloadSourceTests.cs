using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each fact owns its filesystem root and uses an injected build runner.
public sealed class ConductorSelfRelaunchSharedAppPayloadSourceTests
{
    private const string AppName = "Mcg.AgentOrchestrator.App";
    private static readonly DateTime SourceTime = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BuildTime = new(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ChangedTime = new(2020, 1, 3, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Fresh_output_Copies_payload_without_invoking_build()
    {
        using var fixture = new PayloadFixture();
        fixture.CreateOutput();
        fixture.WriteOutput(LandingAppBuildStore.HeadMarkerName, "operator marker");
        fixture.WriteOutput(LandingAppBuildStore.CompleteMarkerName, "store marker");
        var before = ReadFiles(fixture.Output);
        var expected = before.Where(pair => pair.Key is not LandingAppBuildStore.HeadMarkerName
            and not LandingAppBuildStore.CompleteMarkerName).ToDictionary();
        expected.Add("Reference.deps.json", System.Text.Encoding.UTF8.GetBytes("reference dependencies"));
        var calls = 0;
        var source = fixture.CreateSource((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Fresh output must not build.");
        });

        source.AssembleInto(fixture.Destination);

        Assert.Equal(0, calls);
        AssertFilesEqual(expected, ReadFiles(fixture.Destination));
        AssertFilesEqual(before, ReadFiles(fixture.Output));
    }

    [Fact]
    public void Missing_output_Invokes_build_once_without_a_time_limit()
    {
        using var fixture = new PayloadFixture();
        Assert.False(Directory.Exists(fixture.Output));
        var calls = 0;
        var source = fixture.CreateSource((destination, timeout) =>
        {
            calls++;
            Assert.Equal(fixture.Destination, destination);
            Assert.Equal(Timeout.InfiniteTimeSpan, timeout);
            fixture.WriteBuiltApp(destination);
            return (0, "", "", false);
        });

        source.AssembleInto(fixture.Destination);

        Assert.Equal(1, calls);
        Assert.Equal("built App", File.ReadAllText(Path.Combine(fixture.Destination, AppName + ".dll")));
    }

    [Theory]
    [InlineData("app.cs")]
    [InlineData("reference.cs")]
    [InlineData("Directory.Build.props")]
    [InlineData("linked.json")]
    public void Stale_output_Invokes_build_once(string changedSource)
    {
        using var fixture = new PayloadFixture();
        fixture.CreateOutput();
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Root, changedSource), ChangedTime);
        var calls = 0;
        var source = fixture.CreateSource((destination, timeout) =>
        {
            calls++;
            Assert.Equal(Timeout.InfiniteTimeSpan, timeout);
            fixture.WriteBuiltApp(destination);
            return (0, "", "", false);
        });

        source.AssembleInto(fixture.Destination);

        Assert.Equal(1, calls);
        Assert.Equal("built App", File.ReadAllText(Path.Combine(fixture.Destination, AppName + ".dll")));
    }

    [Theory]
    [InlineData("Mcg.AgentOrchestrator.App.dll")]
    [InlineData("Mcg.AgentOrchestrator.App.runtimeconfig.json")]
    [InlineData("Reference.dll")]
    [InlineData("../reference-output/Reference.deps.json")]
    public void Incomplete_output_Invokes_build_once(string missingFile)
    {
        using var fixture = new PayloadFixture();
        fixture.CreateOutput();
        File.Delete(Path.Combine(fixture.Output, missingFile));
        var calls = 0;
        var source = fixture.CreateSource((destination, _) =>
        {
            calls++;
            fixture.WriteBuiltApp(destination);
            return (0, "", "", false);
        });

        source.AssembleInto(fixture.Destination);

        Assert.Equal(1, calls);
        Assert.True(File.Exists(Path.Combine(fixture.Destination, AppName + ".dll")));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(23, false)]
    [InlineData(0, true)]
    public void Failed_build_Reports_apparatus_failure(int exitCode, bool timedOut)
    {
        using var fixture = new PayloadFixture();
        var calls = 0;
        var source = fixture.CreateSource((_, timeout) =>
        {
            calls++;
            Assert.Equal(Timeout.InfiniteTimeSpan, timeout);
            return (exitCode, "", "build diagnostic", timedOut);
        });

        Exception exception = Assert.Throws<SharedAppPayloadApparatusException>(
            () => source.AssembleInto(fixture.Destination));

        Assert.Equal(1, calls);
        Assert.IsNotType<Xunit.Sdk.XunitException>(exception);
        Assert.StartsWith("Shared App payload apparatus failure:", exception.Message);
        Assert.Contains($"exit={exitCode} timedOut={timedOut}", exception.Message);
        Assert.Contains($"appOutput={fixture.Output}", exception.Message);
        Assert.Contains("build diagnostic", exception.Message);
    }

    [Fact]
    public void Successful_build_without_app_Reports_apparatus_failure()
    {
        using var fixture = new PayloadFixture();
        var calls = 0;
        var source = fixture.CreateSource((_, _) => { calls++; return (0, "", "", false); });

        var exception = Assert.Throws<SharedAppPayloadApparatusException>(
            () => source.AssembleInto(fixture.Destination));

        Assert.Equal(1, calls);
        Assert.StartsWith("Shared App payload apparatus failure:", exception.Message);
        Assert.Contains("build produced no " + AppName + ".dll", exception.Message);
    }

    [Fact]
    public void Build_cannot_start_Reports_apparatus_failure_with_cause()
    {
        using var fixture = new PayloadFixture();
        var cause = new System.ComponentModel.Win32Exception("dotnet missing");
        var source = fixture.CreateSource((_, _) => throw cause);

        var exception = Assert.Throws<SharedAppPayloadApparatusException>(
            () => source.AssembleInto(fixture.Destination));

        Assert.Same(cause, exception.InnerException);
        Assert.StartsWith("Shared App payload apparatus failure:", exception.Message);
        Assert.Contains("dotnet missing", exception.Message);
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public void Centralized_layout_Selects_sibling_app_output(string configuration)
    {
        using var fixture = new PayloadFixture();
        var bin = Path.Combine(fixture.Root, "artifacts", "bin");
        var testDirectory = Path.Combine(bin, "Mcg.AgentOrchestrator.Infrastructure.Tests", configuration);

        var output = ConductorSelfRelaunchSharedAppPayloadSource.ResolveAppOutputDirectory(
            fixture.Root, testDirectory, configuration);

        Assert.Equal(Path.Combine(bin, AppName, configuration), output);
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public void Default_layout_Selects_project_app_output(string configuration)
    {
        using var fixture = new PayloadFixture();
        var testDirectory = Path.Combine(fixture.Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "bin", configuration, "net10.0");

        var output = ConductorSelfRelaunchSharedAppPayloadSource.ResolveAppOutputDirectory(
            fixture.Root, testDirectory, configuration);

        Assert.Equal(Path.Combine(fixture.Root, "src", AppName, "bin", configuration, "net10.0"), output);
    }

    [Fact]
    public void Configuration_layout_mismatch_Reports_apparatus_failure()
    {
        using var fixture = new PayloadFixture();
        var directory = Path.Combine(fixture.Root, "bin", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Release");

        var exception = Assert.Throws<SharedAppPayloadApparatusException>(() =>
            ConductorSelfRelaunchSharedAppPayloadSource.ResolveAppOutputDirectory(fixture.Root, directory, "Debug"));

        Assert.StartsWith("Shared App payload apparatus failure:", exception.Message);
        Assert.Contains(directory, exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Project_closure_Uses_transitive_sources_and_declared_dependency_files(bool stale)
    {
        using var fixture = new PayloadFixture();
        fixture.WriteSource("src/" + AppName + "/" + AppName + ".csproj", """
            <Project><ItemGroup>
              <ProjectReference Include="../Reference/Reference.csproj" />
              <Content Include="../../linked.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>
            </ItemGroup></Project>
            """);
        fixture.WriteSource("src/Reference/Reference.csproj", """
            <Project><ItemGroup><ProjectReference Include="../Leaf/Leaf.csproj" /></ItemGroup></Project>
            """);
        fixture.WriteSource("src/Leaf/Leaf.csproj", """
            <Project><PropertyGroup><GenerateDependencyFile>false</GenerateDependencyFile></PropertyGroup></Project>
            """);
        fixture.WriteSource("src/Leaf/leaf.cs", "leaf source");
        foreach (var directory in new[] { "bin", "obj", "artifacts", ".scratch" })
        {
            var generated = "src/Leaf/" + directory + "/generated.cs";
            fixture.WriteSource(generated, "generated noise");
            File.SetLastWriteTimeUtc(Path.Combine(fixture.Root, generated), ChangedTime);
        }
        fixture.CreateOutput();
        fixture.WriteOutput("Leaf.dll", "built Leaf");
        var testDirectory = Path.Combine(fixture.Root, "artifacts", "bin", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Debug");
        var appOutput = ConductorSelfRelaunchSharedAppPayloadSource.ResolveAppOutputDirectory(fixture.Root, testDirectory, "Debug");
        Directory.CreateDirectory(Path.GetDirectoryName(appOutput)!);
        Directory.Move(fixture.Output, appOutput);
        var deps = Path.Combine(fixture.Root, "artifacts", "bin", "Reference", "Debug", "Reference.deps.json");
        Directory.CreateDirectory(Path.GetDirectoryName(deps)!);
        File.Copy(Path.Combine(fixture.Root, "reference-output", "Reference.deps.json"), deps);
        if (stale) File.SetLastWriteTimeUtc(Path.Combine(fixture.Root, "src/Leaf/leaf.cs"), ChangedTime);
        var calls = 0;
        var source = ConductorSelfRelaunchSharedAppPayloadSource.ForBuildOutput(
            fixture.Root, testDirectory, "Debug", (destination, timeout) =>
            {
                calls++;
                Assert.Equal(Timeout.InfiniteTimeSpan, timeout);
                fixture.WriteBuiltApp(destination);
                return (0, "", "", false);
            });

        source.AssembleInto(fixture.Destination);

        Assert.Equal(stale ? 1 : 0, calls);
        if (stale)
            Assert.Equal("built App", File.ReadAllText(Path.Combine(fixture.Destination, AppName + ".dll")));
        else
        {
            var expected = ReadFiles(appOutput);
            expected.Add("Reference.deps.json", System.Text.Encoding.UTF8.GetBytes("reference dependencies"));
            AssertFilesEqual(expected, ReadFiles(fixture.Destination));
        }
    }

    private static Dictionary<string, byte[]> ReadFiles(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(directory, file), File.ReadAllBytes);

    private static void AssertFilesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (path, content) in expected) Assert.Equal(content, actual[path]);
    }

    private sealed class PayloadFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("mcg-payload-source-").FullName;
        internal string Output => Path.Combine(Root, "app-output");
        internal string Destination => Path.Combine(Root, "payload");
        private string DependencyFile => Path.Combine(Root, "reference-output", "Reference.deps.json");

        internal PayloadFixture()
        {
            foreach (var source in new[] { "app.cs", "reference.cs", "Directory.Build.props", "linked.json" })
                Write(Path.Combine(Root, source), source, SourceTime);
        }

        internal ConductorSelfRelaunchSharedAppPayloadSource CreateSource(
            Func<string, TimeSpan, (int ExitCode, string Stdout, string Stderr, bool TimedOut)> runner)
            => new(Output,
                [new(AppName, [Path.Combine(Root, "app.cs"), Path.Combine(Root, "Directory.Build.props"),
                    Path.Combine(Root, "linked.json")], null),
                 new("Reference", [Path.Combine(Root, "reference.cs")], DependencyFile)], runner);

        internal void CreateOutput()
        {
            foreach (var name in new[] { AppName + ".dll", AppName + ".pdb", AppName + ".deps.json",
                AppName + ".runtimeconfig.json", AppName + (OperatingSystem.IsWindows() ? ".exe" : ""),
                "Reference.dll", "Reference.pdb", "runtimes/native/payload.bin", "config/trials/linked.json" })
                WriteOutput(name, "built " + name);
            File.WriteAllBytes(Path.Combine(Output, "runtimes/native/payload.bin"), [0, 255, 128, 10]);
            Write(DependencyFile, "reference dependencies", BuildTime);
        }

        internal void WriteOutput(string name, string content) => Write(Path.Combine(Output, name), content, BuildTime);
        internal void WriteSource(string name, string content) => Write(Path.Combine(Root, name), content, SourceTime);
        internal void WriteBuiltApp(string destination) => Write(Path.Combine(destination, AppName + ".dll"), "built App", BuildTime);

        private static void Write(string path, string content, DateTime timestamp)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, timestamp);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
