using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns its conduct/change logs, pending directory and OS file lock.
public sealed class ConductEventLogWriterChangeStreamTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-conduct-changes-");
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
    private string Log => Path.Combine(_root.FullName, ConductEventLogWriter.CurrentFileName);
    private string Changes => Path.Combine(_root.FullName, ChangeStreamWriter.FileName);

    [Fact]
    public void Append_DirectAndPendingDrainPreserveLegacyBytesAndTypedOrder()
    {
        var clockCalls = 0;
        var writer = new ConductEventLogWriter(Log, utcNow: () => { clockCalls++; return Timestamp; });
        writer.Append("goal-escalation", "g", "owner needed");
        using (new FileStream(Log, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(writer.AppendRequired("goal", "g", "active", eventId: "required-change"));
            Assert.Single(Directory.GetFiles(Path.Combine(_root.FullName,
                ConductEventLogWriter.PendingEventsDirectoryName), "*.jsonl"));
            Assert.Single(File.ReadAllLines(Changes));
        }
        writer.Append("blocked-recheck-heartbeat", "g", "heartbeat");
        writer.Append("acceptance-lease", "g", "lease");

        var golden = new[]
        {
            "{\"timestamp\":\"2026-10-08T12:00:00+00:00\",\"eventKind\":\"goal-escalation\",\"goalId\":\"g\",\"detail\":\"owner needed\",\"operator\":\"decision\"}",
            "{\"timestamp\":\"2026-10-08T12:00:00+00:00\",\"eventKind\":\"goal\",\"goalId\":\"g\",\"detail\":\"active\",\"event_id\":\"required-change\"}",
            "{\"timestamp\":\"2026-10-08T12:00:00+00:00\",\"eventKind\":\"blocked-recheck-heartbeat\",\"goalId\":\"g\",\"detail\":\"heartbeat\"}",
            "{\"timestamp\":\"2026-10-08T12:00:00+00:00\",\"eventKind\":\"acceptance-lease\",\"goalId\":\"g\",\"detail\":\"lease\"}"
        };
        Assert.Equal(Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, golden) + Environment.NewLine), File.ReadAllBytes(Log));
        var records = File.ReadAllLines(Changes).Select(line =>
            JsonSerializer.Deserialize<ChangeStreamRecord>(line, ChangeStreamRecord.JsonOptions)!).ToArray();
        Assert.Equal(new long[] { 1, 2 }, records.Select(record => record.Sequence));
        Assert.Equal(new[] { "goal-escalation", "goal" }, records.Select(record => record.SourceEventKind));
        Assert.Equal(new[] { ChangeStreamRecord.OwnerDecisionRaised, ChangeStreamRecord.GoalTransition },
            records.Select(record => record.ChangeKind));
        Assert.Equal(new[] { "owner needed", "active" }, records.Select(record => record.Detail));
        Assert.All(records, record => Assert.Equal(Timestamp, record.Timestamp));
        Assert.Equal(4, clockCalls);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root.FullName, ConductEventLogWriter.PendingEventsDirectoryName), "*.jsonl"));
        Assert.True(writer.AppendRequired("goal", "g", "active", eventId: "required-change"));
        Assert.Equal(2, File.ReadAllLines(Changes).Length);
        Assert.DoesNotContain("\"sequence\"", File.ReadAllText(Log));
        Assert.DoesNotContain("\"schema\"", File.ReadAllText(Log));
    }

    [Fact]
    public void Append_TypedWriteFailureDoesNotDuplicateRequiredConductLine()
    {
        Directory.CreateDirectory(Changes); // Force a typed-log open failure.
        var writer = new ConductEventLogWriter(Log, utcNow: () => Timestamp);
        Assert.True(writer.AppendRequired("goal", "g", "active", eventId: "one"));
        Assert.True(writer.AppendRequired("goal", "g", "active", eventId: "one"));
        Assert.Single(File.ReadAllLines(Log));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root.FullName, ConductEventLogWriter.PendingEventsDirectoryName), "*.jsonl"));
    }

    public void Dispose() => _root.Delete(true);
}
