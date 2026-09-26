using Microsoft.Data.Sqlite;

public sealed class OrchestratorAttemptRootLeakGuardTests
{
    [Fact]
    public void NewNonGoalEntriesAreFlaggedAndExistingEntriesAreIgnored()
    {
        var baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "old-fixture" };
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "old-fixture", "new-fixture", "operator", "aabbccddaabbccddaabbccddaabbccdd"
        };
        var goals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "aabbccddaabbccddaabbccddaabbccdd"
        };

        Assert.Equal(["new-fixture"], OrchestratorAttemptRootLeakGuardFixture.FindLeaks(baseline, current, goals));
    }

    [Fact]
    public void MissingAttemptRootSnapshotsAsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"mcg-absent-attempt-root-{Guid.NewGuid():N}");
        Assert.Empty(OrchestratorAttemptRootLeakGuardFixture.Snapshot(missing));
    }

    [Fact]
    public void GoalIdsComeFromReadOnlyStateDbAndMissingDbSkips()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-attempt-root-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state.db");
        try
        {
            Assert.False(OrchestratorAttemptRootLeakGuardFixture.TryReadGoalIds(database, out _));
            using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE goals (id TEXT PRIMARY KEY); INSERT INTO goals (id) VALUES ('aabbccddaabbccddaabbccddaabbccdd');";
                command.ExecuteNonQuery();
            }

            Assert.True(OrchestratorAttemptRootLeakGuardFixture.TryReadGoalIds(database, out var goals));
            Assert.Contains("aabbccddaabbccddaabbccddaabbccdd", goals);
            Assert.Empty(OrchestratorAttemptRootLeakGuardFixture.FindLeaks(
                new HashSet<string>(),
                new HashSet<string> { "aabbccddaabbccddaabbccddaabbccdd", "operator" },
                goals));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
