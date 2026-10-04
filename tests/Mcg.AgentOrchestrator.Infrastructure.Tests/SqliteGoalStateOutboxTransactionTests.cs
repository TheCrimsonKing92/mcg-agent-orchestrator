using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class SqliteGoalStateOutboxTransactionTests
{
    [Xunit.Fact]
    public async Task GoalAndMessages_CommitTogetherWithOneVersionIncrement()
    {
        using var seed = await Seed.Create();
        var before = await seed.Version();
        var messages = new[] { Message("first"), Message("second") };

        var result = await seed.Repository.TransactGoalStateWithOutboxAsync("test:commit", seed.GoalId,
            (state, _) => Task.FromResult((true, Cancelled(state!), "committed",
                (IReadOnlyList<OrchestratorStateOutboxMessage>)messages)));

        Xunit.Assert.Equal("committed", result);
        Xunit.Assert.Equal(GoalStatus.Cancelled, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        Xunit.Assert.Equal(before + 1, await seed.Version());
        Xunit.Assert.Equal(messages, await seed.Repository.ListOutboxMessagesAsync("test-goal-delivery"));
    }

    [Xunit.Fact]
    public async Task FaultAfterWrites_RollsBackGoalVersionAndEveryMessage()
    {
        using var seed = await Seed.Create();
        var before = await seed.Version();
        var fault = new InvalidOperationException("before COMMIT");
        var repository = new SqliteOrchestratorStateRepository(seed.DatabasePath)
        {
            BeforeGoalStateOutboxCommit = () => throw fault
        };

        var error = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.TransactGoalStateWithOutboxAsync("test:rollback", seed.GoalId,
                (state, _) => Task.FromResult((true, Cancelled(state!), true,
                    (IReadOnlyList<OrchestratorStateOutboxMessage>)[Message("first"), Message("second")]))));

        Xunit.Assert.Same(fault, error);
        Xunit.Assert.Equal(GoalStatus.Active, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        Xunit.Assert.Equal(before, await seed.Version());
        Xunit.Assert.Equal(0L, await seed.CountOutboxRows());
    }

    [Xunit.Fact]
    public async Task OutboxInsertFailure_RollsBackGoalRow()
    {
        using var seed = await Seed.Create();
        var before = await seed.Version();
        await seed.ExecuteSql("CREATE TRIGGER fail_outbox BEFORE INSERT ON state_outbox BEGIN SELECT RAISE(ABORT, 'outbox fault'); END");

        var error = await Xunit.Assert.ThrowsAsync<SqliteException>(() =>
            seed.Repository.TransactGoalStateWithOutboxAsync("test:insert-failure", seed.GoalId,
                (state, _) => Task.FromResult((true, Cancelled(state!), true,
                    (IReadOnlyList<OrchestratorStateOutboxMessage>)[Message("insert")] ))));

        Xunit.Assert.Contains("outbox fault", error.Message);
        Xunit.Assert.Equal(GoalStatus.Active, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        Xunit.Assert.Equal(before, await seed.Version());
        Xunit.Assert.Equal(0L, await seed.CountOutboxRows());
    }

    [Xunit.Fact]
    public async Task VersionConflict_ReappliesAndInsertsOnlyCommittedAttempt()
    {
        using var seed = await Seed.Create();
        var before = await seed.Version();
        var attempts = 0;

        await seed.Repository.TransactGoalStateWithOutboxAsync("test:retry", seed.GoalId, async (state, token) =>
        {
            attempts++;
            if (attempts == 1)
                await BumpGoalVersion(seed, token);
            return (true, Cancelled(state!), true,
                (IReadOnlyList<OrchestratorStateOutboxMessage>)[Message($"attempt-{attempts}")]);
        });

        Xunit.Assert.Equal(2, attempts);
        Xunit.Assert.Equal(before + 2, await seed.Version());
        Xunit.Assert.Equal("attempt-2", Xunit.Assert.Single(
            await seed.Repository.ListOutboxMessagesAsync("test-goal-delivery")).Id);
    }

    [Xunit.Fact]
    public async Task RepeatedVersionConflict_ThrowsTypedFailureAndInsertsNoMessages()
    {
        using var seed = await Seed.Create();
        var before = await seed.Version();
        var attempts = 0;

        await Xunit.Assert.ThrowsAsync<GoalTransactionConflictException>(() =>
            seed.Repository.TransactGoalStateWithOutboxAsync("test:conflict", seed.GoalId, async (state, token) =>
            {
                attempts++;
                // Deterministic conflicting writer between load and CAS; no timing-dependent overlap.
                await BumpGoalVersion(seed, token);
                return (true, Cancelled(state!), true,
                    (IReadOnlyList<OrchestratorStateOutboxMessage>)[Message($"conflict-{attempts}")]);
            }));

        Xunit.Assert.True(attempts > 1);
        Xunit.Assert.Equal(GoalStatus.Active, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        Xunit.Assert.Equal(before + attempts, await seed.Version());
        Xunit.Assert.Equal(0L, await seed.CountOutboxRows());
    }

    [Xunit.Fact]
    public async Task ForeignGoalState_IsRejectedBeforeEitherWrite()
    {
        using var seed = await Seed.Create();
        var before = await seed.Version();

        await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() =>
            seed.Repository.TransactGoalStateWithOutboxAsync("test:ownership", seed.GoalId,
                (state, _) => Task.FromResult((true,
                    (GoalStateSnapshot?)(state! with { Goal = state.Goal with { Id = GoalId.New().Value } }), true,
                    (IReadOnlyList<OrchestratorStateOutboxMessage>)[Message("foreign")]))));

        Xunit.Assert.Equal(before, await seed.Version());
        Xunit.Assert.Equal(0L, await seed.CountOutboxRows());
    }

    private static Task<bool> BumpGoalVersion(Seed seed, CancellationToken token) =>
        seed.Repository.TransactGoalStateAsync(seed.GoalId,
            (state, _) => Task.FromResult((true, state, true)), token);

    private static GoalStateSnapshot? Cancelled(GoalStateSnapshot state) =>
        state with { Goal = state.Goal with { Status = GoalStatus.Cancelled } };

    private static OrchestratorStateOutboxMessage Message(string id) =>
        new(id, "test-goal-delivery", "{}", DateTimeOffset.UnixEpoch);

    private sealed class Seed(string root, string databasePath, SqliteOrchestratorStateRepository repository, GoalId goalId)
        : IDisposable
    {
        internal string DatabasePath { get; } = databasePath;
        internal SqliteOrchestratorStateRepository Repository { get; } = repository;
        internal GoalId GoalId { get; } = goalId;

        internal static async Task<Seed> Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcg-goal-state-outbox-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(databasePath);
            var repository = new SqliteOrchestratorStateRepository(databasePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Atomic goal delivery", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            await repository.SaveAsync(kernel);
            return new Seed(root, databasePath, repository, goal.Id);
        }

        internal Task<long?> Version() =>
            SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(DatabasePath, GoalId.Value);

        internal async Task<long> CountOutboxRows()
        {
            await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM state_outbox";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        internal async Task ExecuteSql(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
