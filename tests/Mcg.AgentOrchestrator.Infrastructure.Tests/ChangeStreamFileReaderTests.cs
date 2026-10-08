using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its stream and rotated generations.
public sealed class ChangeStreamFileReaderTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-change-reader-");
    private string Log => Path.Combine(_root.FullName, ChangeStreamWriter.FileName);
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-10-08T12:00:00Z");

    [Fact]
    public void ReadAvailable_TailsNewRecordsAndBuffersPartialUtf8Line()
    {
        var writer = new ChangeStreamWriter(Log);
        writer.Append("goal", "g", "covered", Timestamp);
        var reader = new ChangeStreamFileReader(Log);
        Assert.Empty(reader.ReadAvailable(out var signal));
        Assert.Null(signal);
        var record = Record(2) with { Detail = "café" };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, ChangeStreamRecord.JsonOptions) + "\n");
        using (var stream = new FileStream(Log, FileMode.Append)) stream.Write(bytes.AsSpan(0, bytes.Length - 4));
        Assert.Empty(reader.ReadAvailable(out signal));
        Assert.Null(signal);
        using (var stream = new FileStream(Log, FileMode.Append)) stream.Write(bytes.AsSpan(bytes.Length - 4));
        Assert.Equal(record, Assert.Single(reader.ReadAvailable(out signal)));
        Assert.Null(signal);
        Assert.Empty(reader.ReadAvailable(out signal));
        Assert.Null(signal);
    }

    [Theory]
    [InlineData(ChangeStreamFileReader.Gap)]
    [InlineData(ChangeStreamFileReader.Rotation)]
    [InlineData(ChangeStreamFileReader.UnknownSchema)]
    public void ReadAvailable_DiscontinuitySignalsAndReanchorRestoresDeltas(string expected)
    {
        new ChangeStreamWriter(Log).Append("goal", "g", "covered", Timestamp);
        var reader = new ChangeStreamFileReader(Log);
        if (expected == ChangeStreamFileReader.Rotation)
            File.Move(Log, Path.Combine(_root.FullName, "change-stream-moved.log"));
        Append(Record(expected == ChangeStreamFileReader.Gap ? 3 : 2) with
        { Schema = expected == ChangeStreamFileReader.UnknownSchema ? 99 : 1 });

        Assert.Empty(reader.ReadAvailable(out var signal));
        Assert.Equal(expected, signal);
        var anchor = reader.Reanchor();
        Assert.Equal(expected == ChangeStreamFileReader.Gap ? 3 : 2, anchor);
        Append(Record(anchor + 1));
        Assert.Equal(anchor + 1, Assert.Single(reader.ReadAvailable(out signal)).Sequence);
        Assert.Null(signal);
    }

    [Fact]
    public void ReadAvailable_ReplacedSameLengthAndCreationTimeSignalsRotation()
    {
        Append(Record(1));
        var reader = new ChangeStreamFileReader(Log);
        var creation = File.GetCreationTimeUtc(Log);
        var length = new FileInfo(Log).Length;
        File.Move(Log, Path.Combine(_root.FullName, "change-stream-moved.log"));
        Append(Record(2));
        File.SetCreationTimeUtc(Log, creation);
        Assert.Equal(length, new FileInfo(Log).Length);
        Assert.Equal(creation, File.GetCreationTimeUtc(Log));
        Assert.Empty(reader.ReadAvailable(out var signal));
        Assert.Equal(ChangeStreamFileReader.Rotation, signal);
    }

    [Fact]
    public void ReadAvailable_CorruptCompleteLineSignalsUnknownSchema()
    {
        var reader = new ChangeStreamFileReader(Log);
        File.AppendAllText(Log, "{malformed}\n");
        Assert.Empty(reader.ReadAvailable(out var signal));
        Assert.Equal(ChangeStreamFileReader.UnknownSchema, signal);
        reader.Reanchor();
        Append(Record(1));
        Assert.Equal(1, Assert.Single(reader.ReadAvailable(out signal)).Sequence);
        Assert.Null(signal);
    }

    [Fact]
    public void ReadAvailable_FirstFileCanArriveAsPartialLine()
    {
        var reader = new ChangeStreamFileReader(Log);
        var json = JsonSerializer.Serialize(Record(1), ChangeStreamRecord.JsonOptions);
        File.WriteAllText(Log, json[..10]);
        Assert.Empty(reader.ReadAvailable(out var signal));
        Assert.Null(signal);
        File.AppendAllText(Log, json[10..] + "\n");
        Assert.Equal(1, Assert.Single(reader.ReadAvailable(out signal)).Sequence);
        Assert.Null(signal);
    }

    private static ChangeStreamRecord Record(long sequence) => new(1, sequence, Timestamp,
        ChangeStreamRecord.GoalTransition, "g", "goal", "changed");
    private void Append(ChangeStreamRecord record) =>
        File.AppendAllText(Log, JsonSerializer.Serialize(record, ChangeStreamRecord.JsonOptions) + "\n");
    public void Dispose() => _root.Delete(true);
}
