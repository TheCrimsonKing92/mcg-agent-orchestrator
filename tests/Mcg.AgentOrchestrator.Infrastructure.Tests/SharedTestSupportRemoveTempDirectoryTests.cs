public sealed class SharedTestSupportRemoveTempDirectoryTests
{
    [Fact]
    public void RemoveTempDirectory_HolderReleasesAfterTwoAttempts_RemovesDirectoryAndReturns()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var holder = HoldDirectory(root);
        var delays = 0;
        try
        {
            // Elapsed never advances, so only the holder releasing can end the loop.
            SharedTestSupport.RemoveTempDirectory(
                root,
                () => TimeSpan.Zero,
                _ =>
                {
                    delays++;
                    if (delays == 2)
                    {
                        holder.Release();
                    }
                });
        }
        finally
        {
            holder.Release();
        }

        Assert.Equal(2, delays);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void RemoveTempDirectory_HolderKeepsDirectoryPastBudget_ThrowsNamingPathAndAttempts()
    {
        var root = SharedTestSupport.CreateTempDirectory();
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
            Assert.True(
                elapsed >= TimeSpan.FromMilliseconds(SharedTestSupport.RemoveTempDirectoryBudgetMilliseconds),
                $"Expected the loud failure only after the {SharedTestSupport.RemoveTempDirectoryBudgetMilliseconds} ms budget, "
                    + $"but it threw after {elapsed.TotalMilliseconds} ms.");
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            holder.Release();
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void RemoveTempDirectory_MissingDirectory_ReturnsSilentlyWithoutDelaying()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var missing = Path.Combine(root, "never-created");
        var delays = 0;
        try
        {
            SharedTestSupport.RemoveTempDirectory(missing, () => TimeSpan.Zero, _ => delays++);

            Assert.Equal(0, delays);
            Assert.False(Directory.Exists(missing));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static DirectoryHolder HoldDirectory(string root)
    {
        var heldFile = Path.Combine(root, "held.txt");
        File.WriteAllText(heldFile, "held");
        if (OperatingSystem.IsWindows())
        {
            var stream = new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.None);
            return new DirectoryHolder(stream.Dispose);
        }

        // An open handle does not block unlink outside Windows, so withhold write permission
        // on the directory itself until the holder releases.
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
