using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalIntakeRequestStoreTests
{
    [Xunit.Fact]
    public void Reserve_same_payload_replays_original_record()
    {
        var path = CreateDatabase();
        var store = new GoalIntakeRequestStore(path);

        var first = store.Reserve("case-sensitive-key", "fingerprint-a");
        var replay = store.Reserve("case-sensitive-key", "fingerprint-a");

        Xunit.Assert.Equal(GoalIntakeReservationKind.Acquired, first.Kind);
        Xunit.Assert.Equal(GoalIntakeReservationKind.Replay, replay.Kind);
        Xunit.Assert.Equal(first.Record, replay.Record);
        Xunit.Assert.Equal(GoalIntakeRequestStates.StillCommitting, replay.Record.State);
    }

    [Xunit.Fact]
    public void Reserve_changed_payload_fails_closed_without_objective()
    {
        var path = CreateDatabase();
        var store = new GoalIntakeRequestStore(path);
        _ = store.Reserve("conflict-key", "fingerprint-a");

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            store.Reserve("conflict-key", "fingerprint-b"));

        Xunit.Assert.Contains("GOAL_INTAKE_PAYLOAD_CONFLICT", error.Message);
        Xunit.Assert.Contains("existingState=still-committing", error.Message);
        Xunit.Assert.DoesNotContain("fingerprint-a", error.Message);
        Xunit.Assert.DoesNotContain("fingerprint-b", error.Message);
    }

    [Xunit.Fact]
    public async Task Reserve_concurrent_same_key_has_one_winner()
    {
        var path = CreateDatabase();
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);

        Task<GoalIntakeReservation> Start() => Task.Run(() =>
        {
            ready.Signal();
            release.Wait();
            return new GoalIntakeRequestStore(path).Reserve("concurrent-key", "same-fingerprint");
        });

        var first = Start();
        var second = Start();
        try
        {
            Xunit.Assert.True(ready.Wait(TimeSpan.FromSeconds(15)), "Both reservation attempts did not reach the event gate.");
            release.Set();
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));

            Xunit.Assert.Single(results, result => result.Kind == GoalIntakeReservationKind.Acquired);
            Xunit.Assert.Single(results, result => result.Kind == GoalIntakeReservationKind.Replay);
            Xunit.Assert.Equal(results[0].Record, results[1].Record);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [Xunit.Fact]
    public async Task MarkCreated_joins_goal_transaction_and_rolls_back_together()
    {
        var path = CreateDatabase();
        var store = new GoalIntakeRequestStore(path);
        _ = store.Reserve("atomic-key", "atomic-fingerprint");
        var repository = new SqliteOrchestratorStateRepository(path);

        await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.TransactWithOutboxAsync<bool>((kernel, cancellationToken) =>
            {
                var goal = kernel.CreateGoal(
                    "Atomic rollback",
                    [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
                _ = cancellationToken;
                store.MarkCreated("atomic-key", "atomic-fingerprint", goal.Id.Value);
                throw new InvalidOperationException("force rollback");
            }));

        Xunit.Assert.Empty((await repository.LoadAsync()).Goals);
        Xunit.Assert.Equal(
            GoalIntakeRequestStates.StillCommitting,
            store.Get("atomic-key")!.State);
    }

    [Xunit.Fact]
    public async Task MarkCreated_commits_one_goal_and_terminal_receipt()
    {
        var path = CreateDatabase();
        var store = new GoalIntakeRequestStore(path);
        _ = store.Reserve("created-key", "created-fingerprint");
        var repository = new SqliteOrchestratorStateRepository(path);

        var goalId = await repository.TransactWithOutboxAsync<string>((kernel, cancellationToken) =>
        {
            var goal = kernel.CreateGoal(
                "Atomic commit",
                [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            _ = cancellationToken;
            store.MarkCreated("created-key", "created-fingerprint", goal.Id.Value);
            return Task.FromResult((
                ShouldSave: true,
                Result: goal.Id.Value,
                OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
        });

        var receipt = store.Get("created-key")!;
        Xunit.Assert.Equal(GoalIntakeRequestStates.Created, receipt.State);
        Xunit.Assert.Equal(goalId, receipt.GoalId);
        Xunit.Assert.Single((await repository.LoadAsync()).Goals);
    }

    private static string CreateDatabase()
    {
        var path = Path.Combine(CreateTempDirectory(), "state.db");
        _ = StateDbMigrations.EnsureUpToDate(path);
        return path;
    }
}
