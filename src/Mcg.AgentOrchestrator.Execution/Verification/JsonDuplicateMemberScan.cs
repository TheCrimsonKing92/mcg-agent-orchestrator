using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class JsonDuplicateMemberScan
{
    internal static string? Find(JsonElement value, string path, IReadOnlyCollection<string> declared)
    {
        if (Direct(value, path) is { } duplicate) return duplicate;
        // Declared values get their own pre-pass when schema validation reaches them.
        foreach (var property in value.EnumerateObject())
            if (!declared.Contains(property.Name, StringComparer.Ordinal) &&
                Walk(property.Value, Member(path, property.Name)) is { } nested)
                return nested;
        return null;
    }

    private static string? Direct(JsonElement value, string path)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) return Member(path, property.Name);
        return null;
    }

    private static string? Walk(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (Direct(value, path) is { } duplicate) return duplicate;
            foreach (var property in value.EnumerateObject())
                if (Walk(property.Value, Member(path, property.Name)) is { } nested) return nested;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                if (Walk(item, path + "/" + (index++).ToString(CultureInfo.InvariantCulture)) is { } nested)
                    return nested;
        }
        return null;
    }

    private static string Member(string path, string name) =>
        path + "/" + name.Replace("~", "~0").Replace("/", "~1");
}
