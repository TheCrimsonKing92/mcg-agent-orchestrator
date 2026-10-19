using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its workspace, event files and change-stream mutex.
public sealed class GoalLifecycleDecisionChangeSinkTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-lifecycle-decisions-");
    private static readonly GoalId Goal = new("0123456789abcdef0123456789abcdef");
    private static readonly TaskId Task = new("task-1");
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
    private const string Secret = "SECRET-MESSAGE-TEXT";
    private OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(_root.FullName);
    private string Changes => Path.Combine(Workspace.LogDirectory, ChangeStreamWriter.FileName);
    private GoalLifecycleEventWriter CreateWriter() => new(Workspace.GoalLifecycleEventsDirectory,
        new FixedClock(), committed: GoalLifecycleChangeSink.Create(Workspace));

    [Theory]
    [InlineData(ProgressKind.HumanInputReceived, "owner-decision-answered", "goal-decision-answered", "HumanInputReceived task=task-1")]
    [InlineData(ProgressKind.HumanInputSuperseded, "owner-decision-retired", "goal-decision-retired", "HumanInputSuperseded task=task-1")]
    public void Append_DecisionEmitsOneTypedRecordWithoutMessage(ProgressKind kind, string changeKind, string source, string detail)
    {
        var writer = CreateWriter();
        writer.AppendTimelineEvent(new ProgressEvent(Goal, Task, kind, Secret, Timestamp));

        AssertRecord(writer, changeKind, source, detail);
    }

    [Theory]
    [InlineData("TaskDispatched", "TaskDispatched task=task-1 role=Developer")]
    [InlineData("TaskCompleted", "TaskCompleted task=task-1")]
    [InlineData("TaskVerified", "TaskVerified task=task-1")]
    [InlineData("TaskFailed", "TaskFailed task=task-1")]
    [InlineData("AcceptanceResult", "AcceptanceResult")]
    [InlineData("TaskRetried", "TaskRetried task=task-1")]
    public void Append_OtherStagesKeepSourceAndDetail(string eventType, string detail)
    {
        var writer = CreateWriter();
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

        AssertRecord(writer, "goal-stage", "goal-lifecycle", detail);
    }

    private void AssertRecord(GoalLifecycleEventWriter writer, string kind, string source, string detail)
    {
        Assert.Single(File.ReadAllLines(writer.EventFilePath(Goal)));
        Assert.True(ChangeStreamRecord.TryParse(Assert.Single(File.ReadAllLines(Changes)), out var record));
        Assert.NotNull(record);
        Assert.Equal(kind, record.ChangeKind);
        Assert.Equal(source, record.SourceEventKind);
        Assert.Equal(Goal.Value, record.GoalId);
        Assert.Equal(Timestamp, record.Timestamp);
        Assert.Equal(detail, record.Detail);
        Assert.DoesNotContain(Secret, record.Detail);
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Timestamp; }
    public void Dispose() => _root.Delete(true);
}
