using System.Runtime.CompilerServices;

// Parallel-safe: repository reads use the shared immutable snapshot; fixtures own unique roots.
public sealed class RepositoryLayoutConventionsSnapshotTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealRepositoryOverloadsReturnIdenticalOrderedViolations(bool useSeededInventories)
    {
        var root = RepositoryRoot();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var multiTypeFiles = useSeededInventories ? RepositoryLayoutConventions.SeededMultiPublicTypeFiles : [];
        var dottedNames = useSeededInventories ? RepositoryLayoutConventions.SeededDottedFileNames : [];
        var ambientRootFiles = useSeededInventories ? RepositoryLayoutConventions.SeededAmbientRepositoryRootFiles : [];

        var fromPath = RepositoryLayoutConventions.Evaluate(root, multiTypeFiles, dottedNames, ambientRootFiles);
        var fromSnapshot = RepositoryLayoutConventions.Evaluate(snapshot, multiTypeFiles, dottedNames, ambientRootFiles);

        if (!useSeededInventories)
        {
            Assert.NotEmpty(fromPath);
        }

        Assert.Equal(fromPath, fromSnapshot);
    }

    [Fact]
    public void FixtureOverloadsReturnIdenticalOrderedViolations()
    {
        using var fixture = new SourceSnapshotFixture();
        fixture.Write("tests/Synthetic/ZPair.cs", "public class FirstTest { } public class SecondTest { }");
        fixture.Write("tests/Synthetic/Alpha.Part.cs", "public class AlphaPart { }");
        fixture.Write("tests/Synthetic/obj/Ignored.cs", "public class Broken {");
        fixture.Write("tests/Synthetic/Ignored.g.cs", "public class Broken {");
        var snapshot = new RepositorySourceSnapshot(fixture.Root, new NoOpObserver());

        var fromPath = RepositoryLayoutConventions.Evaluate(fixture.Root, [], [], []);
        var fromSnapshot = RepositoryLayoutConventions.Evaluate(snapshot, [], [], []);

        Assert.Collection(fromPath,
            violation =>
            {
                Assert.Equal("tests/Synthetic/Alpha.Part.cs", violation.RelativePath);
                Assert.Equal("dotted-test-file-name", violation.Rule);
            },
            violation =>
            {
                Assert.Equal("tests/Synthetic/ZPair.cs", violation.RelativePath);
                Assert.Equal("multiple-top-level-public-types", violation.Rule);
            });
        Assert.Equal(fromPath, fromSnapshot);
    }

    [Fact]
    public void SnapshotRejectsUngrandfatheredMultiPublicTypeFile()
    {
        using var fixture = new SourceSnapshotFixture();
        const string relativePath = "tests/Synthetic/NewPair.cs";
        fixture.Write(relativePath, "public class FirstTest { } public sealed class SecondTest { }");
        var snapshot = new RepositorySourceSnapshot(fixture.Root, new NoOpObserver());

        var violation = Assert.Single(RepositoryLayoutConventions.Evaluate(snapshot, [], [], []));

        Assert.Equal(new RepositoryLayoutViolation(
            relativePath,
            "multiple-top-level-public-types",
            $"Test source '{relativePath}' declares multiple top-level public types: " +
            "FirstTest, SecondTest. Move each public type to its own dot-free file. " +
            $"See {RepositoryLayoutConventions.DocumentationPath}."), violation);
    }

    [Fact]
    public void MalformedSourceHasIdenticalParseDiagnosticsThroughBothOverloads()
    {
        using var fixture = new SourceSnapshotFixture();
        const string relativePath = "tests/Synthetic/Broken.cs";
        var path = fixture.Write(relativePath, "public class Broken {");
        var snapshot = new RepositorySourceSnapshot(fixture.Root, new NoOpObserver());

        var fromPath = RepositoryLayoutConventions.Evaluate(fixture.Root, [], [], []);
        var fromSnapshot = RepositoryLayoutConventions.Evaluate(snapshot, [], [], []);

        var violation = Assert.Single(fromSnapshot);
        Assert.Equal(relativePath, violation.RelativePath);
        Assert.Equal("unparseable-test-source", violation.Rule);
        Assert.Contains(path, violation.Message, StringComparison.Ordinal);
        Assert.Equal(fromPath, fromSnapshot);
    }

    [Fact]
    public void DeletedSnapshotSourceReportsUnreadableViolationWithoutStaleInventoryNoise()
    {
        using var fixture = new SourceSnapshotFixture();
        const string relativePath = "tests/Synthetic/Pair.Part.cs";
        var path = fixture.Write(relativePath, "public class FirstTest { } public class SecondTest { }");
        var snapshot = new RepositorySourceSnapshot(fixture.Root, new NoOpObserver());
        File.Delete(path);

        var violation = Assert.Single(RepositoryLayoutConventions.Evaluate(
            snapshot, [relativePath], [relativePath], [relativePath]));

        Assert.Equal(relativePath, violation.RelativePath);
        Assert.Equal("unreadable-test-source", violation.Rule);
        Assert.Contains("FileNotFoundException", violation.Message, StringComparison.Ordinal);
        Assert.Contains("The layout guard cannot pass without checking it.", violation.Message, StringComparison.Ordinal);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot)
            ? verifiedRoot
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));

    private sealed class NoOpObserver : ISourceSnapshotObserver
    {
        public void OnEnumerated(string root) { }
        public void OnTextRead(string root, string relativePath) { }
        public void OnParsed(string root, string relativePath) { }
    }
}
