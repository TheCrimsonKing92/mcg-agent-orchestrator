namespace Mcg.AgentOrchestrator.Core;

public sealed record ReverseDependencyTestImpactLookupResult(
    bool Resolved, IReadOnlyList<string> TestClassNames, string? DegradationKind, string? Reason);

// Read-only observation with no focused-selection limits; the index size bound still applies.
public static class ReverseDependencyTestImpactReaderLookup
{
    public static ReverseDependencyTestImpactLookupResult Find(
        string repositoryRoot, IReadOnlyList<string> changedSourcePaths)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in changedSourcePaths.Select(path => path.Replace('\\', '/'))
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var selection = ReverseDependencyTestImpactReader.Read(repositoryRoot, [path],
                maximumSelectedTestClasses: int.MaxValue, maximumFrontierSymbols: int.MaxValue);
            if (selection.Outcome != ReverseDependencySelectionOutcome.Resolved)
                return new(false, [], selection.DegradationKind?.ToString() ?? selection.Outcome.ToString(),
                    selection.Reason);
            names.UnionWith(selection.TestClassNames);
        }
        return new(true, names.Order(StringComparer.Ordinal).ToArray(), null, null);
    }
}
