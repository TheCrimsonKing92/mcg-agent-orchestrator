using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixtures own their temp worktrees; live-tree checks only read verified source.
public sealed class PreTesterTestSourceGuardClassesTests
{
    private const string ChangedPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/NewFeatureTests.cs";
    private const string ProjectName = "Example.Tests";
    private static readonly string[] SharedNames =
    [
        "CliCommandCapabilitiesTestsQueryRouteConvention",
        "CliServedFormsSqliteStoreEffectsTests",
        "CliCommandCapabilitiesTestsQueryRouteNoWriterEffects"
    ];
    private static FindingEvidenceSelection[] Expected =>
        new[] { "AlphaSourceGuardTests", "BetaGuardTests" }.Concat(SharedNames)
            .Select(name => new FindingEvidenceSelection(ProjectName, name)).ToArray();

    [Fact]
    public void TestChangesSelectOrderedGuardStemsThenSharedTableClasses()
    {
        using var fixture = new SourceFixture();

        var selected = PreTesterTestSourceGuardClasses.Select(fixture.Root, [ChangedPath]);

        Assert.Equal(Expected, selected);
        Assert.DoesNotContain(selected, selection => selection.TestClass == "NotAGuardTests");
        Assert.Equal(SharedNames, PreTesterTestSourceGuardClasses.SharedTableClasses.Select(entry => entry.TestClass));
    }

    [Fact]
    public void OnlyNormalizedTopLevelTestPathsTriggerSelection()
    {
        using var fixture = new SourceFixture();
        string[][] negativePaths =
        [
            ["src/Mcg.AgentOrchestrator.App/Orchestration/X.cs"],
            ["docs/tests/notes.md"],
            ["config/x.json"],
            []
        ];
        foreach (var paths in negativePaths)
            Assert.Empty(PreTesterTestSourceGuardClasses.Select(fixture.Root, paths));
        Assert.Equal(Expected, PreTesterTestSourceGuardClasses.Select(fixture.Root,
            ["tests\\Mcg.AgentOrchestrator.Infrastructure.Tests\\NewFeatureTests.cs"]));
        Assert.Equal(Expected, PreTesterTestSourceGuardClasses.Select(fixture.Root,
            ["./TESTS/Example/NewFeatureTests.cs"]));
    }

    [Fact]
    public void MissingSharedClassAndAmbiguousGuardAreOmitted()
    {
        using var fixture = new SourceFixture();
        File.Delete(Path.Combine(fixture.Project, "Cli", SharedNames[1] + ".cs"));
        fixture.Declare("BetaGuardTests", "nested");

        var selected = PreTesterTestSourceGuardClasses.Select(fixture.Root, [ChangedPath]);

        Assert.Equal(Expected.Where(selection => selection.TestClass != "BetaGuardTests" &&
            selection.TestClass != SharedNames[1]), selected);
    }

    [Theory]
    [InlineData("AlphaSourceGuardTests.Fact", "AlphaSourceGuardTests", true)]
    [InlineData("Ns.BetaGuardTests.Theory(value: 1)", "BetaGuardTests", true)]
    [InlineData("CliServedFormsSqliteStoreEffectsTests.Case", "CliServedFormsSqliteStoreEffectsTests", true)]
    [InlineData("AlphaSourceGuardTestsExtra.Fact", "AlphaSourceGuardTestsExtra", false)]
    [InlineData("UnrelatedTests.Fact", "UnrelatedTests", false)]
    [InlineData("AlphaSourceGuardTests", null, false)]
    public void CoverageUsesTheDeclaringClass(string identity, string? declaringClass, bool expected)
    {
        Assert.Equal(declaringClass, AcceptanceTestSourceResolver.ExtractClassName(identity));
        Assert.Equal(expected, PreTesterTestSourceGuardClasses.ContainsClass(declaringClass));
        Assert.Equal(expected, PreTesterTestSourceGuardClasses.IsCovered(identity));
    }

    [Fact]
    public void LiveTreeSelectsEveryGuardStemAndSharedClassWithinCountBound()
    {
        var root = VerifiedRepositoryRoot.Find();
        var guardNames = EnumerateTestSources(root)
            .Where(path => path.EndsWith("GuardTests.cs", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(guardNames);

        var selected = PreTesterTestSourceGuardClasses.Select(root, [ChangedPath]);

        Assert.Equal(guardNames, selected.Where(selection => selection.TestClass.EndsWith("GuardTests", StringComparison.Ordinal))
            .Select(selection => selection.TestClass).OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(SharedNames, name => Assert.Single(selected, selection => selection.TestClass == name));
        Assert.Equal(guardNames.Length + SharedNames.Length, selected.Count);
        Assert.InRange(selected.Count, 1, 45);
    }

    [Fact]
    public void EveryServedFormsTableReaderIsInTheSharedClassList()
    {
        var root = VerifiedRepositoryRoot.Find();
        var needle = "typeof(" + SharedNames[0] + ")";
        var readers = EnumerateTestSources(root).Where(path => File.ReadAllText(path).Contains(needle, StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension).ToArray();

        Assert.NotEmpty(readers);
        Assert.All(readers, name => Assert.Contains(PreTesterTestSourceGuardClasses.SharedTableClasses,
            entry => entry.TestClass == name));
    }

    private static IEnumerable<string> EnumerateTestSources(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.Combine(root, "tests"));
        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (Path.GetFileName(child) is "bin" or "obj" ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                pending.Push(child);
            }
        }
    }

    private sealed class SourceFixture : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public string Project => Path.Combine(Root, "tests", ProjectName);

        public SourceFixture()
        {
            Directory.CreateDirectory(Project);
            File.WriteAllText(Path.Combine(Project, ProjectName + ".csproj"), "<Project />");
            Declare("BetaGuardTests");
            Declare("AlphaSourceGuardTests");
            // The class suffix alone is insufficient: selection discovers file stems.
            File.WriteAllText(Path.Combine(Project, "OrdinaryTests.cs"), "class NotAGuardTests {}");
            foreach (var name in SharedNames) Declare(name, "Cli");
            Declare("IgnoredGuardTests", "bin");
            Declare("IgnoredGuardTests", "obj");
        }

        public void Declare(string name, string folder = "")
        {
            var directory = Path.Combine(Project, folder);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".cs"), "class " + name + " {}");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
