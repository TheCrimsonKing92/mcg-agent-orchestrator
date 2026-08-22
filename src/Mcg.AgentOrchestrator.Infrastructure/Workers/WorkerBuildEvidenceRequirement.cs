using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerBuildEvidenceRequirement
{
    private static readonly HashSet<string> CompiledExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cs", ".fs", ".vb" };

    internal static IReadOnlyList<string> FindRequiredProjects(
        string worktreeRoot,
        AgentRole role,
        IEnumerable<string> changedPaths,
        IEnumerable<string> dirtyPaths)
    {
        if (role is not (AgentRole.Developer or AgentRole.Tester))
        {
            return [];
        }

        var root = Path.GetFullPath(worktreeRoot);
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in changedPaths.Concat(dirtyPaths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalizedPath = path.Replace('\\', '/').TrimStart('/');
            if (GitCli.IsOrchestratorInternalArtifactPath(normalizedPath) ||
                !CompiledExtensions.Contains(Path.GetExtension(normalizedPath)))
            {
                continue;
            }

            var absolutePath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));
            var relativePath = Path.GetRelativePath(root, absolutePath);
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Equals("..", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(absolutePath);
            while (!string.IsNullOrWhiteSpace(directory))
            {
                var project = Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                        .OrderBy(candidate => candidate, StringComparer.Ordinal)
                        .FirstOrDefault()
                    : null;
                if (project is not null)
                {
                    projects.Add(Path.GetRelativePath(root, project).Replace('\\', '/'));
                    break;
                }

                if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        return projects.OrderBy(project => project, StringComparer.Ordinal).ToArray();
    }
}
