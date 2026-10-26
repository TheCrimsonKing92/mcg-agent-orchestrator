using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class OwnedRootObservationAggregateTests
{
    [Fact]
    public void ObserveOnlyRootsCollapseToOneLinePerSweep()
    {
        var reports = Enumerable.Range(0, 30)
            .Select(index => $@"observe-only unregistered build root: C:\repo\isolated\goals\g{index:00}").ToList();
        const string failure = @"owned-root cleanup persistence failed path=C:\repo\isolated\runs\r1";
        const string janitor = "temp-artifact-janitor failed=IOException";
        // A non-observation before the first observation pins the aggregate's insertion position.
        reports.Insert(0, failure);
        reports.Insert(12, janitor);
        var result = new TerminalGoalSweepOwnedRootResult(0, 0, 0, 0, reports);
        Assert.Equal(32, result.ObserveOnlyReports.Count);
        Assert.Equal(3, result.OperatorEvents.Count);
        Assert.Equal($"SWEEP_OWNED_ROOT_OBSERVED detail=\"{failure}\"", result.OperatorEvents[0]);
        Assert.Equal(@"SWEEP_OWNED_ROOT_OBSERVED detail=""observe-only unregistered build roots: count=30 first=C:\repo\isolated\goals\g00""",
            result.OperatorEvents[1]);
        Assert.Equal($"SWEEP_OWNED_ROOT_OBSERVED detail=\"{janitor}\"", result.OperatorEvents[2]);
        Assert.Single(result.OperatorEvents.Where(line => line.Contains("observe-only unregistered build roots:", StringComparison.Ordinal)));
        var none = new TerminalGoalSweepOwnedRootResult(0, 0, 0, 0, [failure, janitor]);
        Assert.Equal(2, none.OperatorEvents.Count);
        Assert.DoesNotContain(none.OperatorEvents, line => line.Contains("observe-only unregistered build roots:", StringComparison.Ordinal));
        const string summary = "SWEEP_BUILD_ROOT_RETENTION window-days=3 reclaimed=0 bytes=0 skipped=0 reasons=none";
        var withSummary = result with { BuildRootRetentionSummary = summary, OrphanFixtureEvents = ["orphan-fixture"] };
        Assert.Equal(summary, withSummary.OperatorEvents[^1]);
        Assert.Single(withSummary.OperatorEvents.Where(line => line == summary));
        Assert.Equal(5, withSummary.OperatorEvents.Count);
        Assert.Equal(reports, withSummary.ObserveOnlyReports);
    }
}
