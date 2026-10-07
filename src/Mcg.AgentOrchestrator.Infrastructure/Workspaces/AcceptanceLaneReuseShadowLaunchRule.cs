using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceLaneReuseShadowLaunchProvenance(
    IReadOnlyList<AcceptanceLaneReuseShadowLaunchSite> LaunchTargets, string DependencyClosureSource,
    IReadOnlyList<AcceptanceLaneReuseShadowLaunchSite> UnresolvedContracts, string? InputAttestation);
internal sealed record AcceptanceLaneReuseShadowLaunchDecision(string Lane, string PartitionId,
    string Decision, string Reason, AcceptanceLaneReuseShadowLaunchProvenance Provenance);

internal static class AcceptanceLaneReuseShadowLaunchRule
{
    internal static IReadOnlyList<AcceptanceLaneReuseShadowLaunchDecision> Classify(
        IReadOnlyList<string>? changedPaths, IReadOnlyList<AcceptanceManifestCheck> checks,
        IReadOnlyList<AcceptanceTestClassSource> inventory, ReverseDependencyTestImpactLookupResult lookup,
        IReadOnlyList<AcceptanceLaneReuseShadowLaunchContract> contracts,
        string? unavailableCause = null, string? inventoryFailure = null)
    {
        var paths = (changedPaths ?? []).Select(path => path.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var globalReason = paths.FirstOrDefault(RequiresAllLanes) is { } allPath
            ? $"requires-all-lanes:{allPath}" : AcceptanceLaneReuseShadowClassifier.PathReason(changedPaths, unavailableCause);
        if (globalReason is null && AcceptanceLaneReuseShadowClassifier.SelectLookupPaths(paths).LookupPaths.Length > 0 && !lookup.Resolved)
            globalReason = $"dependency-index-degraded:{lookup.DegradationKind}";
        if (globalReason is null && inventoryFailure is not null)
            globalReason = $"shadow-unavailable:class-inventory:{inventoryFailure}";
        var changed = paths.ToHashSet(StringComparer.Ordinal);
        var consumers = lookup.TestClassNames.Select(ShortName).ToHashSet(StringComparer.Ordinal);
        var byClass = contracts.ToDictionary(contract => contract.FullName, StringComparer.Ordinal);
        var decisions = new List<AcceptanceLaneReuseShadowLaunchDecision>();
        foreach (var check in checks)
        {
            if (!GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out var partitionId, out var filter)) continue;
            var reason = globalReason;
            string? attestation = null;
            AcceptanceTestClassSource[] members = [];
            var launchTargets = new List<AcceptanceLaneReuseShadowLaunchSite>();
            var unresolved = new List<AcceptanceLaneReuseShadowLaunchSite>();
            if (reason is null)
            {
                var lane = new AcceptanceTestLane(check.Name, filter);
                try
                {
                    _ = AcceptanceLaneMembership.LanesIncluding([lane], string.Empty);
                    members = inventory.Where(item => AcceptanceLaneMembership.LanesIncluding([lane], item.FullName).Count > 0)
                        .OrderBy(item => item.FullName, StringComparer.Ordinal).ToArray();
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                { reason = "lane-membership-unresolved"; }
                foreach (var member in members)
                {
                    if (!byClass.TryGetValue(member.FullName, out var contract))
                        throw new InvalidDataException($"Missing launch contract '{member.FullName}'.");
                    launchTargets.AddRange(contract.LaunchSites);
                    unresolved.AddRange(contract.LaunchSites.Where(site => site.Kind == "unresolved"));
                    unresolved.AddRange(contract.RepositoryReads);
                }
                var seeded = members.FirstOrDefault(member => member.SourcePaths.Any(path =>
                    path.Replace('\\', '/') == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs"));
                if (reason is null && seeded is not null)
                {
                    reason = $"seeded-dispatch-input-attestation-missing:{seeded.FullName}";
                    attestation = "seeded-dispatch-unattested";
                }
                var changedClass = members.FirstOrDefault(member => member.SourcePaths.Any(path => changed.Contains(path.Replace('\\', '/'))));
                if (reason is null && changedClass is not null) reason = $"changed-test-class:{changedClass.FullName}";
                var consumer = members.FirstOrDefault(member => consumers.Contains(ShortName(member.FullName)));
                if (reason is null && consumer is not null) reason = $"references-changed-source:{consumer.FullName}";
                if (reason is null)
                    foreach (var member in members)
                    {
                        var contract = byClass[member.FullName];
                        if (contract.LaunchSites.Any(site => site.Kind == "unresolved")) reason = $"launch-target-unresolved:{member.FullName}";
                        else if (contract.LaunchSites.Any(site => site.Kind == "repo-binary")) reason = $"launch-target-repo-binary:{member.FullName}";
                        else if (contract.RepositoryReads.Count > 0) reason = $"repository-read-unresolved:{member.FullName}";
                        if (reason is not null) break;
                    }
            }
            reason ??= "unaffected";
            decisions.Add(new(check.Name, partitionId, reason == "unaffected" ? "would-reuse" : "must-run", reason,
                new(launchTargets.ToArray(), "reverse-dependency-index", unresolved.ToArray(), attestation)));
        }
        return decisions;
    }

    private static string ShortName(string name) => name.Split('.', '+').Last();
    private static bool RequiresAllLanes(string path) =>
        path.StartsWith("scripts/", StringComparison.Ordinal) || path.StartsWith("config/", StringComparison.Ordinal) ||
        path.StartsWith("tests/Mcg.AgentOrchestrator.TestSupport/", StringComparison.Ordinal) ||
        path.StartsWith("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/", StringComparison.Ordinal) ||
        Path.GetFileName(path) == "global.json" || Path.GetFileName(path).StartsWith("Directory.Build.", StringComparison.Ordinal) ||
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
}
