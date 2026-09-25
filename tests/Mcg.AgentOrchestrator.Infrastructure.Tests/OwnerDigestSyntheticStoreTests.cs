using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class OwnerDigestSyntheticStoreTests
{
    [Fact]
    public async Task SyntheticStoreProducesExactPerGoalAndAggregateMetrics()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var digest = CliOwnerDigestCommand.Read(fixture.Workspace, fixture.Clock,
            OwnerDigestTestFixture.Start, OwnerDigestTestFixture.End);
        Assert.Equal(2, digest.Goals.Count);
        var a = digest.Goals.Single(g => g.GoalId == OwnerDigestTestFixture.GoalA);
        Assert.Equal(new OwnerDigestActorTotals(2, 1, 0), a.Interventions);
        Assert.Equal("correct", a.LandingStatus);
        Assert.Equal(4, a.TailHours);
        Assert.Equal(new OwnerDigestHours(1.5, 1, 0), a.MechanicalHours);
        var b = digest.Goals.Single(g => g.GoalId == OwnerDigestTestFixture.GoalB);
        Assert.Equal("escape", b.LandingStatus);
        Assert.Equal(2, b.TailHours);
        Assert.Equal(new OwnerDigestActorTotals(0, 0, 0), b.Interventions);
        Assert.Equal(new OwnerDigestHours(0, 0, 0), b.MechanicalHours);
        Assert.Equal(3, digest.Totals.Interventions.Total);
        Assert.Equal(new OwnerDigestActorTotals(2, 1, 0), digest.Totals.Interventions);
        Assert.Equal(1.5, digest.Totals.MeanInterventionsPerLanding);
        Assert.Equal(1, digest.Totals.CorrectLandings);
        Assert.Equal(1, digest.Totals.Escapes);
        Assert.Equal(0.5, digest.Totals.CorrectLandingRate);
        Assert.Equal(2, digest.Totals.TailMedianHours);
        Assert.Equal(4, digest.Totals.TailP90Hours);
        Assert.Equal(2.5, digest.Totals.MechanicalHours.Total);
        Assert.Equal(new OwnerDigestHours(1.5, 1, 0), digest.Totals.MechanicalHours);
        Assert.Equal(1, digest.NonLandedGoalsWithInterventions);
        Assert.Equal("not tracked", digest.Reverts);
    }

    [Fact]
    public async Task DigestReadsCanaryReceiptFromUncheckpointedWal()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Workspace.RunEventStorePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        using var writer = new SqliteConnection(connectionString);
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO run_events (event_id, occurred_at, event_type, operation, status, payload_json)
                VALUES ($id, $at, $type, 'receipt', 'Failed', $payload)
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$at", OwnerDigestTestFixture.Start.AddHours(10).ToString("O"));
            command.Parameters.AddWithValue("$type", RunEventTypes.PostLandingCanary);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { landingSha = "aaaa" }));
            command.ExecuteNonQuery();
        }
        Assert.True(new FileInfo(fixture.Workspace.RunEventStorePath + "-wal").Length > 0);

        var digest = CliOwnerDigestCommand.Read(fixture.Workspace, fixture.Clock,
            OwnerDigestTestFixture.Start, OwnerDigestTestFixture.End);

        Assert.Equal("escape", digest.Goals.Single(g => g.GoalId == OwnerDigestTestFixture.GoalA).LandingStatus);
    }
}

internal sealed class OwnerDigestTestFixture : IDisposable
{
    internal static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
    internal static readonly DateTimeOffset End = Start.AddDays(1);
    internal const string GoalA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string GoalB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string GoalC = "cccccccccccccccccccccccccccccccc";
    internal const string GoalD = "dddddddddddddddddddddddddddddddd";

    private readonly string _root;
    internal OrchestratorWorkspace Workspace { get; }
    internal MutableClock Clock { get; } = new(Start);

    private OwnerDigestTestFixture(string root)
    {
        _root = root;
        Workspace = OrchestratorWorkspace.ForDirectory(root);
    }

    internal static async Task<OwnerDigestTestFixture> CreateAsync()
    {
        var fixture = new OwnerDigestTestFixture(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel(fixture.Clock);
        var a = kernel.CreateGoal(new GoalId(GoalA), "Goal A");
        var b = kernel.CreateGoal(new GoalId(GoalB), "Goal B");
        var c = kernel.CreateGoal(new GoalId(GoalC), "Goal C");
        _ = kernel.CreateGoal(new GoalId(GoalD), "Goal D");
        void At(double hour, Action action)
        {
            fixture.Clock.UtcNow = Start.AddHours(hour);
            action();
        }
        void Tick(Goal goal, double hour, string outcome, string state) => At(hour, () =>
            kernel.RecordGoalPolicyDecision(goal.Id, "tick", new ConductorTickOutcomePayload(outcome, state, null)));
        void Intent(Goal goal, double hour, string id, OperatorActorKind kind) => At(hour, () =>
            kernel.RecordOperatorIntentApplied(goal.Id, id, "retry", null, "operator", "cli", null,
                "applied", kind));
        Tick(a, 2, "Escalated", "AwaitingHumanInput");
        Intent(a, 3.5, "h1", OperatorActorKind.Human);
        Tick(a, 4, "Executed", "Verified");
        Tick(a, 5, "Escalated", "Verified");
        Intent(a, 6, "a1", OperatorActorKind.Agent);
        Intent(a, 7, "h2", OperatorActorKind.Human);
        Tick(b, 10, "Executed", "Verified");
        Intent(c, 14, "h3", OperatorActorKind.Human);
        await CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath).SaveAsync(kernel);

        var writer = new GoalLifecycleEventWriter(fixture.Workspace.GoalLifecycleEventsDirectory, fixture.Clock);
        At(-4, () => writer.AppendGoalLandedFromAncestry(new GoalId(GoalD), "goal/d", "d", "dddd"));
        At(8, () => writer.AppendGoalLandedFromAncestry(a.Id, "goal/a", "a", "aaaa"));
        At(12, () => writer.AppendGoalLandedFromAncestry(b.Id, "goal/b", "b", "bbbb"));

        var runEvents = new SqliteRunEventStore(fixture.Workspace.RunEventStorePath);
        await Receipt(runEvents, "aaaa", 9, true);
        await Receipt(runEvents, "bbbb", 13, false);
        Directory.CreateDirectory(fixture.Workspace.OrchestratorDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace.OrchestratorDirectory, "operator-intents.db"), "untouched");
        fixture.Clock.UtcNow = End;
        return fixture;
    }

    private static async Task Receipt(SqliteRunEventStore store, string sha, double hour, bool passed)
    {
        var at = Start.AddHours(hour);
        var payload = JsonSerializer.Serialize(new { landingSha = sha });
        await store.AppendAsync(new RunEventAppend(RunEventTypes.PostLandingCanary,
            null, "receipt", passed ? "Passed" : "Failed", null, payload, at));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
