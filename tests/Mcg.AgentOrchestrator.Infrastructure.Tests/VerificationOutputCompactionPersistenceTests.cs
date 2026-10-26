using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;

// Parallel-safe: each test owns a temporary workspace and non-pooled database connections.
public sealed class VerificationOutputCompactionPersistenceTests
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task TwoRounds_SaveLoad_StoresEachPreviewOnceAndRestoresText()
    {
        const string firstMarker = "earlier-round-stdout";
        const string latestMarker = "latest-round-stdout";
        var firstOutput = TextWithMarker(firstMarker, 2_000);
        var latestOutput = TextWithMarker(latestMarker, 2_000);
        var firstError = TextWithMarker("earlier-round-stderr", 300);
        var latestError = TextWithMarker("latest-round-stderr", 300);
        var goal = CreateGoal(
            Verification(firstOutput, firstError, CompletedAt),
            Verification(latestOutput, latestError, CompletedAt.AddMinutes(1)));

        await WithRepository(async (repository, databasePath) =>
        {
            await repository.SaveGoalSnapshotsAsync([goal]);
            var json = await ReadSnapshotJson(databasePath, goal.Id);
            Assert.Equal(1, CountOccurrences(json, firstMarker));
            Assert.Equal(2, CountOccurrences(json, latestMarker));

            var loaded = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(new GoalId(goal.Id)));
            var task = Assert.Single(loaded.Tasks);
            AssertCompacted(task.LastVerification!);
            Assert.Equal(2, task.VerificationHistory!.Count);
            Assert.All(task.VerificationHistory, AssertCompacted);

            var restored = RestoreTask(loaded);
            Assert.Equal(latestOutput, restored.LastVerification!.AuthoritativeStandardOutput);
            Assert.Equal(latestError, restored.LastVerification.AuthoritativeStandardError);
            Assert.Collection(restored.VerificationHistory,
                record =>
                {
                    Assert.Equal(firstOutput, record.AuthoritativeStandardOutput);
                    Assert.Equal(firstError, record.AuthoritativeStandardError);
                },
                record =>
                {
                    Assert.Equal(latestOutput, record.AuthoritativeStandardOutput);
                    Assert.Equal(latestError, record.AuthoritativeStandardError);
                });
        });
    }

    [Fact]
    public async Task LongOutput_SaveLoad_KeepsFullTextAndMiddleMarker()
    {
        const string middleMarker = "persisted-middle-marker";
        var output = TextWithMarker(middleMarker, VerificationTextBounds.BoundThreshold + 5_000);
        var goal = CreateGoal(Verification(output, string.Empty, CompletedAt));
        Assert.DoesNotContain(middleMarker, Assert.Single(goal.Tasks).LastVerification!.StandardOutput);

        await WithRepository(async (repository, databasePath) =>
        {
            await repository.SaveGoalSnapshotsAsync([goal]);
            var json = await ReadSnapshotJson(databasePath, goal.Id);
            Assert.Contains(middleMarker, json);

            var loaded = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(new GoalId(goal.Id)));
            var task = Assert.Single(loaded.Tasks);
            foreach (var stored in task.VerificationHistory!.Prepend(task.LastVerification!))
            {
                Assert.Equal(output, stored.AuthoritativeStandardOutput);
                Assert.Null(stored.AuthoritativeStandardOutputUnavailableReason);
            }

            var restored = RestoreTask(loaded);
            Assert.Equal(output, restored.LastVerification!.AuthoritativeStandardOutput);
            Assert.Equal(output, Assert.Single(restored.VerificationHistory).AuthoritativeStandardOutput);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyRow_MergeSave_PreservesBaselineAndNewVerification(bool rejectConflict)
    {
        var goal = CreateGoal(Verification("legacy-output", "legacy-error", CompletedAt));
        var task = Assert.Single(goal.Tasks);
        var legacy = task.LastVerification! with
        {
            AuthoritativeStandardOutput = "legacy-output",
            AuthoritativeStandardError = "legacy-error",
            AuthoritativeStandardOutputUnavailableReason = null,
            AuthoritativeStandardErrorUnavailableReason = null
        };
        goal = goal with { Tasks = [task with { LastVerification = legacy, VerificationHistory = [legacy] }] };

        await WithRepository(async (repository, databasePath) =>
        {
            await repository.SaveGoalSnapshotsAsync([goal]);
            var rawJson = await ReadSnapshotJson(databasePath, goal.Id);
            var loaded = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(new GoalId(goal.Id)));
            var kernel = AgentOrchestratorKernel.FromSnapshot(new([loaded], []));
            var baseline = Assert.Single(kernel.ExportSnapshot().Goals);
            Assert.Equal(rawJson, JsonSerializer.Serialize(baseline, JsonOptions));

            kernel.RecordTaskVerification(new GoalId(goal.Id), new TaskId(task.Id),
                Verification("new-output", "new-error", CompletedAt.AddMinutes(1)));
            var current = Assert.Single(kernel.ExportSnapshot().Goals);
            if (!rejectConflict)
            {
                // Force an actual merge with an independent store-owned change.
                await repository.SaveGoalSnapshotsAsync([loaded with { Objective = "advanced-objective" }]);
            }

            var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync(
                [new GoalSnapshotSaveRequest(baseline, current, RejectConflict: rejectConflict)]));
            Assert.Equal(rejectConflict ? GoalSnapshotSaveDisposition.Saved : GoalSnapshotSaveDisposition.Merged,
                result.Disposition);
            var saved = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(new GoalId(goal.Id)));
            if (!rejectConflict)
                Assert.Equal("advanced-objective", saved.Objective);
            var savedTask = Assert.Single(saved.Tasks);
            Assert.Equal(2, savedTask.VerificationHistory!.Count);
            Assert.Equal(JsonSerializer.Serialize(legacy, JsonOptions),
                JsonSerializer.Serialize(savedTask.VerificationHistory[0], JsonOptions));
            AssertCompacted(savedTask.LastVerification!);
            AssertCompacted(savedTask.VerificationHistory[1]);
            var restored = RestoreTask(saved);
            Assert.Equal("new-output", restored.LastVerification!.AuthoritativeStandardOutput);
            Assert.Equal("new-error", restored.LastVerification.AuthoritativeStandardError);
            Assert.Equal("legacy-output", restored.VerificationHistory[0].AuthoritativeStandardOutput);
        });
    }

    private static TaskVerificationRecord Verification(string output, string error, DateTimeOffset at) =>
        new("verify", "C:\\repo", 0, output, error, at, FullStandardOutput: output, FullStandardError: error);

    private static GoalSnapshot CreateGoal(params TaskVerificationRecord[] records)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        var goal = kernel.CreateGoal("Persist verification text", [task]);
        foreach (var record in records)
            kernel.RecordTaskVerification(goal.Id, task.Id, record);
        Assert.NotEqual(GoalStatus.WaitingForHuman, goal.Status);
        return Assert.Single(kernel.ExportSnapshot().Goals);
    }

    private static TaskSpec RestoreTask(GoalSnapshot goal) =>
        Assert.Single(Assert.Single(AgentOrchestratorKernel.FromSnapshot(new([goal], [])).Goals).Tasks);

    private static void AssertCompacted(TaskVerificationSnapshot record)
    {
        Assert.Null(record.AuthoritativeStandardOutput);
        Assert.Null(record.AuthoritativeStandardError);
        Assert.Equal(VerificationSnapshotText.StoredAsBoundedText, record.AuthoritativeStandardOutputUnavailableReason);
        Assert.Equal(VerificationSnapshotText.StoredAsBoundedText, record.AuthoritativeStandardErrorUnavailableReason);
    }

    private static async Task WithRepository(Func<SqliteOrchestratorStateRepository, string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-compaction-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await action(repository, workspace.SqliteStatePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> ReadSnapshotJson(string databasePath, string goalId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
        command.Parameters.AddWithValue("$id", goalId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static int CountOccurrences(string text, string marker)
    {
        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(marker, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += marker.Length;
        }
        return count;
    }

    private static string TextWithMarker(string marker, int length)
    {
        var head = "head-marker\n" + new string('a', length / 2 - "head-marker\n".Length);
        var middle = "\n" + marker + "\n";
        const string tail = "\ntail-marker";
        return head + middle + new string('b', length - head.Length - middle.Length - tail.Length) + tail;
    }
}
