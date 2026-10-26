using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each fixture owns its writer, clock and GUID temporary root.
public sealed class ConductEventsLogTailTests
{
    [Fact]
    public void Rows_AppendsAndProvisionalLine_CommitEachEventExactlyOnce()
    {
        using var fixture = new Fixture();
        fixture.Append("first");
        var tail = new ConductEventsLogTail();
        var first = tail.Rows(fixture.Paths()).ToArray();
        Assert.Single(first);
        Assert.Equal(first, tail.Rows(fixture.Paths()));
        Assert.Equal(0, tail.LastReadBytes);

        var before = new FileInfo(fixture.Log).Length;
        fixture.Append("second");
        var length = new FileInfo(fixture.Log).Length - before;
        var appended = tail.Rows(fixture.Paths());
        Assert.Equal(2, appended.Count);
        Assert.Single(appended, row => row.Detail.EndsWith("second", StringComparison.Ordinal));
        Assert.Equal(length, tail.LastReadBytes);
        Assert.Equal(appended, tail.Rows(fixture.Paths()));
        Assert.Equal(0, tail.LastReadBytes);

        var trailing = JsonSerializer.Serialize(new { timestamp = fixture.Clock.Now, detail = "GOAL goal=12345678 result=trailing" });
        File.AppendAllText(fixture.Log, trailing);
        var provisional = tail.Rows(fixture.Paths());
        Assert.Equal(new ConductEventsLogTail().Rows(fixture.Paths()), provisional);
        Assert.Single(provisional, row => row.Detail.EndsWith("trailing", StringComparison.Ordinal));
        Assert.Equal(Encoding.UTF8.GetByteCount(trailing), tail.LastReadBytes);
        File.AppendAllText(fixture.Log, "\n");
        var completed = tail.Rows(fixture.Paths());
        Assert.Equal(provisional, completed);
        Assert.Equal(Encoding.UTF8.GetByteCount(trailing) + 1, tail.LastReadBytes);
        Assert.Equal(completed, tail.Rows(fixture.Paths()));
        Assert.Equal(0, tail.LastReadBytes);
    }

    [Fact]
    public void Rows_WriterRotationAndPrune_MatchesFreshTailWithoutOldRows()
    {
        using var fixture = new Fixture();
        var tail = new ConductEventsLogTail();
        string? oldest = null;
        (DateTimeOffset At, string Detail)[] oldestRows = [];
        var rotated = new HashSet<string>(StringComparer.Ordinal);
        var pruned = false;
        for (var index = 0; index < 32; index++)
        {
            fixture.Append(index + new string('x', 600));
            var paths = fixture.Paths();
            var rows = tail.Rows(paths);
            Assert.Equal(new ConductEventsLogTail().Rows(paths), rows);
            Assert.Equal(rows.Count, rows.Distinct().Count());
            Assert.Single(rows, row => row.Detail.EndsWith(index + new string('x', 600), StringComparison.Ordinal));
            foreach (var path in paths.Where(path => path != fixture.Log)) rotated.Add(path);
            if (oldest is null && paths.Length > 1)
            {
                oldest = paths[0];
                oldestRows = new ConductEventsLogTail().Rows([oldest]).ToArray();
                Assert.NotEmpty(oldestRows);
            }
            if (oldest is not null && !File.Exists(oldest))
            {
                Assert.All(oldestRows, row => Assert.DoesNotContain(row, rows));
                pruned = true;
                break;
            }
        }
        Assert.True(rotated.Count >= 2, "The writer must rotate at least twice.");
        Assert.True(pruned, "The writer must prune the oldest generation.");
    }

    [Fact]
    public void Rows_LongerReplacementWithChangedPrefix_ResetsCommittedRows()
    {
        using var fixture = new Fixture();
        fixture.Append("old");
        var tail = new ConductEventsLogTail();
        var old = Assert.Single(tail.Rows([fixture.Log]));
        var oldLength = new FileInfo(fixture.Log).Length;
        File.Move(fixture.Log, Path.Combine(fixture.Root, "moved-aside.log"));
        fixture.Append("replacement" + new string('z', 700));
        Assert.True(new FileInfo(fixture.Log).Length > oldLength);
        var replacement = tail.Rows([fixture.Log]);
        Assert.Equal(new ConductEventsLogTail().Rows([fixture.Log]), replacement);
        Assert.DoesNotContain(old, replacement);
        Assert.Single(replacement);
        Assert.Equal(new FileInfo(fixture.Log).Length, tail.LastReadBytes);
    }

    [Fact]
    public void Rows_EmptyFileGrowthAndTruncation_UsesPrefixAndLengthBoundaries()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Log, "");
        var tail = new ConductEventsLogTail();
        Assert.Empty(tail.Rows([fixture.Log]));
        fixture.Append("long-original" + new string('x', 600));
        var original = Assert.Single(tail.Rows([fixture.Log]));
        Assert.Equal(new FileInfo(fixture.Log).Length, tail.LastReadBytes);
        File.WriteAllText(fixture.Log, JsonSerializer.Serialize(new { timestamp = fixture.Clock.Now,
            detail = "GOAL goal=12345678 result=short" }) + "\n");
        var shortened = tail.Rows([fixture.Log]);
        Assert.Equal(new ConductEventsLogTail().Rows([fixture.Log]), shortened);
        Assert.DoesNotContain(original, shortened);
        Assert.Equal(new FileInfo(fixture.Log).Length, tail.LastReadBytes);
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pht-" + Guid.NewGuid().ToString("N"));
        internal string Log => Path.Combine(Root, "conduct-events.log");
        internal OwnerConsoleTestClock Clock { get; } = new() { Now = DateTimeOffset.Parse("2026-10-17T00:06:00Z") };
        internal ConductEventLogWriter Writer { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Writer = new ConductEventLogWriter(Log, maxBytes: 2048, utcNow: () => Clock.Now, rotatedGenerationCount: 2);
        }

        internal void Append(string value)
        {
            Clock.Now = Clock.Now.AddSeconds(1);
            Writer.Append("goal", null, "GOAL goal=12345678 result=" + value);
        }

        internal string[] Paths() => Directory.GetFiles(Root, "conduct-events-*.log")
            .OrderBy(path => path, StringComparer.Ordinal).Concat(File.Exists(Log) ? new[] { Log } : []).ToArray();

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
