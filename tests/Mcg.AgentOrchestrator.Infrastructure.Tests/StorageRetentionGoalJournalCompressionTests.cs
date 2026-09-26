using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class StorageRetentionGoalJournalCompressionTests
{
    [Xunit.Fact]
    public void SelectedTerminalJournalIsCompressedAndStillReadable()
    {
        using var fixture = new RetentionReclaimFixture();
        var completed = WriteJournal(fixture, RetentionReclaimFixture.CompletedId);
        var active = WriteJournal(fixture, RetentionReclaimFixture.ActiveId);
        var failed = WriteJournal(fixture, RetentionReclaimFixture.FailedId);
        var before = GoalOperationJournal.Read(fixture.ExecutionDirectory,
            new GoalId(RetentionReclaimFixture.CompletedId)).Entries;
        Assert.NotEmpty(before);

        var result = fixture.Run(null,
            fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed),
            fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active),
            fixture.Goal(RetentionReclaimFixture.FailedId, GoalStatus.Failed));

        Assert.False(File.Exists(completed));
        Assert.True(File.Exists(completed + ".gz"));
        Assert.True(File.Exists(active));
        Assert.True(File.Exists(failed));
        Assert.Equal(before, GoalOperationJournal.Read(fixture.ExecutionDirectory,
            new GoalId(RetentionReclaimFixture.CompletedId)).Entries);
        Assert.Equal(completed + ".gz", GoalOperationJournal.ResolveReadPath(fixture.ExecutionDirectory,
            new GoalId(RetentionReclaimFixture.CompletedId)));
        Assert.Contains(new GoalId(RetentionReclaimFixture.CompletedId),
            GoalOperationJournal.ReadAll(fixture.ExecutionDirectory).Keys);
        Assert.Contains(result.Decisions, decision => decision.Path == completed &&
            decision.Action == EvidenceRetentionAction.Compressed &&
            decision.Reason == "terminal-goal-journal-compressed");
    }

    private static string WriteJournal(RetentionReclaimFixture fixture, string id)
    {
        var goalId = new GoalId(id);
        var path = GoalOperationJournal.PathFor(fixture.ExecutionDirectory, goalId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(path, JsonSerializer.Serialize(new GoalOperationJournalEntry(
            id + ":test", goalId, "test", GoalOperationStatus.Completed,
            RetentionReclaimFixture.Now.AddDays(-30), "complete"), jsonOptions) + "\n");
        File.SetLastWriteTimeUtc(path, RetentionReclaimFixture.Now.AddDays(-30).UtcDateTime);
        return path;
    }
}
