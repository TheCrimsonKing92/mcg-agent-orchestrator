using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorStewardLaneSubstringResolver
{
    string? RequiredSubstring(string worktree, string collection);
}

internal sealed class ManifestConductorStewardLaneSubstringResolver : IConductorStewardLaneSubstringResolver
{
    public string? RequiredSubstring(string worktree, string collection)
    {
        var lanes = AcceptanceGateEngineSettings.Load(worktree).InfrastructureTestLanes;
        var siblings = AcceptanceTestClassSourceScanner.Scan(worktree)
            .Where(item => item.Collection == collection).Select(item => item.FullName).ToArray();
        var lane = lanes.SingleOrDefault(item => item.OwnedCollections.Contains(collection, StringComparer.Ordinal))
            ?? lanes.FirstOrDefault(item => item.ExclusiveResourceKeys.Contains($"xunit:{collection}", StringComparer.Ordinal))
            ?? siblings.SelectMany(name => AcceptanceLaneMembership.LanesIncluding(lanes, name))
                .Where(item => !item.Name.Equals("Remainder", StringComparison.OrdinalIgnoreCase))
                .GroupBy(item => item.Name, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.First()).FirstOrDefault();
        if (lane is null) return null;
        var terms = lane.Filter.Split('|', '&')
            .Where(term => term.StartsWith("FullyQualifiedName~", StringComparison.Ordinal))
            .Select(term => term["FullyQualifiedName~".Length..]).ToArray();
        if (terms.Length == 0) return null;
        return terms.FirstOrDefault(term => term.Contains(collection, StringComparison.OrdinalIgnoreCase))
            ?? terms.OrderByDescending(term => siblings.Count(name =>
                    name.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(term => Array.IndexOf(terms, term)).First();
    }
}
