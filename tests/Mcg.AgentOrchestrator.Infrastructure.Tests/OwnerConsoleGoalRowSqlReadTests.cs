using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns its migrated database and non-pooled connections.
public sealed class OwnerConsoleGoalRowSqlReadTests
{
    [Fact]
    public async Task BoundedRead_SkipsPoisonedSnapshotsAndOldTerminalRows()
    {
        using var database = new Database();
        await database.SeedHistoryAsync();
        var baseline = await database.Repository.ListGoalMetadataAsync();
        Assert.Equal(209, baseline.Count);
        await database.ExecuteAsync("UPDATE goals SET snapshot_json = '{not-json'");
        await Assert.ThrowsAsync<SqliteException>(() => database.Repository.ListGoalMetadataAsync());

        database.Statements.Clear();
        var rows = await database.Repository.ListGoalBoardRowsAsync(new(200, 1024, "Target files/scopes:"));
        var newestTerminalIds = baseline.Where(IsTerminal).Take(200).Select(row => row.Id).ToHashSet();
        var expected = baseline.Where(row => !IsTerminal(row) || newestTerminalIds.Contains(row.Id)).ToArray();
        Assert.Equal(203, rows.Count);
        Assert.Equal(3, rows.Count(row => !IsTerminal(row)));
        Assert.Equal(200, rows.Count(IsTerminal));
        Assert.Equal(expected.Select(Identity), rows.Select(Identity));
        Assert.All(baseline.Where(IsTerminal).Skip(200), old => Assert.DoesNotContain(rows, row => row.Id == old.Id));
        Assert.Equal(6, baseline.Where(IsTerminal).Skip(200).Count());
        Assert.All(rows, row =>
        {
            Assert.Null(row.CreatedAt);
            Assert.Null(row.Condition);
            Assert.Null(row.PromotionFact);
        });
        AssertColumnOnlySelect(Assert.Single(database.Statements.Where(sql => sql.Contains("FROM goals"))));

        database.Statements.Clear();
        var all = await database.Repository.ListGoalBoardRowsAsync(new(null, 0, null));
        Assert.Equal(209, all.Count);
        Assert.Equal(baseline.Select(Identity), all.Select(Identity));
        Assert.All(all, row => Assert.Equal(string.Empty, row.Objective));
        AssertColumnOnlySelect(Assert.Single(database.Statements.Where(sql => sql.Contains("FROM goals"))));
    }

    [Fact]
    public async Task TitleCuts_PreserveGeneratedAndAuthoredDisplayTitles()
    {
        using var database = new Database();
        var objectives = new[]
        {
            "Plain title\n" + new string('x', 200000 - "Plain title\n".Length),
            Generated("\r\n"), Generated("\n"), "\n\n# Heading title\nBody",
            "Backlog slice: Not generated\nBody"
        };
        await database.SeedAsync(objectives.Select(text => (text, GoalStatus.Active)).ToArray());
        var full = await database.Repository.ListGoalMetadataAsync();
        Assert.Equal(objectives.OrderBy(text => text, StringComparer.Ordinal),
            full.Select(row => row.Objective).OrderBy(text => text, StringComparer.Ordinal));
        var rows = await database.Repository.ListGoalBoardRowsAsync(new(200, 1024, BacklogIntakePlanner.TargetScopeHeadingLine));
        Assert.Equal(5, rows.Count);
        foreach (var row in rows)
        {
            var original = full.Single(item => item.Id == row.Id).Objective;
            Assert.Equal(OwnerGoalTitle.Full(original), OwnerGoalTitle.Full(row.Objective));
            if (original.StartsWith("Plain title")) Assert.Equal(1024, row.Objective.Length);
            if (original.Contains(BacklogIntakePlanner.TargetScopeHeadingLine, StringComparison.Ordinal))
            {
                Assert.Equal(original, row.Objective);
                Assert.Equal("Generated title", OwnerGoalTitle.Full(row.Objective));
            }
        }
    }

    [Fact]
    public async Task Board_DefaultQueryMatchesSqliteAndOriginalGoalSelection()
    {
        using var database = new Database();
        await database.SeedHistoryAsync();
        var baseline = await database.Repository.ListGoalMetadataAsync();
        var clock = new OwnerConsoleTestClock { Now = Database.Start.AddMinutes(210) };
        var harness = new OwnerConsoleHarness(clock);
        var fallback = new PassThrough(database.Repository);
        IReadOnlyDictionary<string, string>? sqlTitles = null, defaultTitles = null;
        var sqlBuilder = new OwnerConsoleViewModelBuilder(database.Repository, harness.Questions, harness.Liveness,
            new Epics(), clock, gateProgress: titles => { sqlTitles = titles; return null; });
        var defaultBuilder = new OwnerConsoleViewModelBuilder(fallback, harness.Questions, harness.Liveness,
            new Epics(), clock, gateProgress: titles => { defaultTitles = titles; return null; });
        var sqlBoard = await sqlBuilder.BuildBoardAsync();
        var defaultBoard = await defaultBuilder.BuildBoardAsync();
        Assert.NotEmpty(sqlBoard.Board);
        Assert.Equal(sqlBoard.Board.Select(row => (row.GoalId, row.Title, row.State, row.Stage, row.Dimmed)),
            defaultBoard.Board.Select(row => (row.GoalId, row.Title, row.State, row.Stage, row.Dimmed)));
        Assert.NotNull(sqlTitles);
        Assert.NotNull(defaultTitles);
        Assert.Equal(203, sqlTitles.Count);
        Assert.Equal(sqlTitles.OrderBy(item => item.Key), defaultTitles.OrderBy(item => item.Key));
        var expected = baseline.Where(item => Enum.TryParse<GoalStatus>(item.Status, true, out var status) &&
                (status is not (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded) ||
                 status is GoalStatus.Failed or GoalStatus.Parked &&
                 (!DateTimeOffset.TryParse(item.UpdatedAt, out var updated) || clock.Now - updated <= TimeSpan.FromHours(24))))
            .Select(item => item.Id).ToArray();
        Assert.Equal(3, expected.Length);
        Assert.Equal(expected, Assert.Single(fallback.Loaded));
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(null, 5, "")]
    [InlineData(1, 5, "ROW")]
    public async Task QueryEdges_DefaultAndSqliteAgree(int? limit, int prefix, string? marker)
    {
        using var database = new Database();
        await database.SeedAsync([("row one", GoalStatus.Completed), ("row two", GoalStatus.Active)]);
        IOrchestratorStateQueries fallback = new PassThrough(database.Repository);
        var query = new GoalBoardRowQuery(limit, prefix, marker);
        Assert.Equal(await fallback.ListGoalBoardRowsAsync(query), await database.Repository.ListGoalBoardRowsAsync(query));
    }

    private static (string, string, string) Identity(GoalSummary row) => (row.Id, row.Status, row.UpdatedAt);
    private static bool IsTerminal(GoalSummary row) => row.Status is "Completed" or "Cancelled" or "Superseded";
    private static void AssertColumnOnlySelect(string sql)
    {
        foreach (var forbidden in new[] { "snapshot_json", "json_each", "goal_promotion_facts" })
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", sql);
    }
    private static string Generated(string newline) => string.Join(newline,
        BacklogIntakePlanner.GeneratedHeadingPrefix + "Generated title", "", new string('b', 20000), "",
        BacklogIntakePlanner.TargetScopeHeadingLine, BacklogIntakePlanner.UnknownScopeMarkerLine,
        "Includes: console", "Exclusions: none", "Verification: tests");

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    // Deliberately leaves ListGoalBoardRowsAsync to the interface default.
    private sealed class PassThrough(IOrchestratorStateQueries inner) : IOrchestratorStateQueries
    {
        internal readonly List<string[]> Loaded = [];
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken token = default) => inner.ListGoalMetadataAsync(token);
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids, CancellationToken token = default)
        {
            Loaded.Add(ids.Select(id => id.Value).ToArray());
            return inner.LoadGoalsAsync(ids, token);
        }
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(CancellationToken token = default) =>
            inner.ListOpenHumanInputRequestsAsync(token);
        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> ids, CancellationToken token = default) => inner.ListTerminalOwnerQuestionHoldsAsync(ids, token);
    }

    private sealed class Database : IDisposable
    {
        internal static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly string _root = SharedTestSupport.CreateTempDirectory();
        internal readonly List<string> Statements = [];
        internal string Path { get; }
        internal SqliteOrchestratorStateRepository Repository { get; }
        internal Database()
        {
            Path = System.IO.Path.Combine(_root, "state.db");
            StateDbMigrations.EnsureUpToDate(Path);
            Repository = new(Path, Statements.Add);
        }
        internal Task SeedHistoryAsync() => SeedAsync(Enumerable.Range(0, 205)
            .Select(index => ($"Terminal {index}", index % 2 == 0 ? GoalStatus.Completed : GoalStatus.Cancelled))
            .Append(("Superseded", GoalStatus.Superseded)).Append(("Active", GoalStatus.Active))
            .Append(("Parked", GoalStatus.Parked)).Append(("Failed", GoalStatus.Failed)).ToArray());
        internal async Task SeedAsync((string Objective, GoalStatus Status)[] cases)
        {
            var kernel = new AgentOrchestratorKernel();
            foreach (var item in cases)
                kernel.CreateGoal(item.Objective, [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
            var snapshots = kernel.ExportSnapshot().Goals.Select((goal, index) => goal with
            {
                Status = cases[index].Status,
                Timeline = [goal.Timeline[0] with { OccurredAt = Start.AddMinutes(index) }]
            }).ToArray();
            await Repository.SaveGoalSnapshotsAsync(snapshots);
            await using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            await connection.OpenAsync();
            for (var index = 0; index < snapshots.Length; index++)
            {
                await using var command = connection.CreateCommand();
                // CreateGoal trims input; preserve exact stored title fixtures, including leading blank lines.
                command.CommandText = "UPDATE goals SET updated_at = $updated, objective = $objective WHERE id = $id";
                command.Parameters.AddWithValue("$updated", Start.AddMinutes(index).ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$objective", cases[index].Objective);
                command.Parameters.AddWithValue("$id", snapshots[index].Id);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
        }
        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            Assert.Equal(209, await command.ExecuteNonQueryAsync());
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
