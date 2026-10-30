namespace Mcg.AgentOrchestrator.Core;

/// <summary>The bounded dependency evidence used when removing a train member.</summary>
public sealed record RepositoryRecheckTestSelection(bool Resolved, IReadOnlyList<string> TestClasses, string? Reason = null)
{
    public static RepositoryRecheckTestSelection Read(string repositoryRoot, IReadOnlyList<string> changedSourcePaths)
    {
        // Missing paths include deletions and rename sources; silently skipping them
        // would turn an incomplete dependency closure into authorization to land.
        if (changedSourcePaths.Any(path => !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(repositoryRoot, path))))
            return new(false, [], "non-source-or-missing-path");
        var selection = ReverseDependencyTestImpactReader.Read(repositoryRoot, changedSourcePaths);
        return new(selection.Outcome == ReverseDependencySelectionOutcome.Resolved,
            selection.TestClassNames, selection.Reason);
    }
}
