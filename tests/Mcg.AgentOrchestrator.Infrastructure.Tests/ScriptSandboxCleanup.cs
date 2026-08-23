internal static class ScriptSandboxCleanup
{
    private const int DefaultAttempts = 3;

    internal static void DeleteOrThrow(IReadOnlyList<string> roots) =>
        DeleteOrThrow(roots, Directory.Exists, DeleteDirectoryTree, DefaultAttempts);

    internal static void DeleteOrThrow(
        IReadOnlyList<string> roots,
        Func<string, bool> directoryExists,
        Action<string> deleteDirectory,
        int attempts)
    {
        var failures = Delete(roots, directoryExists, deleteDirectory, attempts);
        if (failures.Count == 0)
        {
            return;
        }

        throw new IOException(
            "One or more MTP sandbox roots could not be removed." + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    internal static IReadOnlyList<string> Delete(
        IReadOnlyList<string> roots,
        Func<string, bool> directoryExists,
        Action<string> deleteDirectory,
        int attempts)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(deleteDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        List<string> failures = [];
        foreach (var root in roots)
        {
            var fullPath = Path.GetFullPath(root);
            Exception? lastFailure = null;
            for (var attempt = 1; attempt <= attempts && directoryExists(fullPath); attempt++)
            {
                try
                {
                    deleteDirectory(fullPath);
                    lastFailure = null;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                }
            }

            if (!directoryExists(fullPath))
            {
                continue;
            }

            var reason = lastFailure is null
                ? "the delete operation returned without removing the directory"
                : $"{lastFailure.GetType().Name}: {lastFailure.Message}";
            failures.Add(
                $"Test debris remains at '{fullPath}' after {attempts} attempts ({reason}). " +
                "Do not commit this directory. Release the process or file lock named by the failure, " +
                "delete the directory, and rerun the Process spawning lane.");
        }

        return failures;
    }

    internal static void DeleteDirectoryTree(string path)
    {
        foreach (var filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            var directory = new DirectoryInfo(directoryPath);
            directory.Attributes &= ~FileAttributes.ReadOnly;
        }

        var root = new DirectoryInfo(path);
        root.Attributes &= ~FileAttributes.ReadOnly;
        Directory.Delete(path, recursive: true);
    }
}
