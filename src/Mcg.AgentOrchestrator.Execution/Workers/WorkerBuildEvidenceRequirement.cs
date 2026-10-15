using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerBuildEvidenceRequirement
{
    internal sealed record Resolution(IReadOnlyList<string> Projects, IReadOnlyList<string> Errors);
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

            var project = OwningProjectResolver.FindOwningProject(root, absolutePath);
            if (project is not null)
            {
                projects.Add(Path.GetRelativePath(root, project).Replace('\\', '/'));
            }
        }

        return projects.OrderBy(project => project, StringComparer.Ordinal).ToArray();
    }

    // Strict producer/admission discovery shares the existing ownership taxonomy. A source deliberately
    // excluded by a project is different from a source whose ownership cannot be established.
    internal static Resolution ResolveRequiredProjects(string worktreeRoot, IEnumerable<string> paths)
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (GitCli.IsOrchestratorInternalArtifactPath(path.Replace('\\', '/')) ||
                !CompiledExtensions.Contains(Path.GetExtension(path))) continue;
            try
            {
                var absolute = WorkerBuildProjectScope.ValidatePath(worktreeRoot, path, mustExist: false);
                var project = OwningProjectResolver.FindOwningProject(worktreeRoot, absolute);
                if (project is not null)
                {
                    WorkerBuildProjectScope.ValidatePath(worktreeRoot, project, mustExist: true);
                    projects.Add(Path.GetRelativePath(worktreeRoot, project).Replace('\\', '/'));
                }
                else if (!OwningProjectResolver.IsExplicitlyExcluded(worktreeRoot, absolute))
                    errors.Add($"unresolved compiled source: {path}");
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                errors.Add($"{path}: {exception.Message}");
            }
        }
        return new(projects.Order(StringComparer.Ordinal).ToArray(), errors);
    }
}
