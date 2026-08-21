using Mcg.AgentOrchestrator.Core;

public sealed class SharedJsonlFileTests
{
    [Xunit.Fact]
    public void ReadAllLines_AppenderIsOpen_ReturnsCompleteLines()
    {
        var root = CreateTempDirectory();
        var path = CreateJournalPath(root);
        try
        {
            using var heldWriter = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            WriteRecord(heldWriter, "first");

            Assert.Equal(new[] { "first" }, SharedJsonlFile.ReadAllLines(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void SharedJsonlFile_AppendUnderShareViolation_PreservesBothRecordsInOrder()
    {
        var root = CreateTempDirectory();
        var path = CreateJournalPath(root);
        FileStream? heldWriter = null;
        try
        {
            heldWriter = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete);
            WriteRecord(heldWriter, "first");

            SharedJsonlFile.AppendLine(path, "second", retryNumber =>
            {
                Assert.Equal(1, retryNumber);
                heldWriter!.Dispose();
                heldWriter = null;
            });

            Assert.Equal(new[] { "first", "second" }, File.ReadAllLines(path));
        }
        finally
        {
            heldWriter?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void AppendLines_ShareViolation_PreservesAllRecords()
    {
        var root = CreateTempDirectory();
        var path = CreateJournalPath(root);
        FileStream? heldWriter = null;
        try
        {
            heldWriter = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete);
            WriteRecord(heldWriter, "first");

            SharedJsonlFile.AppendLines(path, ["second", "third"], retryNumber =>
            {
                Assert.Equal(1, retryNumber);
                heldWriter!.Dispose();
                heldWriter = null;
            });

            Assert.Equal(new[] { "first", "second", "third" }, File.ReadAllLines(path));
        }
        finally
        {
            heldWriter?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void SharedJsonlFile_PersistentShareViolation_ThrowsAfterBound()
    {
        var root = CreateTempDirectory();
        var path = CreateJournalPath(root);
        try
        {
            using var heldWriter = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete);
            var retries = 0;

            Assert.Throws<IOException>(() =>
                SharedJsonlFile.AppendLine(path, "blocked", _ => retries++));

            Assert.Equal(SharedJsonlFile.MaximumShareViolationRetries, retries);
            Assert.Equal(0L, heldWriter.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void SharedJsonlFile_AppendToDirectoryPath_SurfacesWithoutRetry()
    {
        var root = CreateTempDirectory();
        var retries = 0;
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                SharedJsonlFile.AppendLine(root, "blocked", _ => retries++));

            Assert.Equal(0, retries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void SharedJsonlFile_MissingParentDirectory_SurfacesDirectoryNotFound()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "missing", "goal.jsonl");
        var retries = 0;
        try
        {
            Assert.Throws<DirectoryNotFoundException>(() =>
                SharedJsonlFile.AppendLine(path, "blocked", _ => retries++));

            Assert.Equal(0, retries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-shared-jsonl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateJournalPath(string root)
    {
        var directory = Path.Combine(root, ".orchestrator", "goal-operations");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.jsonl");
    }

    private static void WriteRecord(FileStream stream, string record)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(record + Environment.NewLine);
        stream.Write(bytes);
        stream.Flush();
    }
}
