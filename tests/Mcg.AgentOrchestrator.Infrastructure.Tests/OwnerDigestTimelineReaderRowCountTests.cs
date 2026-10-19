using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: each fixture owns its temporary directory, databases and clock.
public sealed class OwnerDigestTimelineReaderRowCountTests
{
    private const string OutsideIntentGoal = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string LaterLandingGoal = "ffffffffffffffffffffffffffffffff";
    private const string EventlessGoal = "11111111111111111111111111111111";

    [Fact]
    public async Task Read_ManyEvents_CountsGoalRowsAndTimelineTextOnEveryRead()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        var loaded = await repository.LoadAsync();
        var kernel = AgentOrchestratorKernel.FromSnapshot(loaded.ExportSnapshot(), fixture.Clock);
        fixture.Clock.UtcNow = OwnerDigestTestFixture.Start.AddHours(1);
        var goals = new[] { OutsideIntentGoal, LaterLandingGoal, EventlessGoal };
        var counts = new[] { 40, 25, 0 };
        for (var i = 0; i < goals.Length; i++)
        {
            kernel.CreateGoal(new GoalId(goals[i]), i == 0 ? new string('x', 200_000) : "Count fixture");
            for (var j = 1; j < counts[i]; j++)
                kernel.RecordOperatorIntentApplied(new GoalId(goals[i]), $"intent-{j}", "retry", null,
                    "operator", "cli", null, "applied", OperatorActorKind.Human);
        }
        await repository.SaveAsync(kernel);
        fixture.Clock.UtcNow = OwnerDigestTestFixture.End;
        MutateSnapshot(fixture, "json_set(snapshot_json, '$.Timeline', json('[]'))", EventlessGoal);

        using var connection = StateDbConnectionFactory.Open(fixture.Workspace.SqliteStatePath,
            StateDbConnectionProfile.QueryOnlyRead);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json_array_length(snapshot_json, '$.Timeline') FROM goals WHERE id = $id";
        var id = command.Parameters.AddWithValue("$id", goals[0]);
        for (var i = 0; i < goals.Length; i++)
        {
            id.Value = goals[i];
            Assert.Equal((long)counts[i], (long)command.ExecuteScalar()!);
        }
        command.Parameters.Clear();
        command.CommandText = """
            SELECT count(*), sum(CASE WHEN json_valid(snapshot_json) THEN
                CASE WHEN json_type(snapshot_json, '$.Timeline') = 'array'
                    THEN length(json_extract(snapshot_json, '$.Timeline')) END END),
                sum(length(snapshot_json)) FROM goals
            """;
        long rows, chars, snapshotChars;
        using (var countsReader = command.ExecuteReader())
        {
            Assert.True(countsReader.Read());
            rows = countsReader.GetInt64(0);
            chars = countsReader.GetInt64(1);
            snapshotChars = countsReader.GetInt64(2);
        }
        Assert.Equal(7L, rows);
        Assert.True(chars > 0);
        var reader = new OwnerDigestTimelineReader();
        for (var read = 0; read < 2; read++)
        {
            reader.Read(fixture.Workspace.SqliteStatePath, new Dictionary<string, DateTimeOffset>(),
                OwnerDigestTestFixture.Start, fixture.Clock.UtcNow);
            Assert.Equal(rows, (long)reader.RowsRead);
            Assert.Equal(chars, reader.TimelineCharsRead);
            Assert.True(reader.TimelineCharsRead < snapshotChars);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_MixedGoals_MatchesReferenceForExplicitAndDefaultWindows(bool defaultWindow)
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
        void Decision(string goalId, DateTimeOffset at, string evidence)
        {
            fixture.Clock.UtcNow = at;
            kernel.RecordGoalPolicyDecision(new GoalId(goalId), "tick", new ConductorTickOutcomePayload(
                "Executed", "Verified", null, new PolicyDecisionRecord(
                    "Landing", "Execute", 3, evidence, "fixture", [])));
        }
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
        var landings = new Dictionary<string, DateTimeOffset>
        {
            [OwnerDigestTestFixture.GoalA.ToUpperInvariant()] = start.AddHours(8),
            [OwnerDigestTestFixture.GoalB] = start.AddHours(12),
            [OwnerDigestTestFixture.GoalD] = start.AddHours(-4),
            [LaterLandingGoal] = end.AddHours(2)
        };
        // Match CliOwnerDigestCommand.Read's default 24-hour window on the fixture clock.
        var until = (defaultWindow ? fixture.Clock.UtcNow : end).ToUniversalTime();
        var since = (defaultWindow ? until.AddHours(-24) : start).ToUniversalTime();
        var expected = new ReferenceReader().Read(fixture.Workspace.SqliteStatePath, landings, since, until);
        var reader = new OwnerDigestTimelineReader();
        var actual = reader.Read(fixture.Workspace.SqliteStatePath, landings, since, until);
        Assert.Equal(7, actual.Count);
        Assert.Equal(actual.Count, reader.RowsRead);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    [Theory]
    [InlineData("json_remove(snapshot_json, '$.Timeline')")]
    [InlineData("json_set(snapshot_json, '$.Timeline', 'not an array')")]
    [InlineData("json_set(snapshot_json, '$.Timeline', json('{}'))")]
    [InlineData("json_set(snapshot_json, '$.IsMetadataOnly', json('true'))")]
    [InlineData("json_insert(snapshot_json, '$.Timeline[#]', 'invalid event')")]
    [InlineData("'{'")]
    [InlineData("json_set(snapshot_json, '$.Timeline[0]', json('null'))")]
    [InlineData("json_set(snapshot_json, '$.Id', 'OTHER-ID')")]
    public async Task Read_MutatedSnapshot_MatchesReferenceAndPreservesOtherGoals(string expression)
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var landings = new Dictionary<string, DateTimeOffset>();
        var reference = new ReferenceReader();
        var baseline = reference.Read(fixture.Workspace.SqliteStatePath, landings,
            OwnerDigestTestFixture.Start, fixture.Clock.UtcNow);
        Assert.NotEmpty(baseline.Single(input => input.GoalId == OwnerDigestTestFixture.GoalC).Timeline);
        MutateSnapshot(fixture, expression);
        var expected = reference.Read(fixture.Workspace.SqliteStatePath, landings,
            OwnerDigestTestFixture.Start, fixture.Clock.UtcNow);
        var reader = new OwnerDigestTimelineReader();
        var actual = reader.Read(fixture.Workspace.SqliteStatePath, landings,
            OwnerDigestTestFixture.Start, fixture.Clock.UtcNow);
        Assert.Equal(4, actual.Count);
        Assert.Equal(actual.Count, reader.RowsRead);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        var mutatedId = expression.Contains("OTHER-ID", StringComparison.Ordinal)
            ? "OTHER-ID" : OwnerDigestTestFixture.GoalC;
        Assert.Equal(JsonSerializer.Serialize(expected.Single(input => input.GoalId == mutatedId).Timeline),
            JsonSerializer.Serialize(actual.Single(input => input.GoalId == mutatedId).Timeline));
        foreach (var unchanged in baseline.Where(input => input.GoalId != OwnerDigestTestFixture.GoalC))
            Assert.Equal(JsonSerializer.Serialize(unchanged.Timeline),
                JsonSerializer.Serialize(actual.Single(input => input.GoalId == unchanged.GoalId).Timeline));
    }

    [Fact]
    public async Task Read_MetadataOnlySnapshot_CountsRowAndMatchesReferenceWithEmptyTimeline()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        MutateSnapshot(fixture, "json_set(snapshot_json, '$.IsMetadataOnly', json('true'))");
        var landings = new Dictionary<string, DateTimeOffset>();
        var expected = new ReferenceReader().Read(fixture.Workspace.SqliteStatePath, landings,
            OwnerDigestTestFixture.Start, fixture.Clock.UtcNow);
        var reader = new OwnerDigestTimelineReader();
        var actual = reader.Read(fixture.Workspace.SqliteStatePath, landings,
            OwnerDigestTestFixture.Start, fixture.Clock.UtcNow);
        using var connection = StateDbConnectionFactory.Open(fixture.Workspace.SqliteStatePath,
            StateDbConnectionProfile.QueryOnlyRead);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM goals";
        Assert.Equal((long)command.ExecuteScalar()!, (long)reader.RowsRead);
        Assert.Empty(actual.Single(input => input.GoalId == OwnerDigestTestFixture.GoalC).Timeline);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    private static void MutateSnapshot(OwnerDigestTestFixture fixture, string expression,
        string goalId = OwnerDigestTestFixture.GoalC)
    {
        using var connection = StateDbConnectionFactory.Open(fixture.Workspace.SqliteStatePath,
            StateDbConnectionProfile.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE goals SET snapshot_json = {expression} WHERE id = $id";
        command.Parameters.AddWithValue("$id", goalId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private sealed class ReferenceReader
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            Converters = { new JsonStringEnumConverter() }
        };

        public IReadOnlyList<OwnerDigestGoalInput> Read(string stateDatabasePath,
            IReadOnlyDictionary<string, DateTimeOffset> landedAt,
            DateTimeOffset since, DateTimeOffset until)
        {
            var landings = new Dictionary<string, DateTimeOffset>(landedAt, StringComparer.OrdinalIgnoreCase);
            using var connection = StateDbConnectionFactory.Open(stateDatabasePath, StateDbConnectionProfile.QueryOnlyRead);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT goals.id,
                    CASE WHEN json_valid(snapshot_json)
                        THEN COALESCE(json_extract(snapshot_json, '$.Id'), goals.id)
                        ELSE goals.id END,
                    evt.key, evt.type, evt.value
                FROM goals
                LEFT JOIN json_each(
                    CASE WHEN json_valid(snapshot_json) THEN
                        CASE WHEN json_type(snapshot_json, '$.Timeline') = 'array'
                            AND COALESCE(json_extract(snapshot_json, '$.IsMetadataOnly'), 0) = 0
                            THEN snapshot_json END
                    END, '$.Timeline') AS evt
                ORDER BY goals.rowid, CAST(evt.key AS INTEGER)
                """;
            using var reader = command.ExecuteReader();
            var inputs = new List<OwnerDigestGoalInput>();
            string? rowId = null;
            string? goalId = null;
            var events = new List<ProgressEvent>();
            var malformed = false;
            while (reader.Read())
            {
                var nextId = reader.GetString(0);
                if (nextId != rowId)
                {
                    if (rowId is not null)
                        inputs.Add(new(goalId!, null, null, events.OrderBy(e => e.OccurredAt).ToArray()));
                    rowId = nextId;
                    goalId = reader.GetString(1);
                    events.Clear();
                    malformed = false;
                }
                if (malformed || reader.IsDBNull(2)) continue;
                try
                {
                    if (reader.GetString(3) != "object")
                        throw new JsonException("Timeline entry must be an object.");
                    var snapshot = JsonSerializer.Deserialize<ProgressEventSnapshot>(reader.GetString(4), SerializerOptions)
                        ?? throw new JsonException("Timeline entry is missing.");
                    var evt = new ProgressEvent(new GoalId(snapshot.GoalId),
                        snapshot.TaskId is null ? null : new TaskId(snapshot.TaskId),
                        snapshot.Kind, snapshot.Message, snapshot.OccurredAt, snapshot.RequeueSkipped,
                        snapshot.OperatorGates, snapshot.OperatorIntentApplied, snapshot.HumanInputSuperseded,
                        snapshot.TickOutcome);
                    // Latest decisions use window events even when they occur after a landing.
                    if ((landings.TryGetValue(goalId!, out var landing) && evt.OccurredAt <= landing) ||
                        (evt.OccurredAt >= since && evt.OccurredAt < until))
                        events.Add(evt);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
                {
                    events.Clear();
                    malformed = true;
                }
            }
            if (rowId is not null)
                inputs.Add(new(goalId!, null, null, events.OrderBy(e => e.OccurredAt).ToArray()));
            return inputs;
        }
    }
}
