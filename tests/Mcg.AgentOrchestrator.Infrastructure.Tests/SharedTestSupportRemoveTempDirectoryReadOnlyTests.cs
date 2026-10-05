using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns a unique temp root, git repository and file handles.
public sealed class SharedTestSupportRemoveTempDirectoryReadOnlyTests
{
    [Fact]
    public void RemoveTempDirectory_ReadOnlyFilesInNestedTree_RemovesDirectory()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var looseObject = Path.Combine(root, "objects", "ab", "cdef0123");
        var pack = Path.Combine(root, "objects", "pack", "pack-1.pack");
        Directory.CreateDirectory(Path.GetDirectoryName(looseObject)!);
        Directory.CreateDirectory(Path.GetDirectoryName(pack)!);
        File.WriteAllText(looseObject, "loose object");
        File.WriteAllText(pack, "pack");
        File.SetAttributes(looseObject, File.GetAttributes(looseObject) | FileAttributes.ReadOnly);
        File.SetAttributes(pack, File.GetAttributes(pack) | FileAttributes.ReadOnly);
        var elapsed = TimeSpan.Zero;

        var error = Record.Exception(() => SharedTestSupport.RemoveTempDirectory(
            root, () => elapsed, wait => elapsed += wait));

        Assert.Null(error);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void RemoveTempDirectory_SeededGitRepository_RemovesDirectory()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        RunGit(root, "init", "-q");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "seed.txt");
        RunGit(root, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false",
            "commit", "-q", "-m", "seed");
        Assert.True(Directory.Exists(Path.Combine(root, ".git", "objects")));
        var elapsed = TimeSpan.Zero;

        SharedTestSupport.RemoveTempDirectory(root, () => elapsed, wait => elapsed += wait);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void RemoveTempDirectory_HeldTreeWithReadOnlyFile_ThrowsNamingPathAndAttempts()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var readOnlyFile = Path.Combine(root, "readonly.txt");
        File.WriteAllText(readOnlyFile, "read only");
        File.SetAttributes(readOnlyFile, File.GetAttributes(readOnlyFile) | FileAttributes.ReadOnly);
        var holder = HoldDirectory(root);
        var elapsed = TimeSpan.Zero;
        var delays = 0;
        try
        {
            var error = Assert.Throws<IOException>(() => SharedTestSupport.RemoveTempDirectory(
                root,
                () => elapsed,
                wait =>
                {
                    delays++;
                    elapsed += wait;
                }));

            Assert.Contains(root, error.Message, StringComparison.Ordinal);
            Assert.Contains($"{delays + 1} attempts", error.Message, StringComparison.Ordinal);
            Assert.Contains("ms.", error.Message, StringComparison.Ordinal);
            Assert.NotNull(error.InnerException);
            Assert.Contains(error.InnerException!.Message, error.Message, StringComparison.Ordinal);
            Assert.True(elapsed >= TimeSpan.FromMilliseconds(SharedTestSupport.RemoveTempDirectoryBudgetMilliseconds));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            holder.Release();
            // Also clean up on main, where removal leaves both files read-only.
            foreach (var file in new[] { readOnlyFile, Path.Combine(root, "held.txt") })
            {
                if (File.Exists(file))
                {
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                }
            }
            SharedTestSupport.RemoveTempDirectory(root, () => elapsed, wait => elapsed += wait);
        }
    }

    private static void RunGit(string root, params string[] arguments)
    {
        var result = GitCli.Run(root, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} failed: {result.Error}");
    }

    private static DirectoryHolder HoldDirectory(string root)
    {
        var heldFile = Path.Combine(root, "held.txt");
        File.WriteAllText(heldFile, "held");
        File.SetAttributes(heldFile, File.GetAttributes(heldFile) | FileAttributes.ReadOnly);
        if (OperatingSystem.IsWindows())
        {
            var stream = new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.None);
            return new DirectoryHolder(stream.Dispose);
        }

        // Withhold directory write permission because an open handle does not block Unix unlink.
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new DirectoryHolder(() => File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
    }

    private sealed class DirectoryHolder
    {
        private Action? _release;

        public DirectoryHolder(Action release) => _release = release;

        public void Release()
        {
            var release = _release;
            _release = null;
            release?.Invoke();
        }
    }
}
