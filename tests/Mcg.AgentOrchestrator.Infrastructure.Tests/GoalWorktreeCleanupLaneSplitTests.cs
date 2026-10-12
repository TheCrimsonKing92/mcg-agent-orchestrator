using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads checked-in configuration and exercises pure offer-policy decisions.
public sealed class GoalWorktreeCleanupLaneSplitTests
{
    private const string CohortLaneName = "Goal worktree cleanup cohort";
    private const string GitLaneName = "Goal worktree cleanup git";

    [Xunit.Fact]
    public void SplitFiltersPreserveEveryOriginalClassExactlyOnce()
    {
        var settings = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot());
        Xunit.Assert.DoesNotContain(settings.InfrastructureTestLanes, lane => lane.Name == "Goal worktree cleanup");
        var cohort = Xunit.Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == CohortLaneName);
        var git = Xunit.Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == GitLaneName);
        var cohortClasses = ParseFilterClasses(cohort.Filter);
        var gitClasses = ParseFilterClasses(git.Filter);
        string[] expectedClasses =
        [
            "AcceptanceCohortWorkflowTests",
            "AcceptanceVerdictCarryForwardTests",
            "GoalWorktreeTestsAcceptanceLanding",
            "GoalWorktreeTestsCleanupHookDelegates",
            "GoalWorktreeTestsOrphanEphemeralSweep",
            "GoalWorktreeTestsRebaseMerge",
            "GoalWorktreeTestsRemoveCleanup"
        ];

        Xunit.Assert.Equal("AcceptanceCohortWorkflowTests", Xunit.Assert.Single(cohortClasses));
        Xunit.Assert.Empty(cohortClasses.Intersect(gitClasses, StringComparer.Ordinal));
        Xunit.Assert.Equal(
            expectedClasses.OrderBy(name => name, StringComparer.Ordinal),
            cohortClasses.Concat(gitClasses).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Xunit.Fact]
    public void SplitLanesRetainPartitionAndHaveIndependentDurationEstimates()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(repositoryRoot);
        var cohort = Xunit.Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == CohortLaneName);
        var git = Xunit.Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == GitLaneName);
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "config", "acceptance-manifest.json")));
        var partition = Xunit.Assert.Single(
            document.RootElement.GetProperty("engine").GetProperty("localTestPartitions").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "GoalWorktree");
        var laneNames = partition.GetProperty("laneNames").EnumerateArray()
            .Select(item => item.GetString()).ToArray();

        Xunit.Assert.Contains(CohortLaneName, laneNames);
        Xunit.Assert.Contains(GitLaneName, laneNames);
        Xunit.Assert.DoesNotContain("Goal worktree cleanup", laneNames);
        Xunit.Assert.Empty(cohort.ExclusiveResourceKeys);
        Xunit.Assert.Empty(git.ExclusiveResourceKeys);
        Xunit.Assert.Equal(266d, cohort.EstimatedSerialSeconds);
        Xunit.Assert.Equal(192d, git.EstimatedSerialSeconds);
        Xunit.Assert.Equal(458d, cohort.EstimatedSerialSeconds + git.EstimatedSerialSeconds);
    }

    [Xunit.Theory]
    [Xunit.InlineData(CohortLaneName)]
    [Xunit.InlineData(GitLaneName)]
    public void NewLaneEstimateIsOfferedWithoutRemoteSamples(string laneName)
    {
        var settings = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot());
        var lane = Xunit.Assert.Single(settings.InfrastructureTestLanes, item => item.Name == laneName);
        var decision = RemoteLaneOfferPolicy.Decide(new RemoteLaneOfferInputs(
            lane.Name, null, 0, lane.EstimatedSerialSeconds, null, 0, 640));

        Xunit.Assert.True(decision.Offer, decision.Reason);
        Xunit.Assert.Equal(lane.EstimatedSerialSeconds, decision.LocalSeconds);
    }

    [Xunit.Fact]
    public void OriginalMeasuredLaneIsRejectedAsRemoteSlower()
    {
        var decision = RemoteLaneOfferPolicy.Decide(new RemoteLaneOfferInputs(
            "Goal worktree cleanup", 458, 3, 458, 853, 37, 675));

        Xunit.Assert.False(decision.Offer);
        Xunit.Assert.Equal("remote-slower", decision.Reason);
    }

    private static string[] ParseFilterClasses(string filter)
    {
        const string prefix = "FullyQualifiedName~";
        var classes = filter.Split('|').Select(clause =>
        {
            Xunit.Assert.StartsWith(prefix, clause);
            var className = clause[prefix.Length..];
            Xunit.Assert.False(string.IsNullOrWhiteSpace(className));
            return className;
        }).ToArray();
        Xunit.Assert.Equal(classes.Length, classes.Distinct(StringComparer.Ordinal).Count());
        return classes;
    }
}
