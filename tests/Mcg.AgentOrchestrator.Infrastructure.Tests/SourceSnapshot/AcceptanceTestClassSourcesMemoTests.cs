using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: repository sources are read-only and each fixture owns a unique root.
public sealed class AcceptanceTestClassSourcesMemoTests
{
    [Fact]
    public void RealRepository_MemoMatchesFreshScan()
    {
        var repository = VerifiedRepositoryRoot.Find();
        var memo = AcceptanceTestClassSourcesMemo.Get(repository);
        var fresh = AcceptanceTestClassSourceScanner.ScanSources(repository);

        Assert.NotEmpty(fresh);
        Assert.Equal(fresh.Count, memo.Count);
        for (var index = 0; index < fresh.Count; index++)
        {
            Assert.Equal(fresh[index].FullName, memo[index].FullName);
            Assert.Equal<string>(fresh[index].SourcePaths, memo[index].SourcePaths);
            Assert.Equal(fresh[index].SourceText, memo[index].SourceText);
        }
    }

    [Fact]
    public void EquivalentRootSpellings_ReturnSameList()
    {
        var repository = VerifiedRepositoryRoot.Find();
        var sources = AcceptanceTestClassSourcesMemo.Get(repository);
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), repository);

        Assert.False(Path.IsPathRooted(relative));
        Assert.Same(sources, AcceptanceTestClassSourcesMemo.Get(repository + Path.DirectorySeparatorChar));
        Assert.Same(sources, AcceptanceTestClassSourcesMemo.Get(relative));
        if (OperatingSystem.IsWindows())
            Assert.Same(sources, AcceptanceTestClassSourcesMemo.Get(repository.ToUpperInvariant()));
    }

    [Fact]
    public void DifferentRoot_ReturnsItsOwnTestSources()
    {
        using var fixture = new SourceSnapshotFixture();
        const string path = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MemoProbe.cs";
        const string text = "public sealed class MemoProbe { [Fact] public void Run() { } }";
        fixture.Write(path, text);
        var repositorySources = AcceptanceTestClassSourcesMemo.Get(VerifiedRepositoryRoot.Find());
        var fixtureSources = AcceptanceTestClassSourcesMemo.Get(fixture.Root);

        Assert.NotSame(repositorySources, fixtureSources);
        var source = Assert.Single(fixtureSources);
        Assert.Equal("MemoProbe", source.FullName);
        Assert.Equal(new[] { path }, source.SourcePaths);
        Assert.Equal(text, source.SourceText);
        Assert.DoesNotContain(repositorySources, item => item.FullName == source.FullName);
    }

    [Fact]
    public void MemoizedSource_SnapshotDetectsUnsafeClearClassAndLine()
    {
        using var fixture = new SourceSnapshotFixture();
        const string path = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/UnsafeClearProbe.cs";
        fixture.Write(path, """
            public sealed class UnsafeClearProbe
            {
                [Fact] public void Clear()
                {
                    Environment.SetEnvironmentVariable("MCG_DOTNET_ISOLATED_ROOT", null);
                }
            }
            """);
        var source = Assert.Single(AcceptanceTestClassSourcesMemo.Get(fixture.Root));
        var sourcePath = Assert.Single(source.SourcePaths);
        var syntax = RepositorySourceSnapshotCache.Get(fixture.Root).GetSyntaxTree(sourcePath).GetRoot();
        var declaration = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            node => LaneIsolatedRootScanner.ClassName(node) == source.FullName);
        var clear = Assert.Single(LaneIsolatedRootScanner.FindUnsafeClears(declaration));

        Assert.Equal("UnsafeClearProbe", source.FullName);
        Assert.Equal(path, sourcePath);
        Assert.Equal(source.FullName, LaneIsolatedRootScanner.ClassName(
            clear.Node.Ancestors().OfType<ClassDeclarationSyntax>().First()));
        Assert.Equal(5, clear.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        Assert.False(clear.Child);
    }
}
