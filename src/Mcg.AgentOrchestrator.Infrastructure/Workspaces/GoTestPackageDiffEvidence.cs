namespace Mcg.AgentOrchestrator.Infrastructure;

// Evidence is the committed candidate range, never the current working tree.
internal static class GoTestPackageDiffEvidence
{
    internal static IReadOnlySet<string> Read(
        string repositoryDirectory, string integrationBranch, string candidateSha,
        Func<string, string[], GitCli.GitResult>? runGit = null)
    {
        runGit ??= GitCli.Run;
        try
        {
            string Git(params string[] args)
            {
                var result = runGit(repositoryDirectory, args);
                if (!result.Succeeded || result.DrainTimedOut)
                    throw new InvalidOperationException("Git package evidence unavailable.");
                return result.Output;
            }

            var mergeBase = Git("merge-base", integrationBranch, candidateSha).Trim();
            if (mergeBase.Length == 0) return new HashSet<string>(StringComparer.Ordinal);
            // Disabling rename detection includes both endpoints, including moves across the pathspec.
            var paths = Git("diff", "--no-renames", "--name-only", "-z", mergeBase, candidateSha, "--", "*_test.go");
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!path.EndsWith("_test.go", StringComparison.Ordinal)) continue;
                var slash = path.LastIndexOf('/');
                var directory = NormalizeDirectory(repositoryDirectory, slash < 0 ? "." : path[..slash]);
                if (directory is null) return new HashSet<string>(StringComparer.Ordinal);
                directories.Add(directory);
            }
            return directories;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    internal static string? NormalizeDirectory(string repositoryDirectory, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            var root = Path.GetFullPath(repositoryDirectory);
            var path = directory.Replace('\\', '/');
            if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path)) return null;
            var relative = Path.GetRelativePath(root, Path.GetFullPath(path, root)).Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
                return null;
            return relative == "." ? "" : relative.TrimEnd('/');
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
