namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SourceSurveyReport(
    string Root,
    int MaxFiles,
    int ReturnedFiles,
    int TotalMatchedFiles,
    IReadOnlyList<string> Files,
    IReadOnlyList<SourceSurveyGroup> Groups,
    IReadOnlyList<string> ExcludedDirectoryNames,
    string RecommendedCommand);

internal sealed record SourceSurveyGroup(string Directory, int Count);

internal static class SourceSurvey
{
    public const int DefaultMaxFiles = 200;

    private static readonly string[] ExcludedDirectoryNames =
    [
        ".git",
        ".orchestrator",
        ".orchestrator-demo",
        ".orchestrator-prototype",
        ".orchestrator-worktrees",
        ".scratch",
        "artifacts",
        "bin",
        "TestResults",
        "obj",
        "node_modules",
        "playwright-report"
    ];

    private static readonly HashSet<string> ExcludedDirectoryNameSet = new(
        ExcludedDirectoryNames,
        StringComparer.OrdinalIgnoreCase);

    public static SourceSurveyReport Build(string root, int maxFiles = DefaultMaxFiles)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Source survey root cannot be empty.", nameof(root));
        }

        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Source survey root '{fullRoot}' was not found.");
        }

        var limit = Math.Clamp(maxFiles, 1, 1000);
        var files = new List<string>(limit);
        var total = 0;

        foreach (var file in EnumerateSourceFiles(fullRoot))
        {
            total++;
            if (files.Count < limit)
            {
                files.Add(file);
            }
        }

        var groups = files
            .GroupBy(GetTopLevelDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SourceSurveyGroup(group.Key, group.Count()))
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SourceSurveyReport(
            fullRoot,
            limit,
            files.Count,
            total,
            files,
            groups,
            ExcludedDirectoryNames,
            BuildRecommendedCommand());
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (var directory in EnumerateSourceDirectoryPaths(root))
        {
            foreach (var file in EnumerateFiles(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            }
        }
    }

    internal static IReadOnlyList<string> EnumerateSourceDirectories(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        return EnumerateSourceDirectoryPaths(fullRoot)
            .Skip(1)
            .Select(directory => Path.GetRelativePath(fullRoot, directory).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
    }

    private static IEnumerable<string> EnumerateSourceDirectoryPaths(string root)
    {
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            yield return directory;
            foreach (var child in EnumerateDirectories(directory).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!ExcludedDirectoryNameSet.Contains(Path.GetFileName(child)))
                {
                    directories.Push(child);
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string GetTopLevelDirectory(string relativePath)
    {
        var index = relativePath.IndexOf('/', StringComparison.Ordinal);
        return index < 0 ? "." : relativePath[..index];
    }

    private static string BuildRecommendedCommand()
    {
        return "rg --files -g \"!**/artifacts/**\" -g \"!**/bin/**\" -g \"!**/TestResults/**\" -g \"!**/obj/**\" -g \"!**/.scratch/**\" " +
            "-g \"!**/.orchestrator/**\" -g \"!**/.orchestrator-demo/**\" -g \"!**/.orchestrator-prototype/**\" " +
            "-g \"!**/.orchestrator-worktrees/**\" -g \"!**/node_modules/**\" -g \"!**/playwright-report/**\"";
    }
}
