using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class TestCollectionGuardRules
{
    public const string LaneMembershipRule = "lane-membership";
    public const string IsolatedCollectionRule = "isolated-collection";
    internal const string ExemptionsRelativePath = "config/isolated-collection-exemptions.json";
    internal const string DotnetBuildSlotsLaneName = "Dotnet build slots";
    internal const string DotnetBuildSlotsCollection = "DotnetBuildSlots";

    internal static IReadOnlyList<TestCollectionGuardViolation> FindLaneMembershipViolations(
        IReadOnlyList<AcceptanceTestLane> lanes, IReadOnlyList<AcceptanceTestClassDescriptor> descriptors)
    {
        var baseline = lanes.Select(lane => lane with { OwnedCollections = [] }).ToArray();
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
        var violations = new List<TestCollectionGuardViolation>();
        foreach (var descriptor in descriptors)
        {
            var baselineNames = LaneNames(baseline, descriptor.FullName);
            var resolvedNames = LaneNames(resolved, descriptor.FullName);
            if (!baselineNames.SequenceEqual(resolvedNames, StringComparer.Ordinal))
            {
                violations.Add(new TestCollectionGuardViolation(LaneMembershipRule,
                    descriptor.FullName, descriptor.Collection, baselineNames, resolvedNames,
                    "Rename the class so its name contains one of its owning lane's FullyQualifiedName~ " +
                    "substrings in config/acceptance-manifest.json, or remove the owned collection; do not edit the manifest."));
            }
        }
        return violations;
    }

    internal static IReadOnlyList<TestCollectionGuardViolation> FindUnisolatedLaneClasses(
        IReadOnlyList<AcceptanceTestLane> lanes, IReadOnlyList<AcceptanceTestClassDescriptor> descriptors,
        string laneName, string isolatedCollection, IEnumerable<string> exemptClassNames)
    {
        if (!lanes.Any(lane => lane.Name == laneName)) return [];
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
        var lane = resolved.Where(item => item.Name == laneName).ToArray();
        var members = descriptors.Where(item => AcceptanceLaneMembership.LanesIncluding(lane, item.FullName).Count > 0);
        var baseline = lanes.Select(item => item with { OwnedCollections = [] }).ToArray();
        return FindUnisolatedClasses(members, item => item.Collection == isolatedCollection, exemptClassNames)
            .Select(item => new TestCollectionGuardViolation(IsolatedCollectionRule, item.FullName, item.Collection,
                LaneNames(baseline, item.FullName), LaneNames(resolved, item.FullName),
                $"Add [Xunit.Collection(\"{isolatedCollection}\")] or a named exemption with a reason in {ExemptionsRelativePath}."))
            .ToArray();
    }

    // Reflected guards can supply their existing set-based isolation predicate after resolving lane members.
    internal static IReadOnlyList<AcceptanceTestClassDescriptor> FindUnisolatedClasses(
        IEnumerable<AcceptanceTestClassDescriptor> members, Func<AcceptanceTestClassDescriptor, bool> isIsolated,
        IEnumerable<string> exemptClassNames)
    {
        var exemptions = exemptClassNames.ToHashSet(StringComparer.Ordinal);
        return members.Where(item => !isIsolated(item) && !exemptions.Contains(item.FullName)).ToArray();
    }

    internal static IReadOnlyDictionary<string, string> LoadIsolatedCollectionExemptions(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, ExemptionsRelativePath);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{path}: exemptions must be an array of class/reason objects.");
            var exemptions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("class", out var classValue) || classValue.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("reason", out var reasonValue) || reasonValue.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(classValue.GetString()) || string.IsNullOrWhiteSpace(reasonValue.GetString()))
                    throw new InvalidDataException($"{path}: each exemption must have a nonempty class and reason.");
                if (!exemptions.TryAdd(classValue.GetString()!, reasonValue.GetString()!))
                    throw new InvalidDataException($"{path}: duplicate exemption '{classValue.GetString()}'.");
            }
            return exemptions;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                $"{path}: cannot load isolated-collection exemptions: {exception.Message} " +
                "Rebase onto main to pick up the file if it is missing, or repair its class/reason entries.", exception);
        }
    }

    private static string[] LaneNames(IReadOnlyList<AcceptanceTestLane> lanes, string fullName) =>
        AcceptanceLaneMembership.LanesIncluding(lanes, fullName).Select(lane => lane.Name)
            .Order(StringComparer.Ordinal).ToArray();
}
