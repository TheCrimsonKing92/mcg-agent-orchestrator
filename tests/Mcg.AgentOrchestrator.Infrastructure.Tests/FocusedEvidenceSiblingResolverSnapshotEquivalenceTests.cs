using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test fully constructs its own root before obtaining an immutable snapshot.
public sealed class FocusedEvidenceSiblingResolverSnapshotEquivalenceTests
{
    private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    [Xunit.Fact]
    public void AbstractBaseFamilyReturnsTheSameOrderedSiblingsFromSnapshot()
    {
        using var fixture = CreateFixture();

        var siblings = AssertEquivalent(fixture.Root, "Fixture.RequestedTests");

        Assert.NotEmpty(siblings);
        Assert.Equal(new[] { "AlphaDerivedTests", "ZetaDerivedTests" }, siblings);
        Assert.Contains("AlphaDerivedTests", siblings);
        Assert.DoesNotContain("FamilyTestBase", siblings);
        Assert.DoesNotContain("PublicHelper", siblings);
        Assert.DoesNotContain("NestedProjectTests", siblings);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Fixture.BrokenTests")]
    [Xunit.InlineData("Fixture.UnknownTests")]
    public void MalformedOrUnknownClassReturnsTheSameEmptyListFromSnapshot(string requestedClass)
    {
        using var fixture = CreateFixture();

        Assert.Empty(AssertEquivalent(fixture.Root, requestedClass));
    }

    private static IReadOnlyList<string> AssertEquivalent(string root, string requestedClass)
    {
        var expected = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(root, Project, requestedClass);
        var actual = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            root, Project, requestedClass,
            readText: path => RepositorySourceSnapshotCache.Get(root).GetText(path),
            parse: (path, _) => RepositorySourceSnapshotCache.Get(root).GetSyntaxTree(path));

        Assert.Equal(expected, actual);
        return actual;
    }

    private static SourceSnapshotFixture CreateFixture()
    {
        var fixture = new SourceSnapshotFixture();
        fixture.Write(Project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        const string directory = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
        fixture.Write(directory + "Base.cs", """
            namespace Fixture;
            public abstract class FamilyTestBase { }
            public sealed class RequestedTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }
            public sealed class PublicHelper : FamilyTestBase { public void Runs() { } }
            """);
        fixture.Write(directory + "Derived.cs", """
            namespace Fixture;
            public sealed class ZetaDerivedTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }
            public sealed class AlphaDerivedTests : FamilyTestBase { [Xunit.Theory] public void Runs() { } }
            """);
        fixture.Write(directory + "Nested/Nested.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.Write(directory + "Nested/NestedProjectTests.cs", """
            namespace Fixture;
            public sealed class NestedProjectTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }
            """);
        fixture.Write(directory + "Broken.cs", """
            namespace Fixture;
            public sealed class BrokenTests { [Xunit.Fact] public void Runs( { } }
            """);
        return fixture;
    }
}
