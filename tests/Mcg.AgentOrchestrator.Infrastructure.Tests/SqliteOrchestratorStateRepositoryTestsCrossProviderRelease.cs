using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class SqliteOrchestratorStateRepositoryTestsCrossProviderRelease : IDisposable
{
    private readonly string _directory = CreateTempDirectory();

    [Xunit.Fact]
    public async Task SaveAndLoad_ReleasedDeferral_PreservesBoundary()
    {
        var (kernel, goal, task, clock) = DeferredTester();
        kernel.ReassignTaskAgent(goal.Id, task.Id, Tester("Anthropic"));
        AssertReleased(task, clock.UtcNow);
        var snapshotRestored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        AssertReleased(snapshotRestored.GetTask(goal.Id, task.Id), clock.UtcNow);
        var repository = Repository();

        await repository.SaveAsync(kernel);
        var restored = await repository.LoadAsync();

        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Single(restoredTask.VerificationHistory);
        AssertReleased(restoredTask, clock.UtcNow);
    }

    [Xunit.Fact]
    public async Task Load_LegacySnapshotWithoutBoundary_KeepsHistoricalDeferral()
    {
        var (kernel, goal, task, clock) = DeferredTester();
        kernel.ReassignTaskAgent(goal.Id, task.Id, Tester("Anthropic"));
        var repository = Repository();
        await repository.SaveAsync(kernel);

        // Remove the actual JSON property, rather than replacing it with an explicit null.
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
            read.Parameters.AddWithValue("$id", goal.Id.Value);
            var json = Assert.IsType<string>(await read.ExecuteScalarAsync());
            var snapshot = JsonNode.Parse(json)!.AsObject();
            var savedTask = snapshot["Tasks"]!.AsArray().Single()!.AsObject();
            Assert.True(savedTask.Remove("SubscriptionDeferralReleasedAt"));
            using var write = connection.CreateCommand();
            write.CommandText = "UPDATE goals SET snapshot_json = $json WHERE id = $id";
            write.Parameters.AddWithValue("$json", snapshot.ToJsonString());
            write.Parameters.AddWithValue("$id", goal.Id.Value);
            Assert.Equal(1, await write.ExecuteNonQueryAsync());
        }

        var restored = await repository.LoadAsync();

        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Null(restoredTask.SubscriptionDeferralReleasedAt);
        Assert.Null(restoredTask.SubscriptionRetryAfter);
        Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(
            restoredTask, clock.UtcNow, out var retryAfter));
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 16, 58, 0, TimeSpan.Zero), retryAfter);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task TickMerge_ReleasedDeferral_PreservesBoundary(bool releaseInStore)
    {
        var (kernel, goal, task, clock) = DeferredTester();
        var repository = Repository();
        await repository.SaveAsync(kernel);
        var baseline = kernel.ExportGoalSnapshot(goal.Id);
        var tick = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([baseline], []), clock);
        if (!releaseInStore)
        {
            tick.ReassignTaskAgent(goal.Id, task.Id, Tester("Anthropic"));
        }
        else
        {
            tick.RecordTaskNote(goal.Id, task.Id, "Tick note");
        }

        await repository.TransactGoalAsync<bool>(goal.Id, (stored, _) =>
        {
            var operatorKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!], []), clock);
            if (releaseInStore)
            {
                operatorKernel.ReassignTaskAgent(goal.Id, task.Id, Tester("Anthropic"));
            }
            else
            {
                operatorKernel.RecordTaskNote(goal.Id, task.Id, "Operator note");
            }
            return Task.FromResult((true, operatorKernel.ExportGoalSnapshot(goal.Id), true));
        });

        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, tick.ExportGoalSnapshot(goal.Id))]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var restored = await repository.LoadAsync();
        AssertReleased(restored.GetTask(goal.Id, task.Id), clock.UtcNow);
        Assert.Single(restored.GetGoal(goal.Id).Timeline.Where(evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Released subscription deferral", StringComparison.Ordinal)));
    }

    private string DatabasePath => Path.Combine(_directory, "state.db");

    private SqliteOrchestratorStateRepository Repository()
    {
        _ = StateDbMigrations.EnsureUpToDate(DatabasePath);
        return new SqliteOrchestratorStateRepository(DatabasePath);
    }

    private static (AgentOrchestratorKernel, Goal, TaskSpec, FakeClock) DeferredTester()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Persist subscription release", [new TaskSpec(TaskId.New(), "Test", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, [Tester("OpenAI")]);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", clock.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli, ProviderName: "OpenAI"));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec", "C:\\repo", 1, string.Empty,
            "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
            clock.UtcNow, DispatchStartedAt: clock.UtcNow));
        Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, clock.UtcNow, out _));
        return (kernel, goal, task, clock);
    }

    private static AgentDefinition Tester(string provider) => new(
        AgentId.New(), "Tester", AgentRole.Tester,
        new ModelProfile(provider, "test-model", ModelCapability.Text, SubscriptionMode.ApiKey));

    private static void AssertReleased(TaskSpec task, DateTimeOffset releasedAt)
    {
        Assert.Equal(releasedAt, task.SubscriptionDeferralReleasedAt);
        Assert.Null(task.SubscriptionRetryAfter);
        Assert.False(DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(task, out _));
        Assert.False(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, releasedAt, out _));
    }

    // Fixed clock; SQLite loads use the stored boundary, never the machine's current time.
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
