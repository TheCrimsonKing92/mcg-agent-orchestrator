using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each invocation owns its in-memory paths and assignments.
public sealed class LearnedCheckNamesTests
{
    [Fact]
    public void Assign_UniqueFileNames_KeepsPlainNames()
    {
        var result = LearnedCheckNames.Assign(["tests/A/A.Tests.csproj", "tests/B/B.Tests.csproj"]);
        Assert.Equal("A.Tests", result.Names["tests/A/A.Tests.csproj"]);
        Assert.Equal("B.Tests", result.Names["tests/B/B.Tests.csproj"]);
        Assert.Equal(2, result.Names.Count);
        Assert.Empty(result.Collisions);
    }

    [Fact]
    public void Assign_SharedFileName_QualifiesEveryMember()
    {
        var result = LearnedCheckNames.Assign(
            ["tests/A/Tests.csproj", "tests/B/Tests.csproj", "tests/C/Other.csproj"]);
        Assert.Equal("tests.A.Tests", result.Names["tests/A/Tests.csproj"]);
        Assert.Equal("tests.B.Tests", result.Names["tests/B/Tests.csproj"]);
        Assert.Equal("Other", result.Names["tests/C/Other.csproj"]);
        Assert.Equal(3, result.Names.Count);
        Assert.Empty(result.Collisions);
    }

    [Theory]
    [InlineData("core", "Core")]
    [InlineData("a-b", "a.b")]
    public void Assign_SharedPlainSlug_QualifiesBothNames(string first, string second)
    {
        var a = $"tests/A/{first}.csproj";
        var b = $"tests/B/{second}.csproj";
        var result = LearnedCheckNames.Assign([a, b]);
        Assert.Equal($"tests.A.{first}", result.Names[a]);
        Assert.Equal($"tests.B.{second}", result.Names[b]);
        Assert.Empty(result.Collisions);
    }

    [Fact]
    public void Assign_QualifiedSlugCollision_ReportsPathsInOrdinalOrder()
    {
        var result = LearnedCheckNames.Assign(["tests/a/b/X.csproj", "tests/a-b/X.csproj"]);
        Assert.Equal("tests.a-b.X", result.Names["tests/a-b/X.csproj"]);
        Assert.Equal("tests.a.b.X", result.Names["tests/a/b/X.csproj"]);
        Assert.Equal(new[] { "tests/a-b/X.csproj", "tests/a/b/X.csproj" }, result.Collisions);
    }

    [Fact]
    public void Assign_QualifiedAndPlainSlugCollision_ReportsOnlyUnresolvedPaths()
    {
        var result = LearnedCheckNames.Assign(
            ["tests/A/X.csproj", "tests/B/X.csproj", "other/tests.A.X.csproj"]);
        Assert.Equal("tests.B.X", result.Names["tests/B/X.csproj"]);
        Assert.Equal("tests.A.X", result.Names["other/tests.A.X.csproj"]);
        Assert.Equal(new[] { "other/tests.A.X.csproj", "tests/A/X.csproj" }, result.Collisions);
    }

    [Fact]
    public void Assign_ReorderedInputs_ProducesIdenticalMappingsAndCollisions()
    {
        string[] paths = ["tests/B/Tests.csproj", "tests/A/Tests.csproj", "tests/C/Other.csproj",
            "tests/a/b/X.csproj", "tests/a-b/X.csproj"];
        var result = LearnedCheckNames.Assign(paths);
        foreach (var reordered in new[] { paths.Reverse().ToArray(), paths.Order(StringComparer.Ordinal).ToArray() })
        {
            var other = LearnedCheckNames.Assign(reordered);
            Assert.Equal(result.Names.OrderBy(item => item.Key, StringComparer.Ordinal),
                other.Names.OrderBy(item => item.Key, StringComparer.Ordinal));
            Assert.Equal(result.Collisions, other.Collisions);
        }
    }

    [Fact]
    public void Assign_DuplicatePath_RefusesAmbiguousOccurrences()
    {
        const string path = "tests/A/X.csproj";
        var result = LearnedCheckNames.Assign([path, path]);
        Assert.Equal("tests.A.X", Assert.Single(result.Names).Value);
        Assert.Equal(path, Assert.Single(result.Collisions));
    }

    [Fact]
    public void Assign_EmptyInput_ReturnsEmptyResult()
    {
        var result = LearnedCheckNames.Assign([]);
        Assert.Empty(result.Names);
        Assert.Empty(result.Collisions);
    }
}
