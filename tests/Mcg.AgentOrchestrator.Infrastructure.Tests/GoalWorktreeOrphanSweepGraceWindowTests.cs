using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: filesystem timestamps and injected clocks belong to private trees.
public sealed class GoalWorktreeOrphanSweepGraceWindowTests : GoalWorktreeTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(0, false)]
    [Xunit.InlineData(599, false)]
    [Xunit.InlineData(600, false)]
    [Xunit.InlineData(-60, false)]
    [Xunit.InlineData(601, true)]
    public void RootTimestamp_TenMinuteBoundary_PreservesRecentTrees(int ageSeconds, bool removed)
    {
        CheckFreshness(ageSeconds, removed, descendant: false);
    }

    [Xunit.Theory]
    [Xunit.InlineData(599, false)]
    [Xunit.InlineData(600, false)]
    [Xunit.InlineData(-60, false)]
    [Xunit.InlineData(601, true)]
    public void DescendantTimestamp_OldRoot_UsesNewestWrite(int ageSeconds, bool removed)
    {
        CheckFreshness(ageSeconds, removed, descendant: true);
    }

    private void CheckFreshness(int ageSeconds, bool removed, bool descendant)
    {
        var repo = CreateSeededRepository();
        try
        {
            var fixture = new OrphanSweepTestFixture(repo);
            var path = fixture.AddDirectory("grace-candidate");
            var writeTime = OrphanSweepTestFixture.ReferenceTime.AddSeconds(-ageSeconds).UtcDateTime;
            if (descendant)
            {
                File.SetLastWriteTimeUtc(Path.Combine(path, "nested", "content.txt"), writeTime);
                Assert.True(Directory.GetLastWriteTimeUtc(path) < writeTime);
            }
            else Directory.SetLastWriteTimeUtc(path, writeTime);

            var result = fixture.Sweep();

            Assert.Equal(removed ? 1 : 0, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.Equal(!removed, Directory.Exists(path));
            Assert.Equal(removed ? 1 : 0, fixture.Deletions.Count);
            Assert.Single(fixture.Outcomes);
            fixture.AssertOutcome(path, removed ? "removed" : "recent");
            Assert.Empty(fixture.Warnings);
        }
        finally { DeleteDirectory(repo); }
    }
}
