using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns its directory and path-scoped named mutex.
public sealed class ChangeStreamWriterTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-changes-");
    private string Log => Path.Combine(_root.FullName, ChangeStreamWriter.FileName);
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-10-08T12:00:00Z");

    [Fact]
    public void Append_RestartContinuesSequenceAndStampsSchema()
    {
        var writer = new ChangeStreamWriter(Log);
        writer.Append("goal", "g", "first", Timestamp);
        writer.Append("goal-escalation", "g", "second", Timestamp);
        new ChangeStreamWriter(Log).Append("watch-transition", "g", "third", Timestamp);

        var records = Read(Log);
        Assert.Equal(new long[] { 1, 2, 3 }, records.Select(record => record.Sequence));
        Assert.All(records, record => Assert.Equal(ChangeStreamRecord.CurrentSchemaVersion, record.Schema));
        Assert.All(records, record => Assert.Equal(Timestamp, record.Timestamp));
        Assert.Equal(new[] { "first", "second", "third" }, records.Select(record => record.Detail));
    }

    [Fact]
    public async Task Append_ConcurrentInstancesKeepSequenceInFileOrder()
    {
        var writers = new[] { new ChangeStreamWriter(Log), new ChangeStreamWriter(Log) };
        await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(() =>
            writers[index % 2].Append("goal", "g", index.ToString(), Timestamp))))
            .WaitAsync(TimeSpan.FromSeconds(60)); // Hang detector: all append operations must finish.

        var records = Read(Log);
        Assert.Equal(Enumerable.Range(1, 40).Select(index => (long)index), records.Select(record => record.Sequence));
        Assert.Equal(40, records.Select(record => record.Detail).Distinct().Count());
    }

    [Fact]
    public void Append_RotationAndRestartContinueFromRetainedGeneration()
    {
        var writer = new ChangeStreamWriter(Log, maxBytes: 1);
        writer.Append("goal", "g", "first", Timestamp);
        writer.Append("goal", "g", "second", Timestamp);
        Assert.Equal(2, Assert.Single(Read(Log)).Sequence);
        var rotated = Assert.Single(Directory.GetFiles(_root.FullName, "change-stream-*.log"));
        Assert.Equal(1, Assert.Single(Read(rotated)).Sequence);
        // Model a restart between rotation and creation of the next current file.
        File.Move(Log, Path.Combine(_root.FullName, "change-stream-recovered.log"));
        new ChangeStreamWriter(Log, maxBytes: 1).Append("goal", "g", "third", Timestamp);
        Assert.Equal(3, Assert.Single(Read(Log)).Sequence);
    }

    [Fact]
    public void Append_RotationPrunesToConfiguredRetention()
    {
        var writer = new ChangeStreamWriter(Log, maxBytes: 1, rotatedGenerationCount: 2);
        for (var index = 0; index < 5; index++) writer.Append("goal", "g", "change", Timestamp.AddSeconds(index));
        Assert.Equal(5, Assert.Single(Read(Log)).Sequence);
        Assert.Equal(2, Directory.GetFiles(_root.FullName, "change-stream-*.log").Length);
        new ChangeStreamWriter(Log, maxBytes: 1, rotatedGenerationCount: 0).Append("goal", "g", "sixth", Timestamp);
        Assert.Equal(6, Assert.Single(Read(Log)).Sequence);
        Assert.Empty(Directory.GetFiles(_root.FullName, "change-stream-*.log"));
    }

    [Fact]
    public void Append_PartialTailPreservesLastCommittedSequence()
    {
        var writer = new ChangeStreamWriter(Log);
        writer.Append("goal", "g", "first", Timestamp);
        File.AppendAllText(Log, "{\"schema\":1,\"sequence\":200");
        new ChangeStreamWriter(Log).Append("goal", "g", "second", Timestamp);
        var lines = File.ReadAllLines(Log);
        Assert.Equal(3, lines.Length);
        Assert.True(ChangeStreamRecord.TryParse(lines[2], out var record));
        Assert.Equal(2, record!.Sequence);
    }

    [Fact]
    public void Append_UntypedEventCreatesNoFile()
    {
        Assert.Null(new ChangeStreamWriter(Log).Append("acceptance-lease", "g", "lease", Timestamp));
        Assert.False(File.Exists(Log));
    }

    private static ChangeStreamRecord[] Read(string path) => File.ReadAllLines(path)
        .Select(line => JsonSerializer.Deserialize<ChangeStreamRecord>(line, ChangeStreamRecord.JsonOptions)!).ToArray();
    public void Dispose() => _root.Delete(true);
}
