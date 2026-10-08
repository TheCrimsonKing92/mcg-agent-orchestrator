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

    private sealed class LocatorFixture : IDisposable
    {
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

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(TestBuildOutputLocator.BuildOutputRootVariable, _original);
            Directory.Delete(Root, recursive: true);
        }
    }
}
