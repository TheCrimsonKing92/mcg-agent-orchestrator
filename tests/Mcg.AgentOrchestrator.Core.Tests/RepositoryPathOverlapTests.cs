using Mcg.AgentOrchestrator.Core;

public sealed class RepositoryPathOverlapTests
{
    public static IEnumerable<object[]> PathPairs()
    {
        yield return ["src/Feature/File.cs", "src/Feature/File.cs", PathOverlapKind.Exact];
        yield return [@"src\Feature\File.cs", "SRC/FEATURE/FILE.CS/", PathOverlapKind.Exact];
        yield return [" /src/Feature/File.cs/ ", "src/Feature/File.cs", PathOverlapKind.Exact];
        yield return ["src/Feature", "src/Feature/File.cs", PathOverlapKind.DirectoryPrefix];
        yield return ["src/Feature/File.cs", "src/Feature", PathOverlapKind.DirectoryPrefix];
        yield return ["src/App", "src/AppX", PathOverlapKind.None];
        yield return ["src/Feature/A.cs", "src/Feature/B.cs", PathOverlapKind.None];
    }

    [Xunit.Theory(DisplayName = "RepositoryPathOverlap_classifies_normalized_path_pairs")]
    [Xunit.MemberData(nameof(PathPairs))]
    public void ClassifiesNormalizedPathPairs(string left, string right, PathOverlapKind expected)
    {
        Assert.Equal(expected, RepositoryPathOverlap.Classify(left, right));
        Assert.Equal(expected != PathOverlapKind.None, RepositoryPathOverlap.Overlaps(left, right));
    }

    [Xunit.Theory(DisplayName = "RepositoryPathOverlap_matches_parallel_scheduler_path_conflicts")]
    [Xunit.MemberData(nameof(PathPairs))]
    public void MatchesParallelSchedulerPathConflicts(string left, string right, PathOverlapKind expected)
    {
        var plan = ParallelExecutionPlanner.Build([
            new ParallelExecutionIntent("left", "goal-left", [left]),
            new ParallelExecutionIntent("right", "goal-right", [right])
        ]);

        var schedulerDetectedOverlap = plan.Batches.Count == 2;
        Assert.Equal(expected != PathOverlapKind.None, schedulerDetectedOverlap);
    }
}
