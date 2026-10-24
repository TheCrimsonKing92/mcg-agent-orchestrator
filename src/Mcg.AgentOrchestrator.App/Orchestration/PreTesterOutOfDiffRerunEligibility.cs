using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreTesterOutOfDiffRerunEligibility
{
    internal static IReadOnlyList<FindingEvidenceSelection>? Evaluate(PreTesterEvidenceEntry entry,
        IReadOnlyList<string> changedPaths, string worktree, string? sourceRoot)
    {
        if (entry is not { Outcome: "red", FailingTests.Count: > 0 }) return null;
        var executed = FindingEvidenceExecutedCoverage.ExecutedSelections(entry.Selections, entry.NotRun)
            .Select(ParseSelection).OfType<FindingEvidenceSelection>().ToArray();
        var changed = changedPaths.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var goalSources = new AcceptanceTestSourceResolver.Request(worktree);
        var rootSources = new AcceptanceTestSourceResolver.Request(sourceRoot);
        var selected = new List<FindingEvidenceSelection>();
        foreach (var identity in entry.FailingTests)
        {
            if (PreTesterAlwaysRunGuardTestClasses.IsListed(identity) || PreTesterTestSourceGuardClasses.IsCovered(identity) ||
                AcceptanceTestSourceResolver.ExtractClassName(identity) is not { } name) return null;
            var selections = executed.Where(selection => selection.TestClass.Split('.')[^1].Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (selections.Length == 0) return null;
            var paths = selections.SelectMany(selection => goalSources.ResolveSourcePaths(selection.TestProject, identity)
                .Concat(rootSources.ResolveSourcePaths(selection.TestProject, identity))).Select(Normalize).Distinct().ToArray();
            if (paths.Length == 0 || paths.Any(changed.Contains)) return null;
            selected.AddRange(selections);
        }
        return selected.Distinct().ToArray();
    }

    private static FindingEvidenceSelection? ParseSelection(string value)
    {
        var colon = value.LastIndexOf(':');
        return colon <= 0 || colon == value.Length - 1 ? null : new(value[..colon], value[(colon + 1)..]);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimStart('.', '/');
}
