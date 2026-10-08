using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each fixture owns its database, lifecycle files and clock.
public sealed class OwnerDigestTimelineReaderTests
{
    private const string OutsideIntentGoal = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string LaterLandingGoal = "ffffffffffffffffffffffffffffffff";
    private const string EventlessGoal = "11111111111111111111111111111111";
    private const string LifecycleOnlyGoal = "22222222222222222222222222222222";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_MixedGoals_MatchesFullLoadJson(bool defaultWindow)
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var start = OwnerDigestTestFixture.Start;
        var end = OwnerDigestTestFixture.End;
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        var loaded = await repository.LoadAsync();
        var kernel = AgentOrchestratorKernel.FromSnapshot(loaded.ExportSnapshot(), fixture.Clock);
        fixture.Clock.UtcNow = start.AddHours(-5);
        kernel.CreateGoal(new GoalId(OutsideIntentGoal), "Outside intent");
        kernel.CreateGoal(new GoalId(LaterLandingGoal), "Later landing");
        kernel.CreateGoal(new GoalId(EventlessGoal), "No events in window");
        kernel.RecordOperatorIntentApplied(new GoalId(OutsideIntentGoal), "outside", "retry", null,
            "operator", "cli", null, "applied", OperatorActorKind.Human);
        fixture.Clock.UtcNow = start.AddHours(8);
        kernel.RecordOperatorIntentApplied(new GoalId(OwnerDigestTestFixture.GoalA), "at-landing", "retry", null,
            "operator", "cli", null, "applied", OperatorActorKind.Human);
        void Decision(string id, DateTimeOffset at, string evidence)
        {
            fixture.Clock.UtcNow = at;
            kernel.RecordGoalPolicyDecision(new GoalId(id), "tick", new ConductorTickOutcomePayload(
                "Executed", "Verified", null, new PolicyDecisionRecord(
                    "Landing", "Execute", 3, evidence, "fixture", [])));
        }
        // Record out of chronological order, including a tie whose later array entry wins.
        Decision(OwnerDigestTestFixture.GoalA, start.AddHours(16), "post-landing-first");
        Decision(OwnerDigestTestFixture.GoalA, start.AddHours(15), "older");
        Decision(OwnerDigestTestFixture.GoalA, start.AddHours(16), "post-landing-last");
        Decision(OwnerDigestTestFixture.GoalA, end, "exclusive-end");
        Decision(OwnerDigestTestFixture.GoalD, start, "inclusive-start");
        Decision(LaterLandingGoal, start.AddHours(17).ToOffset(TimeSpan.FromHours(5)), "later-landing");
        fixture.Clock.UtcNow = start.AddTicks(-1);
        kernel.RecordOperatorIntentApplied(new GoalId(OwnerDigestTestFixture.GoalC), "before-window", "retry", null,
            "operator", "cli", null, "applied", OperatorActorKind.Human);
        fixture.Clock.UtcNow = end;
        kernel.RecordOperatorIntentApplied(new GoalId(OwnerDigestTestFixture.GoalC), "at-end", "retry", null,
            "operator", "cli", null, "applied", OperatorActorKind.Human);
        await repository.SaveAsync(kernel);

        var writer = new GoalLifecycleEventWriter(fixture.Workspace.GoalLifecycleEventsDirectory, fixture.Clock);
        fixture.Clock.UtcNow = end.AddHours(2);
        writer.AppendGoalLandedFromAncestry(new GoalId(LaterLandingGoal), "goal/later", "later", null!);
        fixture.Clock.UtcNow = end.AddHours(3);
        writer.AppendGoalLandedFromAncestry(new GoalId(LaterLandingGoal), "goal/later", "later", "later-sha");
        fixture.Clock.UtcNow = start.AddHours(9);
        writer.AppendGoalLandedFromAncestry(new GoalId(LifecycleOnlyGoal), "goal/only", "only", "only-sha");
        fixture.Clock.UtcNow = start.AddHours(10);
        writer.AppendGoalLandedFromAncestry(new GoalId(OwnerDigestTestFixture.GoalA), "goal/a", "a", "later-a-sha");
        await File.WriteAllLinesAsync(Path.Combine(fixture.Workspace.GoalLifecycleEventsDirectory, "malformed.jsonl"),
            ["{", "{\"eventType\":\"GoalLanded\"}"]);
        fixture.Clock.UtcNow = end;

        DateTimeOffset? since = defaultWindow ? null : start;
        DateTimeOffset? until = defaultWindow ? null : end;
        var full = CliOwnerDigestCommand.Read(fixture.Workspace, fixture.Clock, out var goals, since, until);
        var projected = CliOwnerDigestCommand.Read(fixture.Workspace, fixture.Clock, since, until);
        Assert.Equal(ToJson(full), ToJson(projected));
        Assert.Equal(3, projected.Goals.Count);
        Assert.Equal(start.AddHours(8), projected.Goals.Single(g => g.GoalId == OwnerDigestTestFixture.GoalA).LandedAt);
        Assert.Equal("aaaa", projected.Goals.Single(g => g.GoalId == OwnerDigestTestFixture.GoalA).LandingSha);
        Assert.Contains(projected.Goals, g => g.GoalId == LifecycleOnlyGoal && g.LandingSha == "only-sha");
        Assert.Equal(1, projected.NonLandedGoalsWithInterventions);
        Assert.Equal(2, projected.MalformedLifecycleLines);
        Assert.Equal("post-landing-last", projected.LatestDecisions.Entries
            .Single(d => d.GoalId == OwnerDigestTestFixture.GoalA).Evidence);
        Assert.Equal("inclusive-start", projected.LatestDecisions.Entries
            .Single(d => d.GoalId == OwnerDigestTestFixture.GoalD).Evidence);
        Assert.Equal("later-landing", projected.LatestDecisions.Entries
            .Single(d => d.GoalId == LaterLandingGoal).Evidence);

        var landings = new Dictionary<string, DateTimeOffset>
        {
            [OwnerDigestTestFixture.GoalA.ToUpperInvariant()] = start.AddHours(8),
            [OwnerDigestTestFixture.GoalB] = start.AddHours(12),
            [OwnerDigestTestFixture.GoalD] = start.AddHours(-4),
            [LaterLandingGoal] = end.AddHours(2)
        };
        var inputs = new OwnerDigestTimelineReader().Read(fixture.Workspace.SqliteStatePath, landings, start, end);
        Assert.Equal(goals.Count, inputs.Count);
        Assert.DoesNotContain(inputs, input => input.GoalId == LifecycleOnlyGoal);
        Assert.Empty(inputs.Single(input => input.GoalId == EventlessGoal).Timeline);
        Assert.Empty(inputs.Single(input => input.GoalId == OutsideIntentGoal).Timeline);
        var cutoffs = new Dictionary<string, DateTimeOffset>(landings, StringComparer.OrdinalIgnoreCase);
        foreach (var goal in goals)
        {
            var expected = goal.Timeline.Where(e =>
                (cutoffs.TryGetValue(goal.Id.Value, out var landing) && e.OccurredAt <= landing) ||
                (e.OccurredAt >= start && e.OccurredAt < end)).ToArray();
            Assert.Equal(JsonSerializer.Serialize(expected),
                JsonSerializer.Serialize(inputs.Single(input => input.GoalId == goal.Id.Value).Timeline));
        }
    }

    [Fact]
    public async Task Read_UnreadableTasks_PreservesTimelineWithoutHydratingGoal()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var expected = new OwnerDigestTimelineReader().Read(fixture.Workspace.SqliteStatePath,
            new Dictionary<string, DateTimeOffset>(), OwnerDigestTestFixture.Start, OwnerDigestTestFixture.End)
            .Single(input => input.GoalId == OwnerDigestTestFixture.GoalC).Timeline;
        MutateSnapshot(fixture, "json_set(snapshot_json, '$.Tasks', 'unreadable')");

        var loaded = await SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath).LoadAsync();
        Assert.DoesNotContain(loaded.Goals, g => g.Id.Value == OwnerDigestTestFixture.GoalC);
        var inputs = new OwnerDigestTimelineReader().Read(fixture.Workspace.SqliteStatePath,
            new Dictionary<string, DateTimeOffset>(), OwnerDigestTestFixture.Start, OwnerDigestTestFixture.End);
        var timeline = inputs.Single(input => input.GoalId == OwnerDigestTestFixture.GoalC).Timeline;
        Assert.Contains(timeline, e => e.OperatorIntentApplied?.IntentId == "h3");
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(timeline));

        var projected = CliOwnerDigestCommand.Read(fixture.Workspace, fixture.Clock);
        var full = CliOwnerDigestCommand.Read(fixture.Workspace, fixture.Clock, out _);
        Assert.Equal(1, projected.NonLandedGoalsWithInterventions);
        Assert.Equal(0, full.NonLandedGoalsWithInterventions);
    }

    [Theory]
    [InlineData("json_remove(snapshot_json, '$.Timeline')")]
    [InlineData("json_set(snapshot_json, '$.Timeline', 'not an array')")]
    [InlineData("json_set(snapshot_json, '$.Timeline', json('{}'))")]
    [InlineData("json_set(snapshot_json, '$.IsMetadataOnly', json('true'))")]
    [InlineData("json_insert(snapshot_json, '$.Timeline[#]', 'invalid event')")]
    [InlineData("'{'")]
    public async Task Read_UnreadableTimeline_ReturnsEmptyInputAndOtherGoals(string expression)
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        MutateSnapshot(fixture, expression);
        var inputs = new OwnerDigestTimelineReader().Read(fixture.Workspace.SqliteStatePath,
            new Dictionary<string, DateTimeOffset>(), OwnerDigestTestFixture.Start, OwnerDigestTestFixture.End);
        Assert.Equal(4, inputs.Count);
        Assert.Empty(inputs.Single(input => input.GoalId == OwnerDigestTestFixture.GoalC).Timeline);
        Assert.Contains(inputs.Single(input => input.GoalId == OwnerDigestTestFixture.GoalA).Timeline,
            e => e.OperatorIntentApplied?.IntentId == "h1");
    }

    private static void MutateSnapshot(OwnerDigestTestFixture fixture, string expression)
    {
        using var connection = StateDbConnectionFactory.Open(fixture.Workspace.SqliteStatePath,
            StateDbConnectionProfile.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE goals SET snapshot_json = {expression} WHERE id = $id";
        command.Parameters.AddWithValue("$id", OwnerDigestTestFixture.GoalC);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static string ToJson(OwnerDigestResult digest)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        CliOwnerDigestCommand.WriteJson(writer, digest);
        return writer.ToString();
    }
}
