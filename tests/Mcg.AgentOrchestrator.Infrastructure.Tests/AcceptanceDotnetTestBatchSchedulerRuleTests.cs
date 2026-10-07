using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: generic rules use only per-test strings and predicates.
public sealed class AcceptanceDotnetTestBatchSchedulerRuleTests
{
    [Fact]
    public void GroupPreservesIndexesAndCombinesContiguousPartitions()
    {
        var groups = AcceptanceDotnetTestBatchScheduler.Group<string>(["a", "p1", "p2", "b"], IsPartition);
        Assert.Collection(groups,
            group => AssertGroup(group, 0, ["a"], false),
            group => AssertGroup(group, 1, ["p1", "p2"], true),
            group => AssertGroup(group, 3, ["b"], false));
    }

    [Fact]
    public void MixedContiguousPartitionsCanOverlap()
    {
        Assert.True(CanOverlap(["p1", "p2", "a", "b"]));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void PresenceAndMultipleSlotsAreRequired(bool present, int budget)
    {
        Assert.False(CanOverlap(["p1", "p2", "a", "b"], present, budget));
    }

    [Fact]
    public void EveryCheckMustBeAnMtpDotnetTest()
    {
        Assert.False(CanOverlap(["p1", "p2", "a", "b"], isMtp: check => check != "b"));
    }

    [Theory]
    [InlineData("a", "b")]
    [InlineData("p1", "p2")]
    [InlineData("p1", "a", "p2")]
    public void PartitionsMustBePresentMixedAndContiguous(params string[] checks)
    {
        Assert.False(CanOverlap(checks));
    }

    [Fact]
    public void ExclusiveResourcesOnNonPartitionsPreventOverlap()
    {
        Assert.False(CanOverlap(["p1", "p2", "a", "b"], hasExclusive: check => check == "a"));
    }

    [Fact]
    public void ExclusiveResourcesOnPartitionsAllowOverlap()
    {
        Assert.True(CanOverlap(["p1", "p2", "a", "b"], hasExclusive: check => check == "p1"));
    }

    private static bool IsPartition(string check) => check.StartsWith('p');

    private static bool CanOverlap(string[] checks, bool present = true, int budget = 2,
        Func<string, bool>? isMtp = null, Func<string, bool>? hasExclusive = null) =>
        AcceptanceDotnetTestBatchScheduler.CanOverlap(checks, present, budget,
            isMtp ?? (_ => true), IsPartition, hasExclusive ?? (_ => false));

    private static void AssertGroup(AcceptanceDotnetTestBatchGroup<string> group,
        int index, string[] checks, bool isPartition)
    {
        Assert.Equal(index, group.Index);
        Assert.Equal(checks, group.Checks);
        Assert.Equal(isPartition, group.IsInfrastructurePartition);
    }
}
