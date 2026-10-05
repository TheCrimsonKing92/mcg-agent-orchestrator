using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

public sealed class FailureClusterSourceReaderTests
{
    [Fact]
    public void ActorFixtureCountsOnlyOperatorRows()
    {
        WithWorkspace(workspace =>
        {
            using (var connection = CreateIntents(workspace, actorColumn: true))
            foreach (var line in File.ReadLines(FailureClusterFixtureData.FilePath("operator-intents-actors.jsonl")))
            {
                using var json = JsonDocument.Parse(line);
                var e = json.RootElement;
                Insert(connection, e.GetProperty("goal_id").GetString(),
                    e.GetProperty("created_at").GetString(), e.GetProperty("actor").GetString());
            }

            var input = FailureClusterSourceReader.Read(workspace, FailureClusterFixtureData.Until);
            Assert.Equal(new[] { DateTimeOffset.Parse("2026-10-01T11:00:00Z"),
                DateTimeOffset.Parse("2026-10-01T11:03:00Z") }, input.OperatorTouches.Select(t => t.At).Order().ToArray());
            Assert.All(input.OperatorTouches, t => Assert.Equal("aaaaaaaa11111111aaaaaaaa11111111", t.GoalId));
            Assert.Equal(0, input.SkippedOperatorIntents);
        });
    }

    [Fact]
    public void MissingMessagesAreValidAndMalformedRecordsAreCountedPerSource()
    {
        WithWorkspace(workspace =>
        {
            Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
            File.WriteAllLines(Path.Combine(workspace.GoalLifecycleEventsDirectory, "shapes.jsonl"), [
                """{"cursor":9,"timestamp":"2026-10-01T12:00:00.0000000+00:00","goalId":"aaaaaaaa11111111aaaaaaaa11111111","eventType":"GoalEscalated","reason":"no message property"}""",
                "{torn-record"
            ]);
            Directory.CreateDirectory(workspace.LogDirectory);
            File.WriteAllLines(workspace.ConductEventsLogPath, [
                """{"timestamp":"2026-10-05T07:36:25.4557092+00:00","eventKind":"EVIDENCE_START","goalId":"aaaaaaaa11111111aaaaaaaa11111111","goal":"aaaaaaaa11111111aaaaaaaa11111111"}""",
                """{"timestamp":"2026-10-05T07:37:25.4557092+00:00","detail":"no kind"}"""
            ]);

            var input = FailureClusterSourceReader.Read(workspace, FailureClusterFixtureData.Until);
            Assert.Equal(1, input.SkippedGoalEvents);
            Assert.Equal(1, input.SkippedConductEvents);
            Assert.Equal(0, input.SkippedOperatorIntents);
            var escalation = Assert.Single(input.GoalEvents);
            Assert.Equal("GoalEscalated", escalation.EventType);
            Assert.Equal("", escalation.Message);
            Assert.Equal("", Assert.Single(input.ConductEvents).Detail);
            Assert.Equal("GoalEscalated", Assert.Single(input.Build(
                FailureClusterFixtureData.Since, FailureClusterFixtureData.Until)).EventKind);
        });
    }

    [Fact]
    public void LegacyIntentsCountAllRowsAndWarnOnceAboutActorColumn()
    {
        WithWorkspace(workspace =>
        {
            using (var connection = CreateIntents(workspace, actorColumn: false))
                Insert(connection, "goal", "2026-10-01T11:00:00Z");
            FailureClusterInputs? input = null;
            var warning = AsyncLocalConsoleRouter.CaptureError(() =>
                input = FailureClusterSourceReader.Read(workspace, FailureClusterFixtureData.Until));

            Assert.Single(input!.OperatorTouches);
            Assert.Equal(0, input.SkippedOperatorIntents);
            var line = Assert.Single(warning.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains("actor column", line);
        });
    }

    [Fact]
    public void InvalidOperatorRowsAreCountedWithoutChargingNonOperators()
    {
        WithWorkspace(workspace =>
        {
            using (var connection = CreateIntents(workspace, actorColumn: true))
            {
                Insert(connection, "goal", "bad timestamp", "operator");
                Insert(connection, null, "2026-10-01T11:00:00Z", "operator");
                Insert(connection, "goal", null, "operator");
                Insert(connection, "goal", "bad timestamp", "steward");
                Insert(connection, "goal", "2026-10-01T11:00:00Z", "operator");
            }
            var input = FailureClusterSourceReader.Read(workspace, FailureClusterFixtureData.Until);
            Assert.Single(input.OperatorTouches);
            Assert.Equal(3, input.SkippedOperatorIntents);
            Assert.Equal(0, input.SkippedGoalEvents + input.SkippedConductEvents);
        });
    }

    [Fact]
    public void UnreadableIntentDatabaseCountsAsOneSkippedSourceFile()
    {
        WithWorkspace(workspace =>
        {
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory,
                SqliteOperatorIntentStore.DatabaseFileName), "not a sqlite database");
            FailureClusterInputs? input = null;
            var warning = AsyncLocalConsoleRouter.CaptureError(() =>
                input = FailureClusterSourceReader.Read(workspace, FailureClusterFixtureData.Until));
            Assert.Empty(input!.OperatorTouches);
            Assert.Equal(1, input.SkippedOperatorIntents);
            Assert.Contains("operator intents unavailable", warning);
        });
    }

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-cluster-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(OrchestratorWorkspace.ForDirectory(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static SqliteConnection CreateIntents(OrchestratorWorkspace workspace, bool actorColumn)
    {
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName),
            Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE operator_intents (goal_id TEXT, task_id TEXT, created_at TEXT" +
            (actorColumn ? ", actor TEXT)" : ")");
        command.ExecuteNonQuery();
        return connection;
    }

    private static void Insert(SqliteConnection connection, string? goal, string? at, string? actor = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = actor is null ? "INSERT INTO operator_intents VALUES ($goal, NULL, $at)" :
            "INSERT INTO operator_intents VALUES ($goal, NULL, $at, $actor)";
        command.Parameters.AddWithValue("$goal", (object?)goal ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", (object?)at ?? DBNull.Value);
        if (actor is not null) command.Parameters.AddWithValue("$actor", actor);
        command.ExecuteNonQuery();
    }
}
