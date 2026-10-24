using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its intent store in a private temp directory.
public sealed class TaskIntentAdmissionPartialReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-intent-partial-ref-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _at = new(2026, 10, 23, 21, 0, 36, TimeSpan.Zero);

    [Xunit.Fact]
    public void ReferenceRebuiltWithoutGraphFieldsMatchesItsPreparation()
    {
        var (store, prepared) = Prepare();
        // Worker-build consumption rebuilds the reference from the persisted invocation, which has no graph fields.
        var rebuilt = new TaskIntentAdmissionReference(prepared.StorePath, prepared.GoalId, prepared.TaskId, prepared.Version, _at);

        Xunit.Assert.Null(store.RecordSuppression(rebuilt, "worker-build-consume:partial"));
        Xunit.Assert.Null(store.RecordSuppression(prepared, "worker-build-consume:full"));
    }

    [Xunit.Fact]
    public void SuppliedGraphIdentityThatDiffersStillFailsTheBinding()
    {
        var (store, prepared) = Prepare();
        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            store.RecordSuppression(prepared with { GraphIdentity = "graph-other" }, "worker-build-consume:graph"));
        Xunit.Assert.Equal("Task admission version does not match its durable preparation.", error.Message);
    }

    [Xunit.Fact]
    public void RebuiltReferenceWithAnotherVersionStillFailsTheBinding()
    {
        var (store, prepared) = Prepare();
        var stale = new TaskIntentAdmissionReference(prepared.StorePath, prepared.GoalId, prepared.TaskId, prepared.Version + 1, _at);
        var error = Xunit.Assert.Throws<InvalidOperationException>(() => store.RecordSuppression(stale, "worker-build-consume:stale"));
        Xunit.Assert.Equal("Task admission version does not match its durable preparation.", error.Message);
    }

    private (SqliteOperatorIntentStore Store, TaskIntentAdmissionReference Prepared) Prepare()
    {
        Directory.CreateDirectory(_root);
        var storePath = Path.GetFullPath(Path.Combine(_root, SqliteOperatorIntentStore.DatabaseFileName));
        var store = new SqliteOperatorIntentStore(storePath, Path.Combine(_root, "logs"), utcNow: () => _at);
        var captured = store.CaptureAdmission("goal-partial-reference", "task-partial-reference",
            "graph-identity-1", Path.Combine(_root, "state.db"));
        store.BindPreparedAdmission(captured, _at);
        var prepared = store.GetPreparedAdmission(captured.GoalId, captured.TaskId, _at);
        Xunit.Assert.Equal("graph-identity-1", prepared.GraphIdentity);
        Xunit.Assert.NotNull(prepared.StatePath);
        return (store, prepared);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
