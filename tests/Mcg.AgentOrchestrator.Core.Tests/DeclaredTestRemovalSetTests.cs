using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: parsing and partitioning have no external state.
public sealed class DeclaredTestRemovalSetTests
{
    [Fact]
    public void FromObjective_InterleavedRemovals_PreservesIdentitiesAndOrdinaryOrder()
    {
        var identities = Enumerable.Range(0, 50).Select(i => $"Example.Tests.FooTests.Removed{i:D2}").ToArray();
        var bullets = identities.Select(identity => $"test-removal: `{identity}`").ToList();
        bullets.Insert(25, "The behavior works.");
        var set = DeclaredTestRemovalSet.FromObjective(Objective(bullets));

        Assert.Equal(["The behavior works."], set.Ordinary);
        Assert.Equal(50, set.Count);
        Assert.Equal(identities, set.Declarations.Select(item => item.TestIdentity));
        Assert.Equal(0, set.GetOrdinaryIndex(26));
        Assert.Null(set.GetOrdinaryIndex(25));
        Assert.Null(set.GetOrdinaryIndex(27));
        Assert.Equal(AcceptanceCriteriaParser.Parse(Objective(bullets)), set.Declarations);
    }

    [Fact]
    public void FromObjective_DuplicateRemovals_KeepsOrderAndMultiplicity()
    {
        var set = DeclaredTestRemovalSet.FromObjective(Objective([
            "test-removal: FooTests.First", "ordinary", "test-removal: FooTests.Second",
            "test-removal: FooTests.First", "ordinary", "another ordinary"]));

        Assert.Equal(3, set.Count);
        Assert.Equal(["FooTests.First", "FooTests.Second", "FooTests.First"],
            set.Declarations.Select(item => item.TestIdentity));
        Assert.Equal(["ordinary", "ordinary", "another ordinary"], set.Ordinary);
        Assert.Equal(0, set.GetOrdinaryIndex(2));
        Assert.Equal(0, set.GetOrdinaryIndex(5));
        Assert.Equal(1, set.GetOrdinaryIndex(6));
    }

    [Theory]
    [InlineData("test-removal:")]
    [InlineData("test-removal: ``")]
    [InlineData("Remove FooTests.Method")]
    public void FromObjective_UnclassifiedBullet_RemainsOrdinary(string bullet)
    {
        var set = DeclaredTestRemovalSet.FromObjective(Objective([bullet]));
        Assert.Equal([bullet], set.Ordinary);
        Assert.Empty(set.Declarations);
    }

    [Fact]
    public void FromObjective_AllRemovals_HasNoOrdinaryCriteria()
    {
        var set = DeclaredTestRemovalSet.FromObjective(Objective(["TEST-REMOVAL: FooTests.Method"]));
        Assert.Empty(set.Ordinary);
        Assert.Equal("FooTests.Method", Assert.Single(set.Declarations).TestIdentity);
        Assert.Null(set.GetOrdinaryIndex(1));
    }

    private static string Objective(IEnumerable<string> bullets) =>
        "## Acceptance criteria\n" + string.Join("\n", bullets.Select((text, i) => $"{i + 1}. {text}"));
}
