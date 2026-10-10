using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerAcceptanceManifestLine
{
    public static string Render(string worktreePath, string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            return "missing";

        var fullPath = Path.GetFullPath(manifestPath);
        var relativePath = Path.GetRelativePath(Path.GetFullPath(worktreePath), fullPath);
        var escapesWorktree = relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        var displayPath = !Path.IsPathRooted(relativePath) && relativePath != "." && !escapesWorktree
            ? relativePath.Replace('\\', '/')
            : fullPath;

        string checks;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            checks = ReadCheckNames(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            checks = "unreadable";
        }

        return $"present: {displayPath} checks: {checks}";
    }

    private static string ReadCheckNames(JsonElement root)
    {
        if (!TryGetProperty(root, "checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            return "none";

        var names = new List<string>();
        foreach (var check in checks.EnumerateArray())
        {
            var name = check.ValueKind == JsonValueKind.String
                ? check.GetString()
                : TryGetProperty(check, "name", out var property) && property.ValueKind == JsonValueKind.String
                    ? property.GetString()
                    : null;
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
