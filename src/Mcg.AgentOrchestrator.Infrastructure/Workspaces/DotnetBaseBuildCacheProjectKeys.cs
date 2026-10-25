using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A key belongs to an evaluated project and includes the identities of all its dependencies.
internal static class DotnetBaseBuildCacheProjectKeys
{
    internal sealed record Inputs(IReadOnlyList<string> Buckets, IReadOnlyList<string> References);

    internal static string Compute(string root, IReadOnlyList<string> invocation, IReadOnlyList<string> environment,
        IReadOnlyDictionary<string, Inputs> projects)
    {
        var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects.Keys.Order(StringComparer.Ordinal)) Resolve(project);
        return string.Join(";", keys.Select(pair => $"{pair.Key}={pair.Value}"));

        string? Resolve(string project)
        {
            if (keys.TryGetValue(project, out var key)) return key;
            if (!visited.Add(project) || !ValidId(project) || !projects.TryGetValue(project, out var inputs)) return null;
            var references = new List<string[]>();
            foreach (var reference in inputs.References.Order(StringComparer.Ordinal))
            {
                var dependencyKey = Resolve(reference);
                if (dependencyKey is null) return null;
                references.Add([reference, dependencyKey]);
            }
            string Normalize(string value) => project.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
                ? NormalizeRoot(value, root) : value;
            object[] parts = ["project-key-v1", project, invocation.Select(Normalize).ToArray(),
                environment.Select(Normalize).ToArray(), inputs.Buckets.Select(Normalize).ToArray(), references];
            return keys[project] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts)))).ToLowerInvariant();
        }
    }

    internal static bool TryGet(string serialized, string project, out string key)
    {
        var prefix = project.Replace('\\', '/') + "=";
        var entry = serialized.Split(';').FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        key = entry is null ? "" : entry[prefix.Length..];
        return key.Length == 64 && key.All(char.IsAsciiHexDigit);
    }

    internal static string NormalizeRoot(string value, string root)
    {
        value = ReplaceRootForms(root);
        // MSBuild's Directory item metadata is absolute except for the volume/root prefix.
        // It names the same worktree as FullPath and must not retain that worktree's spelling.
        var volume = Path.GetPathRoot(root);
        return volume is not null && root.Length > volume.Length ? ReplaceRootForms(root[volume.Length..]) : value;

        string ReplaceRootForms(string path) => value
            .Replace(path.Replace("\\", "\\\\\\\\", StringComparison.Ordinal), "<root>", StringComparison.OrdinalIgnoreCase)
            .Replace(path.Replace("\\", "\\\\", StringComparison.Ordinal), "<root>", StringComparison.OrdinalIgnoreCase)
            .Replace(path.Replace('\\', '/'), "<root>", StringComparison.OrdinalIgnoreCase)
            .Replace(path, "<root>", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ValidId(string project) => !Path.IsPathRooted(project) &&
        !project.StartsWith("../", StringComparison.Ordinal) && project.IndexOfAny(['=', ';']) < 0;

    internal static string[] BuildOrder(string graph, IEnumerable<string> roots)
    {
        Dictionary<string, string[]> references = string.IsNullOrEmpty(graph) ? [] : JsonSerializer.Deserialize<Dictionary<string, string[]>>(graph)!;
        var order = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots) Visit(root);
        return [.. order];

        void Visit(string project)
        {
            if (done.Contains(project)) return;
            if (!ValidId(project) || !visiting.Add(project))
                throw new InvalidOperationException("The evaluated build graph contains an unsafe reference or cycle.");
            if (references.TryGetValue(project, out var dependencies))
                foreach (var dependency in dependencies) Visit(dependency);
            visiting.Remove(project);
            done.Add(project);
            order.Add(project);
        }
    }
}
