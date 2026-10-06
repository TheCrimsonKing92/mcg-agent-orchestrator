using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceLaneReuseShadowDecision(string Lane, string PartitionId, string Decision, string Reason);
internal sealed record AcceptanceLaneReuseShadowClassification(
    IReadOnlyList<string> IgnoredPaths, IReadOnlyList<AcceptanceLaneReuseShadowDecision> Lanes);

internal static class AcceptanceLaneReuseShadowClassifier
{
    private static readonly (string Category, string[] Markers, string[] ResourceKeys)[] MarkerTable =
    [
        ("process-spawning", ["ProcessStartInfo", "Process.Start(", "pwsh", "powershell"],
            ["xunit:ProcessSpawning", "xunit:ProcessSpawningProcessLocal", "xunit:DotnetBuildSlots"]),
        ("built-binary", [".exe\"", ".dll\"", "AppContext.BaseDirectory"], []),
        ("source-text-guard", ["VerifiedRepositoryRoot", "FindRepositoryRoot", "CallerFilePath"], []),
        ("config-driven", ["acceptance-manifest.json", "\"config\"", "\"config/"], [])
    ];

    internal static (string[] LookupPaths, string[] IgnoredPaths) SelectLookupPaths(IReadOnlyList<string> paths)
    {
        var normalized = Normalize(paths).Where(IsCSharp).ToArray();
        return (normalized.Where(IsRelevant).ToArray(),
            normalized.Where(path => path.StartsWith("tests/", StringComparison.Ordinal) && !IsRelevant(path)).ToArray());
    }

    internal static string? PathReason(IReadOnlyList<string>? changedPaths, string? unavailableCause = null)
    {
        if (unavailableCause is not null) return $"shadow-unavailable:{unavailableCause}";
        if (changedPaths is null) return "shadow-unavailable:changed-files";
        if (changedPaths.Count == 0) return "shadow-unavailable:no-changed-files";
        var paths = Normalize(changedPaths);
        var outside = paths.FirstOrDefault(path => !path.StartsWith("src/", StringComparison.Ordinal) &&
                                                  !path.StartsWith("tests/", StringComparison.Ordinal));
        if (outside is not null) return $"change-outside-src-tests:{outside}";
        var nonCSharp = paths.FirstOrDefault(path => !IsCSharp(path));
        return nonCSharp is null ? null : $"non-csharp-change:{nonCSharp}";
    }

    internal static AcceptanceLaneReuseShadowClassification Classify(
        IReadOnlyList<string>? changedPaths, IReadOnlyList<AcceptanceManifestCheck> checks,
        IReadOnlyList<AcceptanceTestClassSource> inventory, ReverseDependencyTestImpactLookupResult lookup,
        string? unavailableCause = null, string? inventoryFailure = null)
    {
        var selection = SelectLookupPaths(changedPaths ?? []);
        var globalReason = PathReason(changedPaths, unavailableCause);
        if (globalReason is null && selection.LookupPaths.Length > 0 && !lookup.Resolved)
            globalReason = $"dependency-index-degraded:{lookup.DegradationKind}";
        if (globalReason is null && inventoryFailure is not null)
            globalReason = $"shadow-unavailable:class-inventory:{inventoryFailure}";
        var changed = Normalize(changedPaths ?? []).ToHashSet(StringComparer.Ordinal);
        var referenced = lookup.TestClassNames.Select(ShortName).ToHashSet(StringComparer.Ordinal);
        var decisions = new List<AcceptanceLaneReuseShadowDecision>();
        foreach (var check in checks)
        {
            if (!GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out var partitionId, out var filter)) continue;
            var reason = globalReason;
            if (reason is null)
            {
                var lane = new AcceptanceTestLane(check.Name, filter);
                AcceptanceTestClassSource[] members;
                try
                {
                    // Evaluate even an empty inventory so unsupported filters never imply reusable.
                    _ = AcceptanceLaneMembership.LanesIncluding([lane], string.Empty);
                    members = inventory.Where(item => AcceptanceLaneMembership.LanesIncluding([lane], item.FullName).Count > 0)
                        .OrderBy(item => item.FullName, StringComparer.Ordinal).ToArray();
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                {
                    decisions.Add(new(check.Name, partitionId, "must-run", "lane-membership-unresolved"));
                    continue;
                }
                foreach (var category in MarkerTable)
                {
                    var key = category.ResourceKeys.FirstOrDefault(key => check.ExclusiveResourceKeys.Contains(key, StringComparer.Ordinal));
                    if (key is not null) { reason = $"always-affected:{category.Category}:{key}"; break; }
                }
                if (reason is null)
                {
                    foreach (var member in members)
                    {
                        var category = MarkerTable.FirstOrDefault(category => category.Markers.Any(marker =>
                            member.SourceText.Contains(marker, StringComparison.Ordinal)));
                        if (category.Category is null) continue;
                        reason = $"always-affected:{category.Category}:{member.FullName}";
                        break;
                    }
                }
                var changedClass = members.FirstOrDefault(item => item.SourcePaths.Any(path =>
                    IsRelevantTest(path.Replace('\\', '/')) && changed.Contains(path.Replace('\\', '/'))));
                if (reason is null && changedClass is not null) reason = $"changed-test-class:{changedClass.FullName}";
                var consumer = members.FirstOrDefault(item => referenced.Contains(ShortName(item.FullName)));
                if (reason is null && consumer is not null) reason = $"references-changed-source:{consumer.FullName}";
            }
            reason ??= "unaffected";
            decisions.Add(new(check.Name, partitionId, reason == "unaffected" ? "would-reuse" : "must-run", reason));
        }
        return new(selection.IgnoredPaths, decisions);
    }

    private static string ShortName(string name) => name.Split('.', '+').Last();
    private static bool IsCSharp(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    private static bool IsRelevant(string path) => path.StartsWith("src/", StringComparison.Ordinal) || IsRelevantTest(path);
    private static bool IsRelevantTest(string path) =>
        path.StartsWith("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/", StringComparison.Ordinal) ||
        path.StartsWith("tests/Mcg.AgentOrchestrator.TestSupport/", StringComparison.Ordinal);
    private static string[] Normalize(IReadOnlyList<string> paths) => paths.Select(path => path.Replace('\\', '/'))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
