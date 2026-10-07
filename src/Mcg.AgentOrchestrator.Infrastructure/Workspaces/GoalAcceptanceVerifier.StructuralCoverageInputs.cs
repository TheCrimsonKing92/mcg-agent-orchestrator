using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record UnresolvedRenameDestination(string Source, string Destination);

internal sealed record DeletedTestFileParse(
    string[] Removed,
    IReadOnlyList<UnresolvedRenameDestination> UnresolvedRenames);

internal static class AcceptanceStructuralCoverageInputs
{
    internal static IReadOnlyList<string> DiscoverTrustedTestProjects(string worktreePath)
    {
        var testsRoot = Path.Combine(worktreePath, "tests");
        if (!Directory.Exists(testsRoot))
        {
            return [];
        }

        return Directory.EnumerateFiles(testsRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path =>
                Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
                File.ReadAllText(path).Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase))
            .Select(path => NormalizePath(Path.GetRelativePath(worktreePath, path))!)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> DiscoverTrustedTestProjects(
        string candidateWorktreePath,
        string mainWorktreePath) =>
        DiscoverTrustedTestProjects(candidateWorktreePath)
            .Concat(DiscoverTrustedTestProjects(mainWorktreePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static IReadOnlyList<string> DeletedTestFilesForProject(
        IReadOnlyList<string> deletedTestFiles,
        string project)
    {
        var normalizedProject = NormalizePath(project)!;
        var projectDirectory = NormalizePath(Path.GetDirectoryName(normalizedProject))?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return [];
        }

        return deletedTestFiles
            .Where(path =>
            {
                var normalizedPath = NormalizePath(path);
                return normalizedPath?.StartsWith(
                    $"{projectDirectory}/",
                    StringComparison.OrdinalIgnoreCase) == true;
            })
            .ToArray();
    }

    internal static IReadOnlyList<UnresolvedRenameDestination> UnresolvedRenamesForProject(
        IReadOnlyList<UnresolvedRenameDestination> rows,
        string project)
    {
        var sources = DeletedTestFilesForProject(rows.Select(row => row.Source).ToArray(), project)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Where(row => sources.Contains(row.Source)).ToArray();
    }

    internal static string? ResolveDeletedTestFileDiff(
        string worktreePath,
        Func<string, string[], string?> resolveGitText) =>
        resolveGitText(worktreePath, ["diff", "--name-status", "main...HEAD", "--"]);

    internal static DeletedTestFileParse ParseDeletedTestFiles(
        string output,
        string project,
        Func<string, string?> resolveOwningProject)
    {
        var normalizedProject = NormalizePath(project);
        var unresolved = new List<UnresolvedRenameDestination>();
        var removed = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts =>
            {
                if (parts.Length < 2 || !TestTamperAnalysis.IsTestFile(parts[1]))
                {
                    return false;
                }

                if (parts[0].Equals("D", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (parts.Length < 3 ||
                    !parts[0].StartsWith("R", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var destinationProject = NormalizePath(resolveOwningProject(parts[2]));
                if (string.IsNullOrWhiteSpace(destinationProject))
                {
                    unresolved.Add(new UnresolvedRenameDestination(NormalizePath(parts[1])!, parts[2]));
                }
                return !string.IsNullOrWhiteSpace(destinationProject) &&
                    !string.Equals(destinationProject, normalizedProject, StringComparison.OrdinalIgnoreCase);
            })
            .Select(parts => NormalizePath(parts[1])!)
            .ToArray();
        return new DeletedTestFileParse(removed, unresolved.Distinct()
            .OrderBy(row => row.Source, StringComparer.Ordinal)
            .ThenBy(row => row.Destination, StringComparer.Ordinal)
            .ToArray());
    }

    internal static string? ResolveOwningProject(string worktreePath, string path)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(directory))
            {
                var project = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                    .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (project is not null)
                {
                    return NormalizePath(Path.GetRelativePath(root, project));
                }
            }

            if (directory.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
