using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceTestInventory(
    IReadOnlyList<AcceptanceTestClassDescriptor> Classes,
    IReadOnlySet<string> DisabledCollections,
    IReadOnlySet<string> ProcessLocalCollectionsAtMain);

internal static class AcceptanceManifestPartitionEquivalence
{
    private static readonly HashSet<string> MutableLaneFields =
        ["name", "filter", "ownedCollections", "exclusiveResourceKeys", "estimatedSerialSeconds"];

    internal static bool IsEquivalent(string trustedJson, string candidateJson, Func<AcceptanceTestInventory> inventoryFactory)
    {
        try
        {
            using var trustedDocument = JsonDocument.Parse(trustedJson);
            using var candidateDocument = JsonDocument.Parse(candidateJson);
            if (HasDuplicateKeys(trustedDocument.RootElement) || HasDuplicateKeys(candidateDocument.RootElement))
                return false;
            var trusted = JsonNode.Parse(trustedJson) as JsonObject;
            var candidate = JsonNode.Parse(candidateJson) as JsonObject;
            if (trusted is null || candidate is null || !SameFixedDimensions(trusted, candidate)) return false;

            var mainSettings = AcceptanceGateEngineSettings.Parse(trustedJson);
            var candidateSettings = AcceptanceGateEngineSettings.Parse(candidateJson);
            if (!CandidatePartitionsAreClosed(candidate, candidateSettings.InfrastructureTestLanes)) return false;

            var inventory = inventoryFactory();
            if (inventory.Classes.Count == 0 || inventory.Classes.Any(item => string.IsNullOrWhiteSpace(item.FullName)) ||
                inventory.Classes.Select(item => item.FullName).Distinct(StringComparer.Ordinal).Count() != inventory.Classes.Count)
                return false;
            var mainLanes = AcceptanceLaneMembership.ResolveOwnedCollections(mainSettings.InfrastructureTestLanes, inventory.Classes);
            var candidateLanes = AcceptanceLaneMembership.ResolveOwnedCollections(candidateSettings.InfrastructureTestLanes, inventory.Classes);
            foreach (var item in inventory.Classes.OrderBy(item => item.FullName, StringComparer.Ordinal))
            {
                var mainCount = AcceptanceLaneMembership.LanesIncluding(mainLanes, item.FullName).Count;
                var candidateCount = AcceptanceLaneMembership.LanesIncluding(candidateLanes, item.FullName).Count;
                if ((mainCount > 0) != (candidateCount > 0) || candidateCount > 1) return false;
            }

            foreach (var collection in inventory.DisabledCollections.Order(StringComparer.Ordinal))
            {
                if (inventory.ProcessLocalCollectionsAtMain.Contains(collection)) continue;
                var lanes = inventory.Classes.Where(item => item.Collection == collection)
                    .SelectMany(item => AcceptanceLaneMembership.LanesIncluding(candidateLanes, item.FullName))
                    .DistinctBy(lane => lane.Name).ToArray();
                if (lanes.Length < 2) continue;
                var common = lanes[0].ExclusiveResourceKeys.Select(key => key.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var lane in lanes.Skip(1))
                    common.IntersectWith(lane.ExclusiveResourceKeys.Select(key => key.Trim()));
                if (common.Count == 0) return false;
            }
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or
                                      ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            return properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                   properties.Any(property => HasDuplicateKeys(property.Value));
        }
        return element.ValueKind == JsonValueKind.Array &&
               element.EnumerateArray().Any(HasDuplicateKeys);
    }

    private static bool SameFixedDimensions(JsonObject trusted, JsonObject candidate)
    {
        var left = (JsonObject)trusted.DeepClone();
        var right = (JsonObject)candidate.DeepClone();
        if (!Normalize(left) || !Normalize(right)) return false;
        return RepositoryChangeClassifier.DescribeJsonChanges(left.ToJsonString(), right.ToJsonString()).Count == 0;
    }

    private static bool Normalize(JsonObject root)
    {
        if (root["engine"] is not JsonObject engine || engine["infrastructureTestLanes"] is not JsonArray lanes ||
            engine["localTestPartitions"] is not JsonArray partitions) return false;
        var retained = new JsonArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in lanes)
        {
            if (item is not JsonObject lane || lane["name"] is not JsonValue nameValue ||
                !nameValue.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name) || !names.Add(name))
                return false;
            foreach (var field in MutableLaneFields) lane.Remove(field);
            // A newly added lane may contain only the five mutable dimensions.
            if (lane.Count > 0) retained.Add(new JsonObject { ["name"] = name, ["extra"] = lane.DeepClone() });
        }
        // Compare named lane residuals independently below: removed or added lanes with extra fields fail.
        var residuals = retained.Select(item => item!.ToJsonString()).Order(StringComparer.Ordinal).ToArray();
        engine["infrastructureTestLanes"] = new JsonArray(residuals.Select(value => JsonNode.Parse(value)).ToArray());
        foreach (var item in partitions)
        {
            if (item is not JsonObject partition || partition["laneNames"] is not JsonArray) return false;
            partition.Remove("laneNames");
        }
        return true;
    }

    private static bool CandidatePartitionsAreClosed(JsonObject candidate, IReadOnlyList<AcceptanceTestLane> lanes)
    {
        if (candidate["engine"] is not JsonObject engine || engine["localTestPartitions"] is not JsonArray partitions)
            return false;
        var declared = lanes.Select(lane => lane.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in partitions)
        {
            if (item is not JsonObject partition || partition["laneNames"] is not JsonArray names) return false;
            foreach (var node in names)
            {
                if (node is not JsonValue value || !value.TryGetValue<string>(out var name) ||
                    !declared.Contains(name)) return false;
                referenced.Add(name);
            }
        }
        return declared.SetEquals(referenced);
    }
}
