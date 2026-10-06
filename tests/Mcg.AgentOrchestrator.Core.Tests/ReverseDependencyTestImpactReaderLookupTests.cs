using Mcg.AgentOrchestrator.Core;

public sealed class ReverseDependencyTestImpactReaderLookupTests
{
    [Fact]
    public void SixChangedFilesResolveToTheUnionOfSinglePathSelections()
    {
        using var repository = new Repository();
        var paths = Enumerable.Range(0, 6).Select(repository.AddSourceAndConsumer).ToArray();
        var expected = paths.SelectMany(path =>
        {
            var single = ReverseDependencyTestImpactReader.Read(repository.Root, [path]);
            Assert.Equal(ReverseDependencySelectionOutcome.Resolved, single.Outcome);
            Assert.Single(single.TestClassNames);
            return single.TestClassNames;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, paths);

        Assert.True(result.Resolved, result.Reason);
        Assert.Equal(6, result.TestClassNames.Count);
        Assert.Equal(expected, result.TestClassNames);
        Assert.Null(result.DegradationKind);
    }

    [Fact]
    public void NinetySevenReferencingClassesResolveWithoutTheFocusedSelectionBound()
    {
        using var repository = new Repository();
        var path = repository.AddSourceAndConsumer(0);
        for (var index = 1; index < 97; index++)
            repository.Write($"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Extra{index}Tests.cs",
                $"public class Extra{index}Tests {{ private ShadowWidget0 value; [Xunit.Fact] public void Example() {{ }} }}");
        var bounded = ReverseDependencyTestImpactReader.Read(repository.Root, [path]);
        Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, bounded.Outcome);

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [path]);

        Assert.True(result.Resolved, result.Reason);
        Assert.Equal(97, result.TestClassNames.Count);
    }

    [Fact]
    public void DeletedPathReportsUnavailableAndNoPartialSelection()
    {
        using var repository = new Repository();
        var path = repository.AddSourceAndConsumer(0);
        var deleted = repository.AddSourceAndConsumer(1);
        File.Delete(Path.Combine(repository.Root, deleted));

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [path, deleted]);

        Assert.False(result.Resolved);
        Assert.Equal("Unavailable", result.DegradationKind);
        Assert.NotEmpty(result.Reason!);
        Assert.Empty(result.TestClassNames);
    }

    [Fact]
    public void SinglePathMatchesTheDefaultReader()
    {
        using var repository = new Repository();
        var path = repository.AddSourceAndConsumer(0);
        var expected = ReverseDependencyTestImpactReader.Read(repository.Root, [path]);

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [path]);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, expected.Outcome);
        Assert.True(result.Resolved, result.Reason);
        Assert.Equal(expected.TestClassNames, result.TestClassNames);
        Assert.Equal(["Widget0ConsumerTests"], result.TestClassNames);
    }

    private sealed class Repository : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-shadow-lookup-{Guid.NewGuid():N}");

        internal Repository()
        {
            Write("src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" /></ItemGroup></Project>");
        }

        internal string AddSourceAndConsumer(int index)
        {
            var path = $"src/Fixture/ShadowWidget{index}.cs";
            Write(path, $"public class ShadowWidget{index} {{ }}");
            Write($"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Widget{index}ConsumerTests.cs",
                $"public class Widget{index}ConsumerTests {{ private ShadowWidget{index} value; [Xunit.Fact] public void Example() {{ }} }}");
            return path;
        }

        internal void Write(string path, string text)
        {
            var absolute = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, text);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
