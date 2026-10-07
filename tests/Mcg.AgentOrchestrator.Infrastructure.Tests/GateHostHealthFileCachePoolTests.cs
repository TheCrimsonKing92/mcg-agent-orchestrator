using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure buffers, scoped probe overrides and unique temporary ledgers.
public sealed class GateHostHealthFileCachePoolTests
{
    [Fact]
    public void LegacyLedgerLineAndCacheRecordRoundTripAtVersionOne()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "host-health.jsonl");
        try
        {
            File.WriteAllText(path, "{\"observedAt\":\"1970-01-01T00:00:00+00:00\",\"gateAttemptId\":\"legacy\",\"launchMs\":60,\"pagedPoolMb\":3400,\"version\":1}\n");
            var legacy = Assert.Single(GateHostHealthLedger.ReadRecent(path));
            Assert.Equal(1, legacy.Version);
            Assert.Null(legacy.FileCachePagedPoolMb);
            var current = new HostHealthLedgerRecord(DateTimeOffset.UnixEpoch, "current", 60, 3400,
                FileCachePagedPoolMb: 2200.5);
            GateHostHealthLedger.Append(path, current);
            Assert.Equal(current, GateHostHealthLedger.ReadRecent(path)[1]);
            var oldReader = JsonSerializer.Deserialize<LegacyRecord>(File.ReadAllLines(path)[1],
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(1, oldReader.Version);
            Assert.Equal(current.PagedPoolMb, oldReader.PagedPoolMb);
            Assert.Equal(current.GateAttemptId, oldReader.GateAttemptId);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void ParserSumsOnlyExactNamedTagsAndPagedBytes(int pointerSize)
    {
        // Independent expectation pins every named tag, including its case-sensitive spelling.
        var tags = new[] { "FMfn", "Ntff", "MmSt", "MPsc", "NtFC", "NtFs", "FIcs", "MPhc",
            "Ntfc", "NtFU", "NtfF", "MmSm", "Ntf0", "Toke", "CM25", "NTFS", "Fmfn", "ntff" };
        var buffer = Buffer(pointerSize, tags);
        var sample = GateHostHealthProbe.ParseFileCachePagedPool(buffer, pointerSize);
        Assert.True(sample.IsAvailable);
        Assert.Equal(13d, sample.Value);
        Assert.Null(sample.UnavailableReason);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void ParserRejectsTruncatedAndImpossibleCounts(int pointerSize)
    {
        var buffer = Buffer(pointerSize, ["FMfn"]);
        AssertUnavailable(GateHostHealthProbe.ParseFileCachePagedPool(buffer.AsSpan(0, buffer.Length - 1), pointerSize));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, uint.MaxValue);
        AssertUnavailable(GateHostHealthProbe.ParseFileCachePagedPool(buffer, pointerSize));
        AssertUnavailable(GateHostHealthProbe.ParseFileCachePagedPool([], pointerSize));
        AssertUnavailable(GateHostHealthProbe.ParseFileCachePagedPool(buffer, 16));
    }

    [Fact]
    public void ParserKeepsAllBitsOf64BitPagedBytes()
    {
        var buffer = Buffer(8, ["FMfn"]);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8 + 16), 6UL * 1024 * 1024 * 1024);
        var sample = GateHostHealthProbe.ParseFileCachePagedPool(buffer, 8);
        Assert.True(sample.IsAvailable);
        Assert.Equal(6144d, sample.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GateCompletionRecordsCacheOrContinuesWithoutIt(bool available)
    {
        var root = CreateRoot();
        using var probe = GateHostHealthProbe.PushProbe(() => new(GateLoadSample.Available(60),
            GateLoadSample.Available(3400), available ? GateLoadSample.Available(2200)
                : GateLoadSample.Unavailable("pooltag-query-status-0xC0000022")));
        try
        {
            var sample = GateHostHealthProbe.Capture();
            Assert.Equal(available, sample.FileCachePagedPoolMb!.IsAvailable);
            if (!available) Assert.Equal("pooltag-query-status-0xC0000022", sample.FileCachePagedPoolMb.UnavailableReason);
            var progress = new List<AcceptanceGateProgress>();
            using (var accountant = AcceptanceGatePhaseAccountant.Start(new FixedClock(), "goal", progress.Add))
            {
                accountant.BindHostHealthLedger(root, "attempt");
                accountant.MarkCompleted(true);
            }
            Assert.Equal("completed", Assert.Single(progress).PhaseBreakdown!.Outcome);
            var record = Assert.Single(GateHostHealthLedger.ReadRecent(GateHostHealthLedger.ResolveStorePath(root)));
            Assert.Equal(3400d, record.PagedPoolMb);
            Assert.Equal(available ? 2200d : (double?)null, record.FileCachePagedPoolMb);
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] Buffer(int pointerSize, string[] tags)
    {
        var stride = pointerSize == 8 ? 40 : 28;
        var buffer = new byte[pointerSize + tags.Length * stride];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)tags.Length);
        for (var index = 0; index < tags.Length; index++)
        {
            var entry = buffer.AsSpan(pointerSize + index * stride, stride);
            Encoding.ASCII.GetBytes(tags[index], entry);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], uint.MaxValue);
            // An unlisted tag outweighs the entire expected sum, so it cannot mask a missing named tag.
            var pagedBytes = (uint)(index < 13 ? 1024 * 1024 : 100 * 1024 * 1024);
            if (pointerSize == 8)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], pagedBytes);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], ulong.MaxValue);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], pagedBytes);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], uint.MaxValue);
            }
        }
        return buffer;
    }

    private static void AssertUnavailable(GateLoadSample sample)
    {
        Assert.False(sample.IsAvailable);
        Assert.Null(sample.Value);
        Assert.Equal("malformed-buffer", sample.UnavailableReason);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "host-health-cache-pool", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record LegacyRecord(DateTimeOffset ObservedAt, string GateAttemptId, double LaunchMs,
        double? PagedPoolMb, int Version = 1);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
        public override long GetTimestamp() => 0;
    }
}
