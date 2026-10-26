using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns its temporary stores and streams; clocks and probes are injected.
public sealed class ConductorStatusLatestLandingSourceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private const string GoalA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string GoalB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string GoalC = "cccccccccccccccccccccccccccccccc";
    private const string GoalD = "dddddddddddddddddddddddddddddddd";
    private const string GoalE = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string GoalX = "11111111111111111111111111111111";
    private const string GoalY = "22222222222222222222222222222222";
    private const string GoalZ = "33333333333333333333333333333333";

    [Fact]
    public async Task LockedGoalEvents_LaterDecoy_StatusUsesPromotionFactInBothViews()
    {
        using var fixture = new Fixture();
        await fixture.SeedGoalsAndFacts();
        var b = fixture.WriteLanding(GoalB, Start.AddMinutes(30));
        var d = fixture.WriteLanding(GoalD, Start.AddMinutes(50));
        Assert.Equal(2, Directory.GetFiles(fixture.Workspace.GoalLifecycleEventsDirectory).Length);
        using var heldB = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None);
        using var heldD = new FileStream(d, FileMode.Open, FileAccess.Read, FileShare.None);

        AssertLanding(fixture.Workspace, GoalB, Start.AddMinutes(30));
        using var json = JsonDocument.Parse(Status(fixture.Workspace, json: true));
        var goals = json.RootElement.GetProperty("payload").GetProperty("goals")
            .GetProperty("nonTerminal").EnumerateArray().ToArray();
        Assert.Equal(new[] { GoalA, GoalC }, goals.Select(goal => goal.GetProperty("id").GetString()));
        Assert.All(goals, goal => Assert.Equal("Active", goal.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task NewFacts_SingleThenGrouped_StatusMovesToLastInsertedMember()
    {
        using var fixture = new Fixture();
        await fixture.SeedGoalsAndFacts();
        fixture.WriteLanding(GoalB, Start.AddMinutes(30));
        fixture.WriteLanding(GoalD, Start.AddMinutes(50));
        AssertLanding(fixture.Workspace, GoalB, Start.AddMinutes(30));

        fixture.Repository.RecordPromotionFacts([FactFor(GoalE, 40)]);
        AssertLanding(fixture.Workspace, GoalE, Start.AddMinutes(40));
        fixture.Repository.RecordPromotionFacts([FactFor(GoalX, 60), FactFor(GoalY, 60), FactFor(GoalZ, 60)]);
        AssertLanding(fixture.Workspace, GoalZ, Start.AddMinutes(60));
    }

    [Fact]
    public async Task EventOnlyLanding_EmptyFacts_StatusIsUnavailableInBothViews()
    {
        using var fixture = new Fixture();
        await fixture.SeedGoals();
        fixture.WriteLanding(GoalB, Start.AddMinutes(30));

        AssertLanding(fixture.Workspace, null, null);
    }

    [Fact]
    public void MissingDatabase_ReadReturnsNullWithoutCreatingFile()
    {
        using var fixture = new Fixture();
        Assert.False(File.Exists(fixture.Workspace.SqliteStatePath));
        Assert.Null(ConductorStatusLatestLandingReader.Read(fixture.Workspace.SqliteStatePath));
        Assert.False(File.Exists(fixture.Workspace.SqliteStatePath));
    }

    [Fact]
    public void UnrelatedTable_NoPromotionSchema_ReadReturnsNull()
    {
        using var fixture = new Fixture();
        using (var connection = fixture.OpenPlainConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (value TEXT)";
            command.ExecuteNonQuery();
        }

        Assert.Null(ConductorStatusLatestLandingReader.Read(fixture.Workspace.SqliteStatePath));
    }

    [Fact]
    public void MalformedTimestamp_OnlyRow_ReadReturnsNull()
    {
        using var fixture = new Fixture();
        _ = fixture.Repository;
        fixture.InsertRawFact(GoalB, "not-an-instant");

        Assert.Null(ConductorStatusLatestLandingReader.Read(fixture.Workspace.SqliteStatePath));
    }

    [Fact]
    public void MalformedTimestamp_SortsFirst_ReadSkipsToValidRow()
    {
        using var fixture = new Fixture();
        _ = fixture.Repository;
        fixture.InsertRawFact(GoalB, "not-an-instant");
        fixture.InsertRawFact(GoalD, Start.AddMinutes(10).ToString("O", CultureInfo.InvariantCulture));
        using (var connection = fixture.OpenPlainConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT promoted_at FROM goal_promotion_facts ORDER BY promoted_at DESC, rowid DESC LIMIT 1";
            Assert.Equal("not-an-instant", command.ExecuteScalar());
        }

        var landing = ConductorStatusLatestLandingReader.Read(fixture.Workspace.SqliteStatePath);
        Assert.NotNull(landing);
        Assert.Equal(GoalD, landing.Goal);
        Assert.Equal(Start.AddMinutes(10), landing.At);
    }

    private static GoalPromotionFact FactFor(string goal, int minutes) =>
        new(goal, new string('a', 40), new string('b', 40), new string('c', 40),
            Start.AddMinutes(minutes), GoalPromotionFact.MergeTrainProvenance);

    private static void AssertLanding(OrchestratorWorkspace workspace, string? goal, DateTimeOffset? at)
    {
        var human = Status(workspace, json: false);
        using var json = JsonDocument.Parse(Status(workspace, json: true));
        var landing = json.RootElement.GetProperty("payload").GetProperty("mostRecentLanding");
        if (goal is null)
        {
            Assert.Contains("Most recent landing: unavailable" + Environment.NewLine, human);
            Assert.Equal(JsonValueKind.Null, landing.ValueKind);
        }
        else
        {
            var timestamp = at!.Value.ToString("O", CultureInfo.InvariantCulture);
            Assert.Contains($"Most recent landing: {goal} at {timestamp}{Environment.NewLine}", human);
            Assert.Equal(goal, landing.GetProperty("goal").GetString());
            Assert.Equal(timestamp, landing.GetProperty("at").GetString());
        }
    }

    private static string Status(OrchestratorWorkspace workspace, bool json)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(json ? ["conductor", "status", "--json"] : ["conductor", "status"],
            workspace, lockProbe: new FixedProbe(), bootTime: () => Start.AddDays(-1),
            mainCommit: () => "abc123", output: output, error: error, time: new FixedTimeProvider()));
        Assert.Equal("", error.ToString());
        return output.ToString();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "landing-source-" + Guid.NewGuid().ToString("N"));
        private SqliteOrchestratorStateRepository? _repository;
        internal OrchestratorWorkspace Workspace { get; }
        internal SqliteOrchestratorStateRepository Repository => _repository ??=
            InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
        }

        internal async Task SeedGoals()
        {
            var kernel = new AgentOrchestratorKernel(new FixedClock());
            kernel.CreateGoal(new GoalId(GoalA), "Goal A");
            kernel.CreateGoal(new GoalId(GoalC), "Goal C");
            await Repository.SaveGoalSnapshotsAsync(kernel.ExportSnapshot().Goals
                .Select(goal => goal with { Status = GoalStatus.Active }).ToArray());
        }

        internal async Task SeedGoalsAndFacts()
        {
            await SeedGoals();
            Repository.RecordPromotionFacts([FactFor(GoalB, 30)]);
            Repository.RecordPromotionFacts([FactFor(GoalD, 20)]);
        }

        internal string WriteLanding(string goal, DateTimeOffset at)
        {
            Directory.CreateDirectory(Workspace.GoalLifecycleEventsDirectory);
            var path = Path.Combine(Workspace.GoalLifecycleEventsDirectory, goal + ".jsonl");
            File.WriteAllText(path, JsonSerializer.Serialize(new { eventType = "GoalLanded", goalId = goal, timestamp = at }) + "\r\n");
            return path;
        }

        internal SqliteConnection OpenPlainConnection()
        {
            Directory.CreateDirectory(Workspace.OrchestratorDirectory);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Workspace.SqliteStatePath, Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        internal void InsertRawFact(string goal, string at)
        {
            using var connection = OpenPlainConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO goal_promotion_facts
                    (goal_id, candidate_revision, prior_main_revision, promoted_main_revision, promoted_at, provenance)
                VALUES ($goal, $candidate, $prior, $main, $at, $provenance)
                """;
            command.Parameters.AddWithValue("$goal", goal);
            command.Parameters.AddWithValue("$candidate", new string('a', 40));
            command.Parameters.AddWithValue("$prior", new string('b', 40));
            command.Parameters.AddWithValue("$main", new string('c', 40));
            command.Parameters.AddWithValue("$at", at);
            command.Parameters.AddWithValue("$provenance", GoalPromotionFact.MergeTrainProvenance);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedProbe : IConductorLockProbe
    {
        public int? ActiveOwnerPid(OrchestratorWorkspace workspace) => null;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Start.AddHours(2);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Start;
    }
}
