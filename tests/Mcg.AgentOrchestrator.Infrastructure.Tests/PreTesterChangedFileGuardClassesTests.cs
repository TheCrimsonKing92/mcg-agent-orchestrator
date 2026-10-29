using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each fixture owns its files and cache; the live-tree check only reads source.
public sealed class PreTesterChangedFileGuardClassesTests
{
    private const string ChangedPath = "src/X/Y.cs";

    [Theory]
    [InlineData("src/X/Y.cs")]
    [InlineData("./SRC\\X\\Y.cs")]
    public void SegmentPathChange_SelectsOnlyTheReadingGuard(string changedPath)
    {
        using var fixture = new SourceFixture();
        fixture.Declare("SegmentTests", """
            [Xunit.Fact]
            public void ReadsSegments() {
                var root = FindRoot();
                var path = Path.Combine(root, "src", "X", "Y.cs");
                var source = File.ReadAllText(path);
            }
            """);
        fixture.Declare("UnrelatedTests", """
            [Fact] public void ReadsOther() {
                var unrelated = "src/X/Y.cs";
                var text = File.ReadAllText("Other.cs");
            }
            public void NotATest() { var text = File.ReadAllText("Y.cs"); }
            """);

        var result = fixture.Select([changedPath]);

        Assert.Equal([new FindingEvidenceSelection("Example.Tests", "SegmentTests")], result.Selections);
        Assert.Empty(result.NotRun);
        Assert.True(PreTesterChangedFileGuardClasses.ContainsClass(result.Selections, "SegmentTests"));
        Assert.False(PreTesterChangedFileGuardClasses.ContainsClass(result.Selections, "SegmentTestsExtra"));
    }

    [Theory]
    [InlineData("src/X/Other.cs")]
    [InlineData("src/X/AY.cs")]
    [InlineData("src/AX/Y.cs")]
    public void UnreadPath_SelectsNoGuard(string changedPath)
    {
        using var fixture = new SourceFixture();
        fixture.Declare("SegmentTests", """
            [Fact] public void ReadsSegments() {
                var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "X", "Y.cs"));
            }
            """);

        Assert.Empty(fixture.Select([changedPath]).Selections);
    }

    [Theory]
    [InlineData("\"Y.cs\"")]
    [InlineData("@\".\\src\\X\\Y.cs\"")]
    [InlineData("\"./src/X/Y.cs\"")]
    [InlineData("\"src\\\\X\\\\Y.cs\"")]
    [InlineData("\"src/X/\\u0059.cs\"")]
    [InlineData("\"\"\"src/X/Y.cs\"\"\"")]
    public void DecodedLiteral_SelectsOneClassAcrossReadsAndChangedPaths(string expression)
    {
        using var fixture = new SourceFixture();
        fixture.Declare("LiteralTests", $$"""
            [Fact] public void ReadsLiteral() { var text = global::System.IO.File.ReadAllText({{expression}}); }
            [Theory] public void ReadsAgain() { var text = File.ReadAllText({{expression}}); }
            """);

        Assert.Equal([new FindingEvidenceSelection("Example.Tests", "LiteralTests")],
            fixture.Select([ChangedPath, "other/Y.cs"]).Selections);
    }

    [Theory]
    [InlineData("Path.Combine(\"src\", Dynamic(), \"Y.cs\")")]
    [InlineData("ReadSource(\"Y.cs\")")]
    [InlineData("\"src/X/Y.cs\" + suffix")]
    public void UnsupportedExpression_DoesNotBorrowUnrelatedLiterals(string expression)
    {
        using var fixture = new SourceFixture();
        fixture.Declare("DynamicTests", $$"""
            [Fact] public void ReadsDynamic() { var text = File.ReadAllText({{expression}}); }
            """);

        Assert.Empty(fixture.Select([ChangedPath]).Selections);
    }

    [Fact]
    public void SameRevision_ReusesMapAndNewRevisionRebuildsIt()
    {
        using var fixture = new SourceFixture();
        fixture.DeclareFilenameGuard("OriginalTests", "Y.cs");
        Assert.Equal("OriginalTests", Assert.Single(fixture.Select([ChangedPath]).Selections).TestClass);

        fixture.DeclareFilenameGuard("OriginalTests", "Other.cs");
        Assert.Equal("OriginalTests", Assert.Single(fixture.Select([ChangedPath]).Selections).TestClass);
        Assert.Empty(fixture.Select([ChangedPath], "main-2").Selections);
        fixture.DeclareFilenameGuard("ReplacementTests", "Y.cs");
        Assert.Equal("ReplacementTests", Assert.Single(fixture.Select([ChangedPath], "main-3").Selections).TestClass);
    }

    [Fact]
    public void ChangedTestSource_IsFreshAndDoesNotPoisonAnotherCandidate()
    {
        using var fixture = new SourceFixture();
        fixture.DeclareFilenameGuard("OverlayTests", "Other.cs");
        Assert.Empty(fixture.Select([ChangedPath]).Selections);

        fixture.DeclareFilenameGuard("OverlayTests", "Y.cs");
        Assert.Equal("OverlayTests", Assert.Single(fixture.Select(
            [ChangedPath, "tests/Example.Tests/OverlayTests.cs"]).Selections).TestClass);
        Assert.Empty(fixture.Select([ChangedPath]).Selections);
    }

    [Fact]
    public void InitiallyChangedTestSource_IsNotCachedAsBaseline()
    {
        using var fixture = new SourceFixture();
        fixture.DeclareFilenameGuard("OverlayTests", "Y.cs");
        Assert.Single(fixture.Select([ChangedPath, "tests/Example.Tests/OverlayTests.cs"]).Selections);
        fixture.DeclareFilenameGuard("OverlayTests", "Other.cs");

        Assert.Empty(fixture.Select([ChangedPath]).Selections);
    }

    [Fact]
    public void UnresolvableGuard_IsReportedRatherThanSilentlyLost()
    {
        using var fixture = new SourceFixture();
        fixture.DeclareFilenameGuard("GuardTests", "Y.cs");
        File.Move(Path.Combine(fixture.Project, "GuardTests.cs"), Path.Combine(fixture.Project, "Different.cs"));

        var result = fixture.Select([ChangedPath]);

        Assert.Empty(result.Selections);
        Assert.Equal(["GuardTests"], result.NotRun);
    }

    [Fact]
    public void EmptyChanges_SelectNothingAndMissingTestRootFailsLoudly()
    {
        using var fixture = new SourceFixture();
        Directory.Delete(Path.Combine(fixture.Root, "tests"), recursive: true);

        Assert.Empty(fixture.Select([]).Selections);
        Assert.Throws<DirectoryNotFoundException>(() => fixture.Select([ChangedPath]));
    }

    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.Execution/Processes/DispatchProcessHost.cs", "ProcessTreeGuiSuppressionTests")]
    [InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs", "PostLandingCanaryTests")]
    public void LiveGuardSegments_SelectTheHistoricalMissedGuard(string path, string guard)
    {
        var root = VerifiedRepositoryRoot.Find();
        var result = new PreTesterChangedFileGuardClasses().Select(root, [path], "live-source");

        Assert.Contains(result.Selections, selection => selection.TestClass == guard);
    }

    private sealed class SourceFixture : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public string Project => Path.Combine(Root, "tests", "Example.Tests");
        private readonly PreTesterChangedFileGuardClasses _selector = new();

        public SourceFixture()
        {
            Directory.CreateDirectory(Project);
            File.WriteAllText(Path.Combine(Project, "Example.Tests.csproj"), "<Project />");
        }

        public void Declare(string name, string methods) =>
            File.WriteAllText(Path.Combine(Project, name + ".cs"), "public class " + name + " { " + methods + " }");

        public void DeclareFilenameGuard(string name, string filename) =>
            Declare(name, $$"""
                [Fact] public void ReadsFile() { var text = File.ReadAllText("{{filename}}"); }
                """);

        public DeveloperDeferredTestSelection Select(IReadOnlyList<string> paths, string revision = "main-1") =>
            _selector.Select(Root, paths, revision);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
