namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface IWorkerSandboxFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string ReadAllText(string path);
}

internal sealed class SystemWorkerSandboxFileSystem : IWorkerSandboxFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
}

// This boundary only reads checkout identity and labels; preparation owns all label writes.
internal static class WorkerSandboxGitBoundary
{
    internal const string OutsideLinkedWorktree = "writable-sandbox-outside-linked-worktree";
    internal const string LowWritableMetadata = "shared-git-metadata-low-writable";
    internal const string UnverifiableMetadata = "shared-git-metadata-unverifiable";

    internal static string? EnsureWritableSandboxInLinkedWorktree(
        string workingDirectory, IWorkerSandboxFileSystem fileSystem)
    {
        for (var directory = Path.GetFullPath(workingDirectory); directory is not null;
             directory = System.IO.Path.GetDirectoryName(directory))
        {
            var gitEntry = Path.Combine(directory, ".git");
            if (fileSystem.FileExists(gitEntry))
            {
                return gitEntry;
            }

            if (fileSystem.DirectoryExists(gitEntry))
            {
                throw new WorkerSandboxRefusedException(OutsideLinkedWorktree, workingDirectory);
            }
        }

        return null;
    }

    internal static void EnsureSharedGitMetadataNotLowWritable(
        string? linkedWorktreeGitFile, IWorkerIntegrityLabeler labeler, IWorkerSandboxFileSystem fileSystem)
    {
        if (linkedWorktreeGitFile is null)
        {
            return;
        }

        var commonDirectory = ResolveCommonDirectory(linkedWorktreeGitFile, fileSystem);
        Verify(commonDirectory);
        foreach (var child in new[] { "hooks", "refs", "objects" })
        {
            var path = Path.Combine(commonDirectory, child);
            if (fileSystem.DirectoryExists(path))
            {
                Verify(path);
            }
        }

        void Verify(string path)
        {
            var state = labeler.Query(path);
            if (state.NativeQueryError is not null)
            {
                throw new WorkerSandboxRefusedException(UnverifiableMetadata, path);
            }

            if (state.Exists && state.Low)
            {
                throw new WorkerSandboxRefusedException(LowWritableMetadata, path);
            }
        }
    }

    private static string ResolveCommonDirectory(string gitFile, IWorkerSandboxFileSystem fileSystem)
    {
        var sourcePath = gitFile;
        try
        {
            var gitLine = fileSystem.ReadAllText(gitFile).Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
            var gitDirectoryValue = gitLine?["gitdir:".Length..].Trim();
            if (string.IsNullOrEmpty(gitDirectoryValue))
            {
                throw new WorkerSandboxRefusedException(UnverifiableMetadata, gitFile);
            }

            var gitDirectory = Path.GetFullPath(gitDirectoryValue, Path.GetDirectoryName(gitFile)!);
            sourcePath = Path.Combine(gitDirectory, "commondir");
            if (!fileSystem.FileExists(sourcePath))
            {
                return gitDirectory;
            }

            var commonDirectoryValue = fileSystem.ReadAllText(sourcePath).Trim();
            if (string.IsNullOrEmpty(commonDirectoryValue))
            {
                throw new WorkerSandboxRefusedException(UnverifiableMetadata, sourcePath);
            }

            return Path.GetFullPath(commonDirectoryValue, gitDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new WorkerSandboxRefusedException(UnverifiableMetadata, sourcePath, ex);
        }
    }
}
