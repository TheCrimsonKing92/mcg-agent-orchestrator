using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its workspace, source files and change-stream mutex.
public sealed class GoalLifecycleChangeSinkTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-lifecycle-changes-");
    private static readonly GoalId Goal = new("0123456789abcdef0123456789abcdef");
    private static readonly TaskId Task = new("task-1");
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
    private const string Secret = "SECRET-MESSAGE-TEXT";
    private OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(_root.FullName);
    private string Changes => Path.Combine(Workspace.LogDirectory, ChangeStreamWriter.FileName);
    private GoalLifecycleEventWriter CreateWriter(Action<GoalLifecycleCommit>? callback) =>
        new(Workspace.GoalLifecycleEventsDirectory, new FixedClock(), committed: callback);

    [Theory]
    [InlineData("TaskDispatched")]
    [InlineData("TaskCompleted")]
    [InlineData("TaskVerified")]
    [InlineData("TaskFailed")]
    [InlineData("HumanInputReceived")]
    [InlineData("HumanInputSuperseded")]
    [InlineData("AcceptanceResult")]
    [InlineData("TaskRetried")]
    public void Append_StageOrIntentEmitsOneStructuralRecord(string eventType)
    {
        var writer = CreateWriter(GoalLifecycleChangeSink.Create(Workspace));
        if (eventType == "TaskDispatched")
            writer.AppendTaskDispatched(Goal, Task, AgentRole.Developer, Secret);
        else if (eventType == "AcceptanceResult")
            writer.AppendAcceptanceResult(Goal, false, new[] { Secret });
        else
        {
            var kind = eventType == "TaskVerified" ? ProgressKind.TaskVerificationRecorded : Enum.Parse<ProgressKind>(eventType);
            var intent = eventType == "TaskRetried"
                ? new OperatorIntentAppliedPayload("intent-1", "arbitrary-verb", Task.Value, Secret, "test", null) : null;
            writer.AppendTimelineEvent(new ProgressEvent(Goal, Task, kind, Secret, Timestamp, OperatorIntentApplied: intent));
        }

        var line = Assert.Single(File.ReadAllLines(Changes));
        Assert.True(ChangeStreamRecord.TryParse(line, out var record));
        Assert.NotNull(record);
        Assert.Equal(ChangeStreamRecord.GoalStage, record.ChangeKind);
        Assert.Equal("goal-lifecycle", record.SourceEventKind);
        Assert.Equal(Goal.Value, record.GoalId);
        Assert.Equal(Timestamp, record.Timestamp);
        var expected = eventType == "AcceptanceResult" ? eventType : $"{eventType} task={Task.Value}";
        if (eventType == "TaskDispatched") expected += " role=Developer";
        Assert.Equal(expected, record.Detail);
        Assert.DoesNotContain(Secret, record.Detail);
        Assert.Single(File.ReadAllLines(writer.EventFilePath(Goal)));
    }

    [Theory]
    [InlineData("GoalLifecycleDecision")]
    [InlineData("WorkerProgress")]
    [InlineData("GoalCreated")]
    [InlineData("TaskRetried")]
    [InlineData("CriteriaCorrectionIgnored")]
    public void Append_NonStageWithoutIntentDoesNotEmitRecord(string eventType)
    {
        var writer = CreateWriter(GoalLifecycleChangeSink.Create(Workspace));
        switch (eventType)
        {
            case "WorkerProgress": writer.AppendWorkerProgress(Goal, 100, 0, Timestamp); break;
            case "GoalCreated": writer.AppendGoalCreated(Goal, Secret); break;
            case "CriteriaCorrectionIgnored": writer.AppendCriteriaCorrectionIgnored(Goal, null, Secret); break;
            default:
                var kind = eventType == "GoalLifecycleDecision" ? ProgressKind.GoalPolicyDecision : ProgressKind.TaskRetried;
                writer.AppendTimelineEvent(new ProgressEvent(Goal, Task, kind, Secret, Timestamp));
                break;
        }
        using var source = JsonDocument.Parse(Assert.Single(File.ReadAllLines(writer.EventFilePath(Goal))));
        Assert.Equal(eventType, source.RootElement.GetProperty("eventType").GetString());
        Assert.False(File.Exists(Changes));
    }

    [Fact]
    public void Append_CallbackObservesCommittedLineOnceWithMatchingFields()
    {
        var commits = new List<GoalLifecycleCommit>();
        var path = Path.Combine(Workspace.GoalLifecycleEventsDirectory, $"{Goal.Value}.jsonl");
        var writer = CreateWriter(commit =>
        {
            Assert.Single(File.ReadAllLines(path));
            commits.Add(commit);
        });
        writer.AppendTaskDispatched(Goal, Task, AgentRole.Developer, Secret);
        Assert.Equal(new GoalLifecycleCommit(Goal, "TaskDispatched", Timestamp, Task.Value, "Developer", false),
            Assert.Single(commits));
    }

    [Fact]
    public void Append_IoCallbackFailurePreservesCommitAndCursor()
    {
        var calls = 0;
        var writer = CreateWriter(_ => { calls++; throw new IOException("sink unavailable"); });
        writer.AppendGoalCreated(Goal, Secret);
        writer.AppendAcceptanceResult(Goal, true, Array.Empty<string>());
        Assert.Equal(2, calls);
        AssertCursors(writer, 0, 1);
    }

    [Fact]
    public void Append_NonIoCallbackFailurePropagatesAfterCommitAndCursorAdvance()
    {
        var calls = 0;
        var writer = CreateWriter(_ => { calls++; throw new InvalidOperationException("invalid sink"); });
        Assert.Throws<InvalidOperationException>(() => writer.AppendGoalCreated(Goal, Secret));
        Assert.Throws<InvalidOperationException>(() => writer.AppendAcceptanceResult(Goal, true, Array.Empty<string>()));
        Assert.Equal(2, calls);
        AssertCursors(writer, 0, 1);
    }

    [Fact]
    public void Append_SourceWriteFailureDoesNotInvokeCallback()
    {
        var calls = 0;
        var writer = CreateWriter(_ => calls++);
        Directory.CreateDirectory(writer.EventFilePath(Goal));
        Assert.Throws<UnauthorizedAccessException>(() => writer.AppendGoalCreated(Goal, Secret));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Append_NullCallbackPreservesSourceBytesWithoutStream()
    {
        var plain = CreateWriter(null);
        plain.AppendTaskDispatched(Goal, Task, AgentRole.Developer, Secret);
        var original = File.ReadAllBytes(plain.EventFilePath(Goal));
        Assert.False(File.Exists(Changes));
        File.Delete(plain.EventFilePath(Goal));
        var wired = CreateWriter(GoalLifecycleChangeSink.Create(Workspace));
        wired.AppendTaskDispatched(Goal, Task, AgentRole.Developer, Secret);
        Assert.Equal(original, File.ReadAllBytes(wired.EventFilePath(Goal)));
        Assert.Single(File.ReadAllLines(Changes));
    }

    [Fact]
    public void Append_StreamAccessFailurePreservesSourceCommit()
    {
        Directory.CreateDirectory(Changes);
        var writer = CreateWriter(GoalLifecycleChangeSink.Create(Workspace));
        writer.AppendTaskDispatched(Goal, Task, AgentRole.Developer, Secret);
        Assert.Single(File.ReadAllLines(writer.EventFilePath(Goal)));
    }

    private static void AssertCursors(GoalLifecycleEventWriter writer, params int[] expected)
    {
        var cursors = File.ReadAllLines(writer.EventFilePath(Goal)).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("cursor").GetInt32();
        });
        Assert.Equal(expected, cursors);
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Timestamp; }
    public void Dispose() => _root.Delete(true);
}
