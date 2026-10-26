using System.Text;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all files and the run-event database live in a unique test directory.
public sealed class OwnerConsoleJsonlTailTests : IDisposable
{
    private const int Window = 1_048_576;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "console-tail-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_root, "events.jsonl");

    public OwnerConsoleJsonlTailTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("short")]
    [InlineData("bom-only")]
    [InlineData("mid-line")]
    [InlineData("line-start")]
    [InlineData("crlf")]
    [InlineData("fragment")]
    [InlineData("utf8")]
    public void Select_WindowAndWholeFile_MatchLegacyReaders(string fixture)
    {
        var text = fixture switch
        {
            "short" => "\uFEFF{\"a\":1}\n{\"b\":2}\n",
            "bom-only" => "\uFEFF",
            "mid-line" => new string('x', Window + 13) + "\nkeep\n",
            "line-start" => "drop\n" + new string('x', Window - 6) + "\nkeep\n",
            "crlf" => "{\"a\":1}\r\n{\"b\":2}\r\n",
            "fragment" => "{\"a\":1}\n{\"b\":2}",
            "utf8" => "é" + new string('x', Window - 7) + "\nkeep\n",
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };
        File.WriteAllText(LogPath, text, new UTF8Encoding(false));
        if (fixture == "line-start") Assert.Equal(5, new FileInfo(LogPath).Length - Window);
        if (fixture == "utf8") Assert.Equal(1, new FileInfo(LogPath).Length - Window);
        var tail = new OwnerConsoleJsonlTail(LogPath);
        Assert.Equal(ReadTail(LogPath), tail.Select("window", line => line, Window));
        Assert.Equal(ReadWhole(LogPath), tail.Select("whole", line => line, long.MaxValue));
    }

    [Fact]
    public void Select_TwoConsumers_ParseOnlyNewCompleteLines()
    {
        File.WriteAllText(LogPath, Lines(0, 500));
        var tail = new OwnerConsoleJsonlTail(LogPath);
        var callsA = 0;
        var callsB = 0;
        IReadOnlyList<string> A() => tail.Select("a", line => { callsA++; return line; }, long.MaxValue);
        IReadOnlyList<string> B() => tail.Select("b", line => { callsB++; return line; }, long.MaxValue);
        Assert.Equal(500, A().Count);
        Assert.Equal(500, B().Count);
        var bytes = new FileInfo(LogPath).Length;
        Assert.Equal(bytes, tail.ContentBytesRead);
        Assert.Equal(1000, tail.ParseCalls);
        Assert.Equal(500, callsA);
        Assert.Equal(500, callsB);

        A(); B();
        Assert.Equal(bytes, tail.ContentBytesRead);
        Assert.Equal(1000, tail.ParseCalls);
        Assert.Equal(500, callsA);
        Assert.Equal(500, callsB);

        var appended = Lines(500, 7);
        File.AppendAllText(LogPath, appended);
        var fresh = new OwnerConsoleJsonlTail(LogPath).Select("fresh", line => line, long.MaxValue);
        Assert.Equal(fresh, A());
        Assert.Equal(fresh, B());
        Assert.Equal(bytes + Encoding.UTF8.GetByteCount(appended), tail.ContentBytesRead);
        Assert.Equal(1014, tail.ParseCalls);
        Assert.Equal(507, callsA);
        Assert.Equal(507, callsB);
    }

    [Theory]
    [InlineData("rewrite")]
    [InlineData("shrink")]
    [InlineData("rename")]
    public void Select_ReplacedContent_ResetsAndReparsesWholeFile(string change)
    {
        File.WriteAllText(LogPath, Lines(0, 30));
        var creationTime = File.GetCreationTimeUtc(LogPath);
        var tail = new OwnerConsoleJsonlTail(LogPath);
        Assert.Equal(30, tail.Select("a", line => line, long.MaxValue).Count);
        var calls = tail.ParseCalls;
        var count = change == "shrink" ? 3 : 40;
        if (change == "rename") File.Move(LogPath, LogPath + ".old");
        File.WriteAllText(LogPath, Lines(1000, count));
        if (change == "rewrite")
        {
            File.SetCreationTimeUtc(LogPath, creationTime);
            Assert.Equal(creationTime, File.GetCreationTimeUtc(LogPath));
        }

        Assert.Equal(new OwnerConsoleJsonlTail(LogPath).Select("fresh", line => line, long.MaxValue),
            tail.Select("a", line => line, long.MaxValue));
        Assert.Equal(calls + count, tail.ParseCalls);
    }

    [Fact]
    public void Select_FinalFragment_ReparsesUntilTerminatedThenCaches()
    {
        File.WriteAllText(LogPath, "{\"a\":1}\n{\"b\":");
        var tail = new OwnerConsoleJsonlTail(LogPath);
        Assert.Equal(["{\"a\":1}", "{\"b\":"], tail.Select("a", line => line, long.MaxValue));
        Assert.Equal(2, tail.ParseCalls);
        tail.Select("a", line => line, long.MaxValue);
        Assert.Equal(3, tail.ParseCalls);
        File.AppendAllText(LogPath, "2}\n");
        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], tail.Select("a", line => line, long.MaxValue));
        Assert.Equal(4, tail.ParseCalls);
        tail.Select("a", line => line, long.MaxValue);
        Assert.Equal(4, tail.ParseCalls);
    }

    [Fact]
    public async Task Select_MissingFile_ReadersKeepFallbackBehavior()
    {
        var clock = new OwnerConsoleTestClock();
        var tail = new OwnerConsoleJsonlTail(LogPath);
        Assert.Throws<FileNotFoundException>(() => tail.Select("a", line => line, Window));
        Assert.Null(new OwnerGateMotionReader(LogPath, clock, tail).Describe("57cf8f07"));
        var manifestPath = Path.Combine(_root, "manifest.json");
        File.WriteAllText(manifestPath, """{"engine":{"infrastructureTestLanes":[]}}""");
        Assert.Null(new OwnerConsoleGateProgressLoader(LogPath, manifestPath,
            Path.Combine(_root, "durations.jsonl"), clock, conductTail: tail).Load(new Dictionary<string, string>()));
        var runPath = Path.Combine(_root, "run-events.db");
        var runs = new SqliteRunEventStore(runPath);
        await runs.AppendAsync(new(RunEventTypes.ConductorTick, null, "tick", "complete", "tick=1", null, clock.Now));
        Assert.Equal(clock.Now, new OwnerConsoleLatestTickReader(tail, runPath).Read());
        Assert.False(File.Exists(LogPath));
    }

    private static string Lines(int start, int count) =>
        string.Concat(Enumerable.Range(start, count).Select(index => $"{{\"line\":{index}}}\n"));

    private static IReadOnlyList<string> ReadWhole(string path)
    {
        using var reader = new StreamReader(path);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    // Verbatim legacy OwnerConsoleGateProgressLoader.ReadTail, with its path supplied by the test.
    private static IEnumerable<string> ReadTail(string logPath)
    {
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var offset = Math.Max(0, length - 1_048_576);
        stream.Position = offset;
        var buffer = new byte[(int)(length - offset)];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0) break;
            read += count;
        }
        using var data = new MemoryStream(buffer, 0, read);
        using var reader = new StreamReader(data);
        if (offset > 0) reader.ReadLine();
        while (reader.ReadLine() is { } line) yield return line;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
