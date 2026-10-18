using Mcg.AgentOrchestrator.Core;
using Xunit;
using Repository = ReverseDependencyIndexScopeTests.Repository;

// Parallel-safe: every case owns a unique repository fixture.
public sealed class RepositoryTestImpactClosureTests
{
    [Fact]
    public void Measure_ReferencedSources_CountsTheSameClosureAsReader()
    {
        using var repository = Repository.Create();
        AssertCountMatchesReader(repository, 3);
        repository.Write("src/Mcg.AgentOrchestrator.App/Extra.cs", "public class Extra { }");
        repository.Write("src/Mcg.AgentOrchestrator.App/Nested/More.cs", "public class More { }");
        AssertCountMatchesReader(repository, 5);
    }

    [Fact]
    public void Measure_AboveCap_ReturnsFullCountWhenReaderAbandons()
    {
        using var repository = Repository.Create();
        var selection = new FileSystemTestClassDeclarationReader(repository.Root, maximumIndexedSourceFiles: 2)
            .ReadReverseDependentTestClasses([Repository.CoreSource]);
        Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Assert.Equal(ReverseDependencyDegradationKind.IndexedSourceBound, selection.DegradationKind);
        var measured = RepositoryTestImpactClosure.MeasureIndexedSources(repository.Root, 2);
        Assert.NotNull(measured);
        Assert.Equal(3, measured.IndexedSourceFileCount);
        Assert.Equal(selection.Headroom!.IndexedSourceFileCount, measured.IndexedSourceFileCount);
        Assert.Equal(2, measured.IndexedSourceFileCap);
    }

    [Fact]
    public void Measure_MissingTestProject_ReturnsNull()
    {
        using var repository = Repository.Create();
        File.Delete(repository.GetPath($"{Repository.TestDirectory}/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
        Assert.Null(RepositoryTestImpactClosure.MeasureIndexedSources(repository.Root));
    }

    [Fact]
    public void Measure_ReferenceWithoutInclude_ReturnsNull()
    {
        using var repository = Repository.Create();
        repository.Write("src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj",
            "<Project><ItemGroup><ProjectReference /></ItemGroup></Project>");
        Assert.Null(RepositoryTestImpactClosure.MeasureIndexedSources(repository.Root));
    }

    [Fact]
    public void DefaultCap_UsesReaderBound()
    {
        Assert.Equal(ReverseDependencyTestImpactReader.MaximumIndexedSourceFiles,
            RepositoryTestImpactClosure.DefaultIndexedSourceFileCap);
        using var repository = Repository.Create();
        Assert.Equal(RepositoryTestImpactClosure.DefaultIndexedSourceFileCap,
            RepositoryTestImpactClosure.MeasureIndexedSources(repository.Root)!.IndexedSourceFileCap);
    }

    private static void AssertCountMatchesReader(Repository repository, int expected)
    {
        var selection = new FileSystemTestClassDeclarationReader(repository.Root, maximumIndexedSourceFiles: 10)
            .ReadReverseDependentTestClasses([Repository.CoreSource]);
        var measured = RepositoryTestImpactClosure.MeasureIndexedSources(repository.Root, 10);
        Assert.NotNull(measured);
        Assert.Equal(expected, measured.IndexedSourceFileCount);
        Assert.NotNull(selection.Headroom);
        Assert.Equal(selection.Headroom.IndexedSourceFileCount, measured.IndexedSourceFileCount);
        Assert.Equal(10, measured.IndexedSourceFileCap);
    }
}
