namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WholeDirectoryRemoval
{
    internal static void Remove(string path, string? ownerFileName = null)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var ownerFile = ownerFileName is null ? null : Path.Combine(path, ownerFileName);
        if (ownerFile is not null && File.Exists(ownerFile))
        {
            // A running binary must make this probe fail before any directory is touched.
            using var probe = File.Open(ownerFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Temporary directory has no parent: {path}");
        var retired = Path.Combine(parent, $".trash-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.Move(path, retired);
        Directory.Delete(retired, recursive: true);
    }

    internal static void TryRemoveEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
