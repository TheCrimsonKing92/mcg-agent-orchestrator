using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its workspace and both file-derived sequence writers.
public sealed class ConductEventLogWriterStageKindTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-conduct-stage-");
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-10-10T12:00:00Z");

    [Fact]
    public void Append_InterleavedStageSourcesShareContiguousSequence()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(_root.FullName);
        var conduct = new ConductEventLogWriter(workspace.ConductEventsLogPath, utcNow: () => Timestamp);
        var lifecycle = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, new FixedClock(),
            committed: GoalLifecycleChangeSink.Create(workspace));
        var goal = new GoalId("0123456789abcdef0123456789abcdef");
        conduct.Append("acceptance", goal.Value, "passed");
        lifecycle.AppendTimelineEvent(new ProgressEvent(goal, new TaskId("task-1"), ProgressKind.TaskCompleted, "secret", Timestamp));
        conduct.Append("gate-progress", goal.Value, "heartbeat");
        conduct.Append("acceptance-cohort", goal.Value, "passed");
        lifecycle.AppendAcceptanceResult(goal, true, Array.Empty<string>());
        conduct.Append("acceptance", null, "no goal");
        conduct.Append("acceptance-cohort", " ", "blank goal");

        var records = File.ReadAllLines(Path.Combine(workspace.LogDirectory, ChangeStreamWriter.FileName)).Select(line =>
        {
            Assert.True(ChangeStreamRecord.TryParse(line, out var record));
            return record!;
        }).ToArray();
        Assert.Equal(new long[] { 1, 2, 3, 4 }, records.Select(record => record.Sequence));
        Assert.Equal(4, records.Select(record => record.Sequence).Distinct().Count());
        Assert.Equal(new[] { "acceptance", "goal-lifecycle", "acceptance-cohort", "goal-lifecycle" },
            records.Select(record => record.SourceEventKind));
        Assert.All(records, record =>
        {
            Assert.Equal(ChangeStreamRecord.GoalStage, record.ChangeKind);
            Assert.Equal(goal.Value, record.GoalId);
            Assert.Equal(Timestamp, record.Timestamp);
        });
        Assert.DoesNotContain(records, record => record.SourceEventKind == "gate-progress");
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Timestamp; }
    public void Dispose() => _root.Delete(true);
}
