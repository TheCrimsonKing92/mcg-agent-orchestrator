namespace Mcg.AgentOrchestrator.Infrastructure;

// Lane membership includes only sources under directories explicitly declared by the manifest.
internal static class LaneTestSourceRootResolver
{
    internal static string Normalize(string entry)
    {
        var normalized = entry.Replace('\\', '/');
        return normalized.EndsWith('/') ? normalized[..^1] : normalized;
    }

    internal static void ValidateEntry(string? entry)
    {
        var normalized = entry is null ? "" : Normalize(entry);
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) ||
            normalized.StartsWith('/') || normalized.Contains(':') ||
            normalized.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"Acceptance manifest engine laneTestSourceRoots entry '{entry ?? "null"}' must be a repository-relative directory without empty, '.' or '..' segments.");
        }
    }

    internal static IReadOnlyList<string> Resolve(string worktreePath) =>
        AcceptanceGateEngineSettings.Load(worktreePath).LaneTestSourceRoots
            .Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();

    internal static string[] SourceFiles(string worktreePath, IReadOnlyList<string> roots) =>
        roots.Select(root => Path.Combine(worktreePath, root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static IEnumerable<string> GitPathspecs(IReadOnlyList<string> roots) =>
        roots.Select(root => ":(literal)" + root);
}
