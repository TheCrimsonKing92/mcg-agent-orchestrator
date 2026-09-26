using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public static partial class RepositoryChangeClassifier
{
    public static string? ComputeOwnerProtectedChangeFingerprint(
        IEnumerable<(string File, string? Trusted, string? Candidate)> files)
    {
        try
        {
            var entries = files.Select(file =>
            {
                using var trusted = string.IsNullOrWhiteSpace(file.Trusted) ? null : JsonDocument.Parse(file.Trusted);
                using var candidate = string.IsNullOrWhiteSpace(file.Candidate) ? null : JsonDocument.Parse(file.Candidate);
                var changes = new List<JsonChangeEntry>();
                DescribeEntries(trusted?.RootElement, candidate?.RootElement, "", changes);
                return (File: file.File.Replace('\\', '/'), Changes: changes);
            }).OrderBy(file => file.File, StringComparer.Ordinal).ToArray();
            if (entries.Length == 0) return null;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (var file in entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("file", file.File);
                    writer.WriteString("classification", file.File.Equals("config/acceptance-manifest.json", StringComparison.Ordinal)
                        ? "acceptance-manifest" : "owner-protected-policy");
                    writer.WriteStartArray("changes");
                    foreach (var change in file.Changes.OrderBy(change => change.Path, StringComparer.Ordinal)
                                 .ThenBy(change => change.Kind, StringComparer.Ordinal))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("path", change.Path);
                        writer.WriteString("kind", change.Kind);
                        writer.WriteString("before", change.Before);
                        writer.WriteString("after", change.After);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return "v1:" + Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record JsonChangeEntry(string Path, string Kind, string? Before, string? After);

    private static void DescribeEntries(JsonElement? before, JsonElement? after, string path, List<JsonChangeEntry> changes)
    {
        if (before is null || after is null)
        {
            if (before is null && after is null) return;
            AddEntriesForLeaves(after ?? before!.Value, path, before is null ? "added" : "removed", changes);
            return;
        }
        if (before.Value.ValueKind != after.Value.ValueKind)
        {
            changes.Add(new(path.Length == 0 ? "$" : path, "modified", Canonical(before), Canonical(after)));
            return;
        }
        if (before.Value.ValueKind == JsonValueKind.Object)
        {
            var left = before.Value.EnumerateObject().ToArray();
            var right = after.Value.EnumerateObject().ToArray();
            foreach (var name in left.Concat(right).Select(property => property.Name).Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                var child = path.Length == 0 ? name : $"{path}.{name}";
                if (left.Count(property => property.Name == name) > 1 || right.Count(property => property.Name == name) > 1)
                    changes.Add(new(child, "duplicate-key",
                        CanonicalDuplicates(left.Where(property => property.Name == name).Select(property => property.Value)),
                        CanonicalDuplicates(right.Where(property => property.Name == name).Select(property => property.Value))));
                var old = left.LastOrDefault(property => property.Name == name);
                var current = right.LastOrDefault(property => property.Name == name);
                DescribeEntries(old.Name is null ? null : old.Value, current.Name is null ? null : current.Value, child, changes);
            }
            return;
        }
        if (before.Value.ValueKind == JsonValueKind.Array)
        {
            var left = before.Value.EnumerateArray().ToArray();
            var right = after.Value.EnumerateArray().ToArray();
            for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
                DescribeEntries(index < left.Length ? left[index] : null, index < right.Length ? right[index] : null,
                    $"{path}[{index}]", changes);
            return;
        }
        if (!JsonElement.DeepEquals(before.Value, after.Value))
            changes.Add(new(path.Length == 0 ? "$" : path, "modified", Canonical(before), Canonical(after)));
    }

    private static void AddEntriesForLeaves(JsonElement element, string path, string kind, List<JsonChangeEntry> changes)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            if (properties.Length == 0) changes.Add(new(path.Length == 0 ? "$" : path, kind,
                kind == "removed" ? Canonical(element) : null, kind == "added" ? Canonical(element) : null));
            foreach (var property in properties)
                AddEntriesForLeaves(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", kind, changes);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray().ToArray();
            if (items.Length == 0) changes.Add(new(path.Length == 0 ? "$" : path, kind,
                kind == "removed" ? Canonical(element) : null, kind == "added" ? Canonical(element) : null));
            for (var index = 0; index < items.Length; index++)
                AddEntriesForLeaves(items[index], $"{path}[{index}]", kind, changes);
        }
        else
            changes.Add(new(path.Length == 0 ? "$" : path, kind,
                kind == "removed" ? Canonical(element) : null, kind == "added" ? Canonical(element) : null));
    }

    private static string? Canonical(JsonElement? element)
    {
        if (element is null) return null;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, element.Value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CanonicalDuplicates(IEnumerable<JsonElement> elements)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var element in elements) WriteCanonical(writer, element);
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var child in element.EnumerateArray()) WriteCanonical(writer, child);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}
