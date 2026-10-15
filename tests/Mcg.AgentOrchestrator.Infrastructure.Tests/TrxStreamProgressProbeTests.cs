using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns a unique temp directory.
public sealed class TrxStreamProgressProbeTests
{
    private static readonly DateTime FileTime = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Missing_directory_has_no_sample()
    {
        InTempDirectory(directory =>
            Assert.Null(new TrxStreamProgressProbe(Path.Combine(directory, "missing")).Sample()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_directory_without_matching_files_has_no_sample(bool unrelatedFiles)
    {
        InTempDirectory(directory =>
        {
            if (unrelatedFiles)
            {
                foreach (var name in new[] { "other.bin", "results.trx", "trx-stream-1.bin2" })
                {
                    var path = Path.Combine(directory, name);
                    File.WriteAllBytes(path, [1]);
                    File.SetLastWriteTimeUtc(path, FileTime);
                }
            }
            Assert.Null(new TrxStreamProgressProbe(directory).Sample());
        });
    }

    [Fact]
    public void Unchanged_files_return_equal_fingerprints()
    {
        InTempDirectory(directory =>
        {
            WriteStream(directory, "trx-stream-1.bin", [1, 2], FileTime);
            WriteStream(directory, "trx-stream-2.bin", [3], FileTime.AddMinutes(-1));
            var probe = new TrxStreamProgressProbe(directory);
            var sample = probe.Sample();
            Assert.Equal(Fingerprint(2, 3, FileTime), sample);
            Assert.Equal(sample, probe.Sample());
        });
    }

    [Fact]
    public void File_growth_changes_the_fingerprint_without_a_timestamp_change()
    {
        InTempDirectory(directory =>
        {
            var path = WriteStream(directory, "trx-stream-1.bin", [1], FileTime);
            var probe = new TrxStreamProgressProbe(directory);
            var before = probe.Sample();
            File.AppendAllText(path, "x");
            File.SetLastWriteTimeUtc(path, FileTime);
            var after = probe.Sample();
            Assert.NotEqual(before, after);
            Assert.Equal(Fingerprint(1, 2, FileTime), after);
        });
    }

    [Fact]
    public void A_stream_added_in_a_subfolder_changes_count_and_total_length()
    {
        InTempDirectory(directory =>
        {
            WriteStream(directory, "trx-stream-1.bin", [1], FileTime);
            var probe = new TrxStreamProgressProbe(directory);
            var before = probe.Sample();
            var subfolder = Path.Combine(directory, "nested");
            Directory.CreateDirectory(subfolder);
            WriteStream(subfolder, "trx-stream-2.bin", [2, 3], FileTime.AddMinutes(-1));
            var after = probe.Sample();
            Assert.NotEqual(before, after);
            Assert.Equal(Fingerprint(2, 3, FileTime), after);
        });
    }

    [Fact]
    public void Last_write_only_change_changes_the_fingerprint()
    {
        InTempDirectory(directory =>
        {
            var path = WriteStream(directory, "trx-stream-1.bin", [1], FileTime);
            var probe = new TrxStreamProgressProbe(directory);
            var before = probe.Sample();
            File.SetLastWriteTimeUtc(path, FileTime.AddMinutes(5));
            var after = probe.Sample();
            Assert.NotEqual(before, after);
            Assert.Equal(Fingerprint(1, 1, FileTime.AddMinutes(5)), after);
        });
    }

    [Fact]
    public void Exclusive_access_does_not_throw_or_hide_other_files()
    {
        InTempDirectory(directory =>
        {
            var path = WriteStream(directory, "trx-stream-1.bin", [1], FileTime);
            WriteStream(directory, "trx-stream-2.bin", [2], FileTime);
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var probe = new TrxStreamProgressProbe(directory);
            string? sample = null;
            Assert.Null(Record.Exception(() => { sample = probe.Sample(); }));
            Assert.NotNull(sample);
        });
    }

    [Fact]
    public void Invalid_directory_path_does_not_throw()
    {
        InTempDirectory(directory =>
        {
            var probe = new TrxStreamProgressProbe(directory + "\0");
            Assert.Null(Record.Exception(() => Assert.Null(probe.Sample())));
        });
    }

    private static string Fingerprint(int count, long length, DateTime lastWrite) =>
        string.Create(CultureInfo.InvariantCulture, $"{count}:{length}:{lastWrite.Ticks}");

    private static string WriteStream(string directory, string name, byte[] bytes, DateTime lastWrite)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, lastWrite);
        return path;
    }

    private static void InTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"trx-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
