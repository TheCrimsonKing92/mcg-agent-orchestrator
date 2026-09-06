namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RepositorySourceInventoryResult(
    string Root,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Directories,
    string Origin,
    bool Complete,
    int UnreadableDirectoryCount,
    int SkippedLinkBoundaryCount,
    IReadOnlyList<string> IncompleteReasons);

internal static class RepositorySourceInventory
{
    private const int GitTimeoutMilliseconds = 3_000;
    private const int MaxFallbackFiles = 100_000;
    private const int MaxFallbackDirectories = 20_000;

    internal static readonly IReadOnlyList<string> ExcludedDirectoryNames =
    [
        ".git",
        ".mcg-sandbox",
        ".orchestrator",
        ".orchestrator-context",
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

    internal static RepositorySourceInventoryResult Build(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Repository source inventory root '{fullRoot}' was not found.");
        }

        return TryBuildGitInventory(fullRoot, out var gitInventory)
            ? gitInventory
            : BuildFileSystemFallback(fullRoot);
    }

    internal static bool IsExcludedRelativePath(string relativePath)
    {
        return relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(ExcludedDirectoryNameSet.Contains);
    }

    internal static string BuildRecommendedRgCommand()
    {
        return "rg --files " + string.Join(
            ' ',
            ExcludedDirectoryNames.Select(name => $"-g \"!**/{name}/**\""));
    }

    private static bool TryBuildGitInventory(string fullRoot, out RepositorySourceInventoryResult inventory)
    {
        inventory = default!;
        var topLevelResult = GitCli.Run(fullRoot, GitTimeoutMilliseconds, "rev-parse", "--show-toplevel");
        if (!IsUsable(topLevelResult))
        {
            return false;
        }

        var topLevelText = topLevelResult.Output.Trim();
        if (string.IsNullOrWhiteSpace(topLevelText))
        {
            return false;
        }

        string topLevel;
        try
        {
            topLevel = Path.GetFullPath(topLevelText);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!IsWithinRoot(fullRoot, topLevel))
        {
            return false;
        }

        var filesResult = GitCli.Run(
            fullRoot,
            GitTimeoutMilliseconds,
            "ls-files",
            "-z",
            "--cached",
            "--others",
            "--exclude-standard",
            "--full-name",
            "--",
            ".");
        if (!IsUsable(filesResult))
        {
            return false;
        }

        var files = new List<string>();
        var skippedLinkBoundaries = 0;
        foreach (var gitPath in filesResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(
                    topLevel,
                    gitPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            if (!IsWithinRoot(fullPath, fullRoot) || !File.Exists(fullPath))
            {
                continue;
            }

            if (!IsSafeFileLink(fullPath, fullRoot))
            {
                skippedLinkBoundaries++;
                continue;
            }

            var relativePath = NormalizeRelativePath(Path.GetRelativePath(fullRoot, fullPath));
            if (!IsExcludedRelativePath(relativePath))
            {
                files.Add(relativePath);
            }
        }

        var orderedFiles = files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        inventory = new RepositorySourceInventoryResult(
            fullRoot,
            orderedFiles,
            BuildDirectoriesFromFiles(orderedFiles),
            "git",
            Complete: skippedLinkBoundaries == 0,
            UnreadableDirectoryCount: 0,
            skippedLinkBoundaries,
            IncompleteReasons: DescribeSkippedLinks(skippedLinkBoundaries));
        return true;
    }

    private static RepositorySourceInventoryResult BuildFileSystemFallback(string fullRoot)
    {
        var files = new List<string>();
        var directories = new List<string> { string.Empty };
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        var visitedTargets = new HashSet<string>(GetPathComparer()) { fullRoot };
        var unreadableDirectories = new HashSet<string>(GetPathComparer());
        var skippedLinkBoundaries = 0;
        var fileLimitReached = false;
        var directoryLimitReached = false;

        while (pending.Count > 0 && !fileLimitReached && !directoryLimitReached)
        {
            var directory = pending.Pop();
            if (!TryEnumerateFiles(directory, out var childFiles))
            {
                unreadableDirectories.Add(directory);
            }
            else
            {
                foreach (var file in childFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (files.Count >= MaxFallbackFiles)
                    {
                        fileLimitReached = true;
                        break;
                    }

                    var relativePath = NormalizeRelativePath(Path.GetRelativePath(fullRoot, file));
                    if (IsExcludedRelativePath(relativePath))
                    {
                        continue;
                    }

                    if (!IsSafeFileLink(file, fullRoot))
                    {
                        skippedLinkBoundaries++;
                        continue;
                    }

                    files.Add(relativePath);
                }
            }

            if (fileLimitReached)
            {
                break;
            }

            if (!TryEnumerateDirectories(directory, out var childDirectories))
            {
                unreadableDirectories.Add(directory);
                continue;
            }

            foreach (var childDirectory in childDirectories.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relativeDirectory = NormalizeRelativePath(Path.GetRelativePath(fullRoot, childDirectory));
                if (IsExcludedRelativePath(relativeDirectory))
                {
                    continue;
                }

                if (directories.Count >= MaxFallbackDirectories)
                {
                    directoryLimitReached = true;
                    break;
                }

                if (!TryResolveTraversalTarget(childDirectory, fullRoot, out var traversalTarget))
                {
                    skippedLinkBoundaries++;
                    continue;
                }

                if (!visitedTargets.Add(traversalTarget))
                {
                    skippedLinkBoundaries++;
                    continue;
                }

                directories.Add(relativeDirectory);
                pending.Push(childDirectory);
            }
        }

        var reasons = new List<string>();
        if (unreadableDirectories.Count > 0)
        {
            reasons.Add($"{unreadableDirectories.Count} unreadable director{(unreadableDirectories.Count == 1 ? "y" : "ies")}");
        }

        if (skippedLinkBoundaries > 0)
        {
            reasons.AddRange(DescribeSkippedLinks(skippedLinkBoundaries));
        }

        if (fileLimitReached)
        {
            reasons.Add($"fallback file limit {MaxFallbackFiles} reached");
        }

        if (directoryLimitReached)
        {
            reasons.Add($"fallback directory limit {MaxFallbackDirectories} reached");
        }

        return new RepositorySourceInventoryResult(
            fullRoot,
            files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            "filesystem-fallback",
            Complete: reasons.Count == 0,
            unreadableDirectories.Count,
            skippedLinkBoundaries,
            reasons);
    }

    private static bool IsUsable(GitCli.GitResult result)
    {
        return result.ProcessStarted && result.Succeeded && !result.DrainTimedOut;
    }

    private static IReadOnlyList<string> BuildDirectoriesFromFiles(IEnumerable<string> files)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };
        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(directory))
            {
                directories.Add(NormalizeRelativePath(directory));
                directory = Path.GetDirectoryName(directory);
            }
        }

        return directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryEnumerateFiles(string directory, out string[] files)
    {
        try
        {
            files = Directory.GetFiles(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or NotSupportedException)
        {
            files = [];
            return false;
        }
    }

    private static bool TryEnumerateDirectories(string directory, out string[] directories)
    {
        try
        {
            directories = Directory.GetDirectories(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            directories = [];
            return false;
        }
    }

    private static bool TryResolveTraversalTarget(string directory, string root, out string target)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            var resolved = info.LinkTarget is null ? info : info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is null)
            {
                target = string.Empty;
                return false;
            }

            target = Path.GetFullPath(resolved.FullName);
            return IsWithinRoot(target, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            target = string.Empty;
            return false;
        }
    }

    private static bool IsSafeFileLink(string file, string root)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is null)
            {
                return true;
            }

            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            return resolved is not null && IsWithinRoot(Path.GetFullPath(resolved.FullName), root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> DescribeSkippedLinks(int count) =>
        count == 0 ? [] : [$"{count} link boundar{(count == 1 ? "y" : "ies")} skipped"];

    private static bool IsWithinRoot(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative.Equals(".", StringComparison.Ordinal) ||
            (!relative.Equals("..", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
