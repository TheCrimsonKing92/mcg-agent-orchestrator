using Microsoft.CodeAnalysis.CSharp;

// Parallel-safe: each test owns a unique fixture root.
public sealed class RepositorySourceSnapshotTests
{
    [Fact]
    public void AccessorsMatchDirectReadsAndParsesIncludingLineEndingsAndGeneratedFiles()
    {
        using var fixture = new SourceSnapshotFixture();
        fixture.Write("tests/A/CrLf.cs", "class A {\r\n void M() {}\r\n}\r\n");
        fixture.Write("tests/A/Lf.cs", "class B {\n void M() {}\n}\n");
        fixture.Write("tests/A/Cr.cs", "class C {\r void M() {}\r}\r");
        fixture.Write("src/A/Bom.cs", "class D {\n void M() {}\n}", bom: true);
        fixture.Write("tests/A/obj/Generated.cs", "class E {}");
        fixture.Write("tests/A/Empty.cs", "");
        fixture.Write("tests/A/BlankLines.cs", "\n\n");
        fixture.Write("tests/A/Ignore.txt", "not source");
        var snapshot = RepositorySourceSnapshotCache.Get(fixture.Root);
        var expected = new[] { "tests", "src" }
            .SelectMany(area => Directory.EnumerateFiles(Path.Combine(fixture.Root, area), "*.cs", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(fixture.Root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, snapshot.Files);
        Assert.Contains("tests/A/obj/Generated.cs", snapshot.Files);
        foreach (var relative in snapshot.Files)
        {
            var full = Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            var text = File.ReadAllText(full);
            Assert.Equal(text, snapshot.GetText(relative));
            Assert.Equal(text, snapshot.GetText(full));
            Assert.Equal(File.ReadAllLines(full), snapshot.GetLines(relative));
            var direct = CSharpSyntaxTree.ParseText(text, path: full);
            var cached = snapshot.GetSyntaxTree(relative);
            Assert.Equal(full, cached.FilePath);
            Assert.Equal(text, cached.GetRoot().ToFullString());
            Assert.True(cached.GetRoot().IsEquivalentTo(direct.GetRoot()));
            foreach (var node in cached.GetRoot().DescendantNodes())
                Assert.Equal(direct.GetLineSpan(node.Span), cached.GetLineSpan(node.Span));
        }
        Assert.Equal(expected.Where(path => path.StartsWith("tests/", StringComparison.Ordinal)),
            snapshot.EnumerateFiles("tests").Select(path => Path.GetRelativePath(fixture.Root, path).Replace('\\', '/')));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.Files).Add("new.cs"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.GetLines(expected[0])).Add("new line"));
    }

    [Fact]
    public void MissingAndOutOfScopePathsFailLoudly()
    {
        using var fixture = new SourceSnapshotFixture();
        fixture.Write("src/A/File.cs", "class C {}");
        var snapshot = RepositorySourceSnapshotCache.Get(fixture.Root);
        Assert.Throws<FileNotFoundException>(() => snapshot.GetText("src/A/Missing.cs"));
        Assert.Throws<FileNotFoundException>(() => snapshot.GetLines("src/A/Missing.cs"));
        Assert.Throws<FileNotFoundException>(() => snapshot.GetSyntaxTree("src/A/Missing.cs"));
        Assert.Throws<DirectoryNotFoundException>(() => snapshot.EnumerateFiles("tests/missing"));
        Assert.Throws<ArgumentException>(() => snapshot.EnumerateFiles(fixture.Root + "x"));
        Assert.Throws<ArgumentException>(() => snapshot.EnumerateFiles("docs"));
    }

    [Fact]
    public void InventoryAndMaterializedContentStayFrozen()
    {
        using var fixture = new SourceSnapshotFixture();
        var file = fixture.Write("src/A/File.cs", "class Before {}");
        var snapshot = RepositorySourceSnapshotCache.Get(fixture.Root);
        var text = snapshot.GetText(file);
        File.WriteAllText(file, "class After {}");
        fixture.Write("src/A/Later.cs", "class Later {}");
        Assert.Single(snapshot.Files);
        Assert.Equal(text, snapshot.GetText(file));
        Assert.Equal(new[] { text }, snapshot.GetLines(file));
        Assert.Equal(text, snapshot.GetSyntaxTree(file).GetRoot().ToFullString());
    }
}
