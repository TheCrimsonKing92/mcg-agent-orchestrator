using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its temporary directory, file handles and clock.
public sealed class OwnerGateMotionReaderTests : IDisposable
{
    private const string Goal = "57cf8f07be1944ce91efaa1b16dd85e0";
    private const int WindowBytes = 1_048_576;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "owner-gate-motion-" + Guid.NewGuid().ToString("N"));
    private readonly OwnerConsoleTestClock _clock = new();
    private string LogPath => Path.Combine(_directory, "conduct-events.jsonl");

    public OwnerGateMotionReaderTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void OtherEventKindsAndMalformedLinesDoNotSupplyMotion()
    {
        File.WriteAllText(LogPath, UnchangedLines() + Line(2.5, 99, "remote-lane") + "\n" +
            Line(3.5, 98, "goal-landed") + "\n{torn json\n" +
            "{\"eventKind\":\"gate-progress\",\"timestamp\":17}\n", Encoding.UTF8);
        Assert.Equal("no output change 14m", Reader().Describe(Goal));
    }

    [Fact]
    public void MissingFileReturnsNull()
    {
        Assert.False(File.Exists(LogPath));
        Assert.Null(Reader().Describe(Goal));
    }

    [Fact]
    public void OversizedWindowExcludesHeadAndSkipsAValidJsonCutSuffix()
    {
        var samples = UnchangedLines();
        var cutSuffix = Line(2.5, 99) + "\n";
        var paddingLength = WindowBytes - Encoding.UTF8.GetByteCount(cutSuffix + samples) - 1;
        var prefix = Line(3.5, 98) + "\n" + new string('x', WindowBytes + 128) + "\ncut-prefix:";
        var tail = cutSuffix + new string('x', paddingLength) + "\n" + samples;
        Assert.Equal(WindowBytes, Encoding.UTF8.GetByteCount(tail));
        File.WriteAllText(LogPath, prefix + tail, new UTF8Encoding(false));
        Assert.Equal(Encoding.UTF8.GetByteCount(prefix), new FileInfo(LogPath).Length - WindowBytes);
        // Without skipping the cut line, its valid JSON suffix would create recent motion.
        using var cutDocument = JsonDocument.Parse(cutSuffix);
        Assert.Equal("gate-progress", cutDocument.RootElement.GetProperty("eventKind").GetString());
        using var writer = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        Assert.Equal("no output change 14m", Reader().Describe(Goal));
    }

    [Fact]
    public void ReadsWhileWriterIsOpenAndAllowsFurtherAppends()
    {
        File.WriteAllText(LogPath, UnchangedLines(), new UTF8Encoding(false));
        using var writer = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        writer.Flush();
        var reader = Reader();
        Assert.Equal("no output change 14m", reader.Describe(Goal));
        writer.Write(Encoding.UTF8.GetBytes(Line(0, 4000) + "\n"));
        writer.Flush();
        Assert.Equal("output moving", reader.Describe(Goal));
    }

    [Fact]
    public void UnreadableAndMalformedPathsReturnNull()
    {
        Assert.Null(new OwnerGateMotionReader(_directory, _clock).Describe(Goal));
        Assert.Null(new OwnerGateMotionReader(LogPath + '\0', _clock).Describe(Goal));
        File.WriteAllText(LogPath, UnchangedLines());
        using var exclusive = new FileStream(LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(Reader().Describe(Goal));
    }

    private OwnerGateMotionReader Reader() => new(LogPath, _clock);

    private string UnchangedLines() => string.Concat(Enumerable.Range(1, 14).Select(minutes => Line(minutes) + "\n"));

    private string Line(double minutesAgo, long bytes = 3165, string kind = "gate-progress") =>
        JsonSerializer.Serialize(new
        {
            timestamp = _clock.Now.AddMinutes(-minutesAgo), eventKind = kind, goalId = "57cf8f07",
            detail = $"PHASE_PROGRESS phase=test target=\"lane with spaces\" child_pid=123 output_bytes={bytes}"
        });

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
