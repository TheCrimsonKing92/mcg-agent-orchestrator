using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: schema operations use only the fixture's unique database.
public sealed class SqliteOperatorLessonUntilGoalTests
{
    [Fact]
    public void LegacySchema_ReadsWithoutMigrationAndWriteAddsNullableColumn()
    {
        using var fixture = new OperatorLessonHarness();
        CreateLegacyDatabase(fixture.Workspace.OperatorLessonsStorePath);

        var old = Assert.Single(fixture.LessonStore.List());

        Assert.Equal("legacy", old.Id);
        Assert.Null(old.UntilGoalId);
        Assert.DoesNotContain("untilGoalId", JsonSerializer.Serialize(old,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.Ordinal);
        Assert.False(HasUntilColumn(fixture.Workspace.OperatorLessonsStorePath));
        var goalId = Guid.NewGuid().ToString("N");
        OperatorLessonUntilGoalRetirementTests.Add(fixture, "new", goalId);
        Assert.True(HasUntilColumn(fixture.Workspace.OperatorLessonsStorePath));
        Assert.Null(Assert.Single(fixture.LessonStore.List(), lesson => lesson.Id == "legacy").UntilGoalId);
        Assert.Equal(goalId, Assert.Single(fixture.LessonStore.List(), lesson => lesson.Id == "new").UntilGoalId);
        Assert.False(fixture.LessonStore.TryAppendLesson(old, "legacy-source"));
        Assert.Equal(2, fixture.LessonStore.List().Count);
    }

    private static void CreateLegacyDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE lessons (
                id TEXT PRIMARY KEY, source_intent_id TEXT NOT NULL UNIQUE,
                situation TEXT NOT NULL, rule TEXT NOT NULL, applies_to_json TEXT NOT NULL,
                evidence_json TEXT NOT NULL, actor TEXT NOT NULL, actor_kind TEXT NOT NULL,
                channel TEXT NOT NULL, recorded_at TEXT NOT NULL, goal_id TEXT);
            CREATE TABLE lesson_retirements (
                lesson_id TEXT PRIMARY KEY REFERENCES lessons(id), source_intent_id TEXT NOT NULL UNIQUE,
                reason TEXT NOT NULL, evidence_json TEXT NOT NULL, actor TEXT NOT NULL,
                actor_kind TEXT NOT NULL, channel TEXT NOT NULL, retired_at TEXT NOT NULL);
            INSERT INTO lessons VALUES ('legacy', 'legacy-source', 'Old situation', 'Old rule',
                '["steward"]', '[]', 'operator', 'Human', 'cli', '2026-09-28T12:00:00+00:00', NULL);
            """;
        command.ExecuteNonQuery();
    }

    private static bool HasUntilColumn(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('lessons') WHERE name='until_goal_id'";
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }
}
