using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplayChangedTestClassSelection
{
    internal static (IReadOnlyList<string> Classes, bool Gained, string? AbandonReason) Read(
        string checkout, IReadOnlyList<string> changedFiles, IReadOnlyList<string> reverseDependentClasses)
    {
        // Deleted files declare no classes at the attempt's revision.
        var present = changedFiles.Where(path => File.Exists(Path.Combine(checkout, path)));
        var changed = RepositoryTestImpactPlanner.ReadChangedInfrastructureTestClasses(present, checkout);
        var classes = new SortedSet<string>(reverseDependentClasses, StringComparer.Ordinal);
        var previousCount = classes.Count;
        classes.UnionWith(changed.TestClasses);
        return (classes.ToArray(), classes.Count > previousCount, changed.AbandonReason);
    }
}
