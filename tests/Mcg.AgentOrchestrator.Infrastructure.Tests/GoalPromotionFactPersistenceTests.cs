using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns its SQLite database and fixed promotion instant.
public sealed class GoalPromotionFactPersistenceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OriginalFact_LaterWritesArchiveAndRestart_RetainIdentityAndTime()
    {
        using var fixture = new Fixture();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(GoalId.New(), "Promotion retention");
        await fixture.Repository.SaveAsync(kernel);
        var original = Fact(goal.Id.Value);
        fixture.Repository.RecordPromotionFacts([original]);
        fixture.Repository.RecordPromotionFacts([Fact(goal.Id.Value, At.AddDays(3))]);

        var snapshot = Assert.Single(kernel.ExportSnapshot().Goals);
        snapshot = snapshot with
        {
            Tasks = snapshot.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray()
        };
        foreach (var status in new[] { GoalStatus.Completed, GoalStatus.Active, GoalStatus.Superseded, GoalStatus.Completed })
        {
            await fixture.Repository.SaveGoalSnapshotsAsync([snapshot with { Status = status }]);
            Assert.Equal(original, Assert.Single(await Read(fixture.Path)).PromotionFact);
        }
        fixture.Execute("UPDATE goals SET updated_at = '2000-01-01T00:00:00.0000000+00:00'");
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(At));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM goal_snapshot_archive"));
        Assert.Equal(1L, fixture.Scalar("SELECT json_extract(snapshot_json, '$.IsMetadataOnly') FROM goals"));
        Assert.Equal(original, Assert.Single(await Read(fixture.Path)).PromotionFact);
        await fixture.Repository.SaveGoalSnapshotsAsync([snapshot]); // stale snapshot cannot erase the independent fact
        Assert.Equal(original, Assert.Single(await Read(fixture.Path)).PromotionFact);
    }

    [Fact]
    public async Task GroupedInsertion_ReplayAndConflict_AreAtomicAndImmutable()
    {
        using var fixture = new Fixture();
        var kernel = new AgentOrchestratorKernel();
        var ids = Enumerable.Range(0, 3).Select(index => kernel.CreateGoal(GoalId.New(), $"Member {index}").Id.Value).ToArray();
        await fixture.Repository.SaveAsync(kernel);
        var original = Fact(ids[1]);
        fixture.Repository.RecordPromotionFacts([original]);
        Assert.Throws<InvalidOperationException>(() => fixture.Repository.RecordPromotionFacts([
            Fact(ids[0]), new(ids[1], new string('d', 40), new string('a', 40), new string('c', 40), At,
                GoalPromotionFact.CohortProvenance)]));
        Assert.Null((await Read(fixture.Path)).Single(row => row.Id == ids[0]).PromotionFact);

        var group = ids.Select(id => Fact(id)).ToArray();
        fixture.Repository.RecordPromotionFacts(group);
        fixture.Repository.RecordPromotionFacts(group);
        var rows = await Read(fixture.Path);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(Fact(row.Id), row.PromotionFact));
        Assert.Equal(3L, fixture.Scalar("SELECT COUNT(*) FROM goal_promotion_facts"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnly_LegacyOrMalformedProof_StaysUnknownWithoutSnapshotReads(bool malformed)
    {
        using var fixture = new Fixture();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(GoalId.New(), "Unknown promotion");
        await fixture.Repository.SaveAsync(kernel);
        fixture.Repository.RecordPromotionFacts([Fact(goal.Id.Value)]);
        if (malformed)
            fixture.Execute("UPDATE goal_promotion_facts SET promoted_at = 'not-an-instant'");
        else
            fixture.Execute("DROP TABLE goal_promotion_facts; DELETE FROM schema_migrations WHERE migration_number = 14");
        // A terminal default metadata read must succeed despite unusable snapshot JSON.
        fixture.Execute("UPDATE goals SET status = 'Completed', snapshot_json = 'not-json'");
        var bytes = File.ReadAllBytes(fixture.Path);
        var modified = File.GetLastWriteTimeUtc(fixture.Path);
        var row = Assert.Single(await Read(fixture.Path));
        Assert.Null(row.PromotionFact);
        Assert.Null(row.CreatedAt);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(fixture.Path));
        if (!malformed)
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'goal_promotion_facts'"));
    }

    private static GoalPromotionFact Fact(string id, DateTimeOffset? at = null) =>
        new(id, new string('b', 40), new string('a', 40), new string('c', 40), at ?? At,
            GoalPromotionFact.CohortProvenance);

    private static Task<IReadOnlyList<GoalSummary>> Read(string path) =>
        SqliteOrchestratorStateRepository.OpenReadOnly(path).ListGoalMetadataAsync();

    private sealed class Fixture : IDisposable
    {
        private readonly string root = SharedTestSupport.CreateTempDirectory();
        public string Path { get; }
        public SqliteOrchestratorStateRepository Repository { get; }
        public Fixture()
        {
            Path = System.IO.Path.Combine(root, "state.db");
            Repository = InfrastructureTestSupport.CreateMigratedStateRepository(Path);
        }
        public void Execute(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        public long Scalar(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
    }
}
