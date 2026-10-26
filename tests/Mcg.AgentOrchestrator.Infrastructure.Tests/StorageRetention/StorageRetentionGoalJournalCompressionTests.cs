using System.IO.Compression;
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
        Assert.Equal(before, GoalOperationJournal.ReadActive(fixture.ExecutionDirectory,
            new GoalId(RetentionReclaimFixture.CompletedId)).Entries);
        Assert.Equal(before, GoalOperationJournal.ReadStrict(fixture.ExecutionDirectory,
            new GoalId(RetentionReclaimFixture.CompletedId)).Entries);
        Assert.Equal(completed + ".gz", GoalOperationJournal.ResolveReadPath(fixture.ExecutionDirectory,
            new GoalId(RetentionReclaimFixture.CompletedId)));
        Assert.Contains(new GoalId(RetentionReclaimFixture.CompletedId),
            GoalOperationJournal.ReadAll(fixture.ExecutionDirectory).Keys);
        Assert.Contains(result.Decisions, decision => decision.Path == completed &&
            decision.Action == EvidenceRetentionAction.Compressed &&
            decision.Reason == "terminal-goal-journal-compressed");
    }

    [Xunit.Fact]
    public void InvalidCompressedSiblingDefersThatJournalAndContinuesToNext()
    {
        using var fixture = new RetentionReclaimFixture();
        var invalid = WriteJournal(fixture, RetentionReclaimFixture.CompletedId);
        var valid = WriteJournal(fixture, RetentionReclaimFixture.ActiveId);
        File.WriteAllText(invalid + ".gz", "invalid gzip");

        var result = fixture.Run(null,
            fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed),
            fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Cancelled));

        Assert.True(File.Exists(invalid));
        Assert.False(File.Exists(valid));
        Assert.True(File.Exists(valid + ".gz"));
        Assert.False(result.Failed);
        Assert.Contains(result.Decisions, decision => decision.Path == invalid &&
            decision.Reason == "journal-compression-failed" &&
            decision.FailureExceptionType == nameof(InvalidDataException));
        Assert.Single(GoalOperationJournal.Read(fixture.ExecutionDirectory,
            new GoalId(RetentionReclaimFixture.CompletedId)).Entries);
    }

    [Xunit.Fact]
    public void CompressedJournalReadUsesArchiveOnlyAsFallback()
    {
        using var fixture = new RetentionReclaimFixture();
        WriteJournal(fixture, RetentionReclaimFixture.CompletedId);
        fixture.Run(null, fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed));
        var goalId = new GoalId(RetentionReclaimFixture.CompletedId);
        var archive = GoalOperationJournal.ArchivePathFor(fixture.ExecutionDirectory, goalId);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(archive, JsonSerializer.Serialize(new GoalOperationJournalEntry(
            goalId.Value + ":archive", goalId, "archive", GoalOperationStatus.Completed,
            RetentionReclaimFixture.Now.AddDays(-40), "earlier"), options) + "\n");

        Assert.Single(GoalOperationJournal.Read(fixture.ExecutionDirectory, goalId).Entries);
        Assert.Single(GoalOperationJournal.ReadActive(fixture.ExecutionDirectory, goalId).Entries);
        File.Delete(GoalOperationJournal.PathFor(fixture.ExecutionDirectory, goalId) + ".gz");
        Assert.Single(GoalOperationJournal.Read(fixture.ExecutionDirectory, goalId).Entries);
        Assert.False(GoalOperationJournal.ReadActive(fixture.ExecutionDirectory, goalId).HasEntries);
    }

    [Xunit.Fact]
    public void PlainJournalTakesPrecedenceOverEarlierArchive()
    {
        using var fixture = new RetentionReclaimFixture();
        var goalId = new GoalId(RetentionReclaimFixture.CompletedId);
        var plain = WriteJournal(fixture, goalId.Value);
        var archive = GoalOperationJournal.ArchivePathFor(fixture.ExecutionDirectory, goalId);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.Copy(plain, archive);

        var direct = GoalOperationJournal.Read(fixture.ExecutionDirectory, goalId);
        var all = GoalOperationJournal.ReadAll(fixture.ExecutionDirectory)[goalId];

        Assert.Single(direct.Entries);
        Assert.Single(all.Entries);
        Assert.Equal(plain, direct.Path);
    }

    [Xunit.Fact]
    public void ReaderAvoidsDuplicateCrashCopyAndIncludesLaterPlainAppend()
    {
        using var fixture = new RetentionReclaimFixture();
        var path = WriteJournal(fixture, RetentionReclaimFixture.CompletedId);
        fixture.Run(null, fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed));
        var goalId = new GoalId(RetentionReclaimFixture.CompletedId);
        using (var gzip = new GZipStream(File.OpenRead(path + ".gz"), CompressionMode.Decompress))
        using (var plain = File.Create(path))
            gzip.CopyTo(plain);

        Assert.Single(GoalOperationJournal.ReadActive(fixture.ExecutionDirectory, goalId).Entries);

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        File.AppendAllText(path, JsonSerializer.Serialize(new GoalOperationJournalEntry(
            goalId.Value + ":later", goalId, "later", GoalOperationStatus.Completed,
            RetentionReclaimFixture.Now.AddDays(-1), "new"), options) + "\n");

        Assert.Equal(2, GoalOperationJournal.ReadActive(fixture.ExecutionDirectory, goalId).Entries.Count);
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
