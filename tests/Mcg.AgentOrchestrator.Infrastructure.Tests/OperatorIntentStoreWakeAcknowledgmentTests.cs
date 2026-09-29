using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorIntentStoreWakeAcknowledgmentTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false, "IOException")]
    [Xunit.InlineData(true, "UnauthorizedAccessException")]
    public async Task CompletionRemainsTerminalWhenWakeRemovalIsDeferred(bool unauthorized, string reason)
    {
        var root = CreateTempDirectory();
        try
        {
            var logs = Path.Combine(root, "logs");
            var lines = new List<string>();
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), logs)
            {
                RemoveWakeFile = _ => throw (unauthorized
                    ? new UnauthorizedAccessException("injected")
                    : new IOException("injected")),
                EmitDiagnostic = lines.Add
            };
            var intent = CreateIntent();
            await store.EnqueueAsync(intent);
            Xunit.Assert.NotNull(await store.ClaimNextAsync(intent.GoalId, "owner-a"));

            await store.CompleteAsync(intent.Id, "owner-a", OperatorIntentStatus.Applied, "Applied in test.", DateTimeOffset.UtcNow);

            var stored = await store.GetAsync(intent.Id);
            Xunit.Assert.NotNull(stored);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied, stored.Status);
            Xunit.Assert.Equal("Applied in test.", stored.Outcome);
            Xunit.Assert.True(File.Exists(Path.Combine(logs, intent.Id + SqliteOperatorIntentStore.WakeFileSuffix)));
            Xunit.Assert.Equal($"OPERATOR_INTENT_WAKE_ACK_DEFERRED id={intent.Id} reason={reason}", Xunit.Assert.Single(lines));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task UnexpectedRemovalFailureStillThrowsAfterTerminalUpdate()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), Path.Combine(root, "logs"))
            {
                RemoveWakeFile = _ => throw new InvalidOperationException("injected")
            };
            var intent = CreateIntent();
            await store.EnqueueAsync(intent);
            Xunit.Assert.NotNull(await store.ClaimNextAsync(intent.GoalId, "owner-a"));

            await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CompleteAsync(intent.Id, "owner-a", OperatorIntentStatus.Rejected, "Rejected in test.", DateTimeOffset.UtcNow));
            var stored = await store.GetAsync(intent.Id);
            Xunit.Assert.Equal(OperatorIntentStatus.Rejected, stored!.Status);
            Xunit.Assert.Equal("Rejected in test.", stored.Outcome);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task DefaultRemovalDeletesWakeFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var logs = Path.Combine(root, "logs");
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), logs);
            var intent = CreateIntent();
            await store.EnqueueAsync(intent);
            Xunit.Assert.NotNull(await store.ClaimNextAsync(intent.GoalId, "owner-a"));

            await store.CompleteAsync(intent.Id, "owner-a", OperatorIntentStatus.Applied, "Applied.", DateTimeOffset.UtcNow);

            Xunit.Assert.False(File.Exists(Path.Combine(logs, intent.Id + SqliteOperatorIntentStore.WakeFileSuffix)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static OperatorIntentRecord CreateIntent() => new(
        "wake-intent", "wake-key", OperatorIntentVerbs.VerifyManual, "goal-one", "task-one", "{}", [],
        "operator", "dashboard", "dashboard-operator-control", DateTimeOffset.UtcNow);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "operator-intent-wake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
