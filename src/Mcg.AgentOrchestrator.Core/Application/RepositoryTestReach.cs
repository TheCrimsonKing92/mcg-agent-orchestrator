namespace Mcg.AgentOrchestrator.Core;

/// <summary>Bounded test membership evidence, independent of command-line filter limits.</summary>
public static class RepositoryTestReach
{
    private const int MaximumReachableTestClasses = 4_000;

    public static IReadOnlySet<string>? TryReadReachableTestClasses(
        string repositoryRoot, IReadOnlyList<string> changedSourcePaths)
    {
        try
        {
            // The planner deliberately skips deleted paths. Attribution cannot treat a
            // partially readable member as a complete reach set and exclude its candidacy.
            if (changedSourcePaths.Any(path => !File.Exists(Path.Combine(repositoryRoot, path.Trim())))) return null;
            var selection = ReverseDependencyTestImpactReader.Read(repositoryRoot, changedSourcePaths,
                maximumSelectedTestClasses: MaximumReachableTestClasses, propagateShippingSourceReach: true);
            return selection.Outcome == ReverseDependencySelectionOutcome.Resolved
                ? selection.TestClassNames.ToHashSet(StringComparer.Ordinal)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
