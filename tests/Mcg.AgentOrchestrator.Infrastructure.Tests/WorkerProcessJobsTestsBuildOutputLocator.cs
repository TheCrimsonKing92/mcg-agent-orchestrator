using System.Reflection;
using System.Runtime.Loader;

[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class WorkerProcessJobsTestsBuildOutputLocator
{
    private const string Project = "SiblingProbe";
    private const string Configuration = "Debug";

    [Fact]
    public void Copied_closure_Uses_build_root_and_reports_missing_paths_when_unset()
    {
        using var fixture = new LocatorFixture();
        var closure = fixture.CreateClosure();
        var output = fixture.CreateOutput("bin");
        Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable,
            Path.Combine(fixture.Root, "bin"));

        var found = TestBuildOutputLocator.Locate(Project, Configuration, closure);

        Assert.Equal(output, found.Directory);
        Assert.Equal([Path.Combine(fixture.Root, Project, Configuration), output], found.TriedPaths);

        Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable, null);
        var missing = TestBuildOutputLocator.Locate(Project, Configuration, closure);

        Assert.Null(missing.Directory);
        Assert.Equal([Path.Combine(fixture.Root, Project, Configuration)], missing.TriedPaths);
    }

    [Fact]
    public void Missing_build_root_Reports_both_attempted_paths()
    {
        using var fixture = new LocatorFixture();
        var closure = fixture.CreateClosure();
        var binRoot = Path.Combine(fixture.Root, "missing-bin");
        Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable, binRoot);

        var missing = TestBuildOutputLocator.Locate(Project, Configuration, closure);

        Assert.Null(missing.Directory);
        Assert.Equal([Path.Combine(fixture.Root, Project, Configuration),
            Path.Combine(binRoot, Project, Configuration)], missing.TriedPaths);
    }

    [Fact]
    public void In_place_layout_Resolves_sibling_without_build_root()
    {
        using var fixture = new LocatorFixture();
        var testDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "bin",
            "Mcg.AgentOrchestrator.Infrastructure.Tests", Configuration)).FullName;
        var output = fixture.CreateOutput("bin");

        var found = TestBuildOutputLocator.Locate(Project, Configuration, testDirectory);

        Assert.Equal(output, found.Directory);
        Assert.Equal([output], found.TriedPaths);
    }

    [Fact]
    public void Existing_sibling_Takes_precedence_over_build_root()
    {
        using var fixture = new LocatorFixture();
        var closure = fixture.CreateClosure();
        var sibling = fixture.CreateOutput("");
        fixture.CreateOutput("bin");
        Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable,
            Path.Combine(fixture.Root, "bin"));

        var found = TestBuildOutputLocator.Locate(Project, Configuration, closure);

        Assert.Equal(sibling, found.Directory);
        Assert.Equal([sibling], found.TriedPaths);
    }

    [Fact]
    public void Missing_test_assembly_Does_not_consult_build_root()
    {
        using var fixture = new LocatorFixture();
        var closure = fixture.CreateClosure(includeAssembly: false);
        fixture.CreateOutput("bin");
        Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable,
            Path.Combine(fixture.Root, "bin"));

        var missing = TestBuildOutputLocator.Locate(Project, Configuration, closure);

        Assert.Null(missing.Directory);
        Assert.Equal([Path.Combine(fixture.Root, Project, Configuration)], missing.TriedPaths);
    }

    [Fact]
    public void Sibling_missing_required_file_Continues_to_build_root()
    {
        using var fixture = new LocatorFixture();
        var closure = fixture.CreateClosure();
        var sibling = fixture.CreateOutput("");
        var output = fixture.CreateOutput("bin");
        File.WriteAllText(Path.Combine(output, "probe.dll"), "probe");
        Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable,
            Path.Combine(fixture.Root, "bin"));

        var found = TestBuildOutputLocator.Locate(Project, Configuration, closure, "probe.dll");

        Assert.Equal(output, found.Directory);
        Assert.Equal([sibling, output], found.TriedPaths);
    }

    [Fact]
    public void FixtureProbe_CopiedClosureFindsDllInExportedBuildRoot()
    {
        using var fixture = new LocatorFixture();
        var anchor = fixture.CreateCopiedAnchor();
        var output = fixture.CreateOutput("bin");
        File.WriteAllText(Path.Combine(output, Project + ".dll"), "probe");
        Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable,
            Path.Combine(fixture.Root, "bin"));

        Assert.Equal(output, FixtureProbeLocator.FindDirectory(anchor, Project, Project + ".dll"));
    }

    [Fact]
    public void FixtureProbe_CopiedClosureFindsCompleteOutputInExportedBuildRoot()
    {
        using var fixture = new LocatorFixture();
        var anchor = fixture.CreateCopiedAnchor();
        var output = fixture.CreateOutput("bin");
        string[] requiredFiles = [Project + ".dll", Project + ".exe",
            Project + ".deps.json", Project + ".runtimeconfig.json"];
        foreach (var name in requiredFiles)
            File.WriteAllText(Path.Combine(output, name), "probe");
        Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable,
            Path.Combine(fixture.Root, "bin"));

        Assert.Equal(output, FixtureProbeLocator.FindDirectory(anchor, Project, requiredFiles));
    }

    [Fact]
    public void FixtureProbe_UnsetBuildRootReportsOnlySiblingPath()
    {
        using var fixture = new LocatorFixture();
        var anchor = fixture.CreateCopiedAnchor();
        var output = fixture.CreateOutput("bin");
        File.WriteAllText(Path.Combine(output, Project + ".dll"), "probe");

        Assert.Null(FixtureProbeLocator.FindDirectory(anchor, Project, Project + ".dll"));
        Assert.Equal([Path.Combine(fixture.Root, "summary", Project, Configuration)],
            FixtureProbeLocator.TriedPaths(anchor, Project, Project + ".dll"));
    }

    [Fact]
    public void FixtureProbe_IncompleteOutputIsRejectedEvenWhenDllExists()
    {
        using var fixture = new LocatorFixture();
        var anchor = fixture.CreateCopiedAnchor();
        var output = fixture.CreateOutput("bin");
        string[] requiredFiles = [Project + ".dll", Project + ".exe",
            Project + ".deps.json", Project + ".runtimeconfig.json"];
        foreach (var name in requiredFiles.Take(3))
            File.WriteAllText(Path.Combine(output, name), "probe");
        Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable,
            Path.Combine(fixture.Root, "bin"));
        Assert.True(File.Exists(Path.Combine(output, requiredFiles[0])));
        Assert.False(File.Exists(Path.Combine(output, requiredFiles[3])));

        Assert.Null(FixtureProbeLocator.FindDirectory(anchor, Project, requiredFiles));
        Assert.Equal([Path.Combine(fixture.Root, "summary", Project, Configuration), output],
            FixtureProbeLocator.TriedPaths(anchor, Project, requiredFiles));
    }

    [Fact]
    public void FixtureProbe_CompleteSiblingOutputRetainsPrecedence()
    {
        using var fixture = new LocatorFixture();
        var anchor = fixture.CreateCopiedAnchor();
        var sibling = fixture.CreateOutput("summary");
        string[] requiredFiles = [Project + ".dll", Project + ".exe",
            Project + ".deps.json", Project + ".runtimeconfig.json"];
        foreach (var name in requiredFiles)
            File.WriteAllText(Path.Combine(sibling, name), "probe");
        fixture.CreateOutput("bin");
        Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable,
            Path.Combine(fixture.Root, "bin"));

        Assert.Equal(sibling, FixtureProbeLocator.FindDirectory(anchor, Project, requiredFiles));
        Assert.Equal([sibling], FixtureProbeLocator.TriedPaths(anchor, Project, requiredFiles));
    }

    private sealed class LocatorFixture : IDisposable
    {
        private AssemblyLoadContext? _copiedContext;
        private readonly string? _original = Environment.GetEnvironmentVariable(
            TestBuildOutputLocator.BuildOutputRootVariable);
        internal string Root { get; } = Directory.CreateTempSubdirectory("mcg-output-locator-").FullName;

        internal LocatorFixture()
            => Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable, null);

        internal string CreateClosure(bool includeAssembly = true)
        {
            var closure = Directory.CreateDirectory(Path.Combine(Root, ".c", "closure-token")).FullName;
            if (includeAssembly)
                File.WriteAllText(Path.Combine(closure,
                    Path.GetFileName(typeof(TestBuildOutputLocator).Assembly.Location)), "test assembly");
            return closure;
        }

        internal string CreateOutput(string binRoot)
            => Directory.CreateDirectory(Path.Combine(Root, binRoot, Project, Configuration)).FullName;

        internal Type CreateCopiedAnchor()
        {
            var closure = Directory.CreateDirectory(Path.Combine(Root, "summary", ".c", "closure-token")).FullName;
            var original = typeof(WorkerProcessJobsTestsBuildOutputLocator);
            var copy = Path.Combine(closure, Path.GetFileName(original.Assembly.Location));
            File.Copy(original.Assembly.Location, copy);
            _copiedContext = new AssemblyLoadContext("fixture-probe-anchor", isCollectible: true);
            var anchor = _copiedContext.LoadFromAssemblyPath(copy).GetType(original.FullName!, throwOnError: true)!;
            Assert.Equal(copy, anchor.Assembly.Location);
            Assert.Equal(Configuration, anchor.Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration);
            return anchor;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable, _original);
            _copiedContext?.Unload();
            Directory.Delete(Root, recursive: true);
        }
    }
}
