using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public static partial class RepositoryChangeClassifier
{
    public static bool IsOwnerProtectedPolicyPath(string path) =>
        string.Equals(Path.GetFileName(path.Replace('\\', '/')), "conductor-policy.json", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> DescribeJsonChanges(string? trustedJson, string? candidateJson)
    {
        using var trusted = string.IsNullOrWhiteSpace(trustedJson) ? null : JsonDocument.Parse(trustedJson);
        using var candidate = string.IsNullOrWhiteSpace(candidateJson) ? null : JsonDocument.Parse(candidateJson);
        var changes = new List<string>();
        Compare(trusted?.RootElement, candidate?.RootElement, string.Empty, changes);
        if (changes.Count <= 20)
            return changes;

        var omitted = changes.Count - 20;
        return [.. changes.Take(20), $"(+{omitted} more)"];
    }

    private static void Compare(JsonElement? trusted, JsonElement? candidate, string path, List<string> changes)
    {
        if (trusted is null || candidate is null)
        {
            if (trusted is null && candidate is null)
                return;
            AddLeaves(candidate ?? trusted!.Value, path, changes);
            return;
        }

        if (trusted.Value.ValueKind != candidate.Value.ValueKind)
        {
            changes.Add(path.Length == 0 ? "$" : path);
            return;
        }

        if (trusted.Value.ValueKind == JsonValueKind.Object)
        {
            var left = trusted.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            var right = candidate.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            foreach (var name in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var child = path.Length == 0 ? name : $"{path}.{name}";
                Compare(left.TryGetValue(name, out var l) ? l : null,
                    right.TryGetValue(name, out var r) ? r : null, child, changes);
            }
            return;
        }

        if (trusted.Value.ValueKind == JsonValueKind.Array)
        {
            var left = trusted.Value.EnumerateArray().ToArray();
            var right = candidate.Value.EnumerateArray().ToArray();
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
                Compare(i < left.Length ? left[i] : null, i < right.Length ? right[i] : null,
                    $"{path}[{i}]", changes);
            return;
        }

        if (!JsonElement.DeepEquals(trusted.Value, candidate.Value))
            changes.Add(path.Length == 0 ? "$" : path);
    }

    private static void AddLeaves(JsonElement element, string path, List<string> changes)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            if (properties.Length == 0)
                changes.Add(path.Length == 0 ? "$" : path);
            foreach (var property in properties)
                AddLeaves(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", changes);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray().ToArray();
            if (items.Length == 0)
                changes.Add(path.Length == 0 ? "$" : path);
            for (var i = 0; i < items.Length; i++)
                AddLeaves(items[i], $"{path}[{i}]", changes);
        }
        else
            changes.Add(path.Length == 0 ? "$" : path);
    }
}
