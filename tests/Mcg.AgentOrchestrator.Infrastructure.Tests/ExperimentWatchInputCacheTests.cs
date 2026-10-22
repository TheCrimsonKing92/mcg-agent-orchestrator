using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every fact owns its database and journal directory; observation times are fixed.
public sealed class ExperimentWatchInputCacheTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GoalReread_HydratesOnlyChangedAndNewGoals()
    {
        WithWorkspace(workspace =>
        {
            var snapshots = Seed(workspace);
            var cache = new ExperimentWatchInputCache();
            var first = cache.ReadGoals(workspace.SqliteStatePath, AsOf);
            Assert.Equal(5, first.Count);
            Assert.Equal(5, cache.LastHydratedGoalCount);
            AssertGoalsEqual(ExperimentGoals.Read(workspace.SqliteStatePath), first);

            var changed = snapshots[0] with { Status = GoalStatus.Cancelled };
            var added = Snapshot('0', 3);
            new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
                .SaveGoalSnapshotsAsync([changed, added]).GetAwaiter().GetResult();

            var second = cache.ReadGoals(workspace.SqliteStatePath, AsOf.AddMinutes(10));
            Assert.Equal(2, cache.LastHydratedGoalCount);
            Assert.Equal(6, second.Count);
            Assert.Equal(GoalStatus.Cancelled, second.Single(goal => goal.Id.Value == changed.Id).Status);
            Assert.Equal(3, second.Single(goal => goal.Id.Value == added.Id).Tasks.Count);
            AssertGoalsEqual(ExperimentGoals.Read(workspace.SqliteStatePath), second);
        });
    }

    [Fact]
    public void GoalReread_UnchangedStateHydratesNothingAndFullRefreshAfterTheInterval()
    {
        WithWorkspace(workspace =>
        {
            Seed(workspace);
            var cache = new ExperimentWatchInputCache();
            var first = cache.ReadGoals(workspace.SqliteStatePath, AsOf);
            Assert.Equal(5, cache.LastHydratedGoalCount);

            var unchanged = cache.ReadGoals(workspace.SqliteStatePath, AsOf.AddMinutes(59));
            Assert.Equal(0, cache.LastHydratedGoalCount);
            AssertGoalsEqual(first, unchanged);
            foreach (var goal in unchanged)
                Assert.Same(first.Single(previous => previous.Id == goal.Id), goal);

            var refreshed = cache.ReadGoals(workspace.SqliteStatePath, AsOf.AddMinutes(60));
            Assert.Equal(5, cache.LastHydratedGoalCount);
            AssertGoalsEqual(ExperimentGoals.Read(workspace.SqliteStatePath), refreshed);
            foreach (var goal in refreshed)
                Assert.NotSame(first.Single(previous => previous.Id == goal.Id), goal);
        });
    }

    [Fact]
    public void GoalReread_DropsGoalsNoLongerListed()
    {
        WithWorkspace(workspace =>
        {
            var snapshots = Seed(workspace);
            var cache = new ExperimentWatchInputCache();
            Assert.Equal(5, cache.ReadGoals(workspace.SqliteStatePath, AsOf).Count);

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = workspace.SqliteStatePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM goals WHERE id = $id";
                command.Parameters.AddWithValue("$id", snapshots[0].Id);
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var second = cache.ReadGoals(workspace.SqliteStatePath, AsOf.AddMinutes(10));
            Assert.Equal(0, cache.LastHydratedGoalCount);
            Assert.Equal(4, second.Count);
            Assert.DoesNotContain(second, goal => goal.Id.Value == snapshots[0].Id);
            AssertGoalsEqual(ExperimentGoals.Read(workspace.SqliteStatePath), second);
        });
    }

    [Fact]
    public void LandingReread_ParsesOnlyChangedJournals()
    {
        WithWorkspace(workspace =>
        {
            var directory = workspace.GoalLifecycleEventsDirectory;
            Directory.CreateDirectory(directory);
            var firstPath = Path.Combine(directory, "a.jsonl");
            var removedPath = Path.Combine(directory, "b.jsonl");
            File.WriteAllLines(firstPath, [Landing("shared", AsOf), "malformed", "{}"]);
            File.WriteAllLines(removedPath, [Landing("removed", AsOf)]);
            File.WriteAllLines(Path.Combine(directory, "c.jsonl"),
                [Landing("shared", AsOf.AddHours(-1)), Landing("retained", AsOf)]);
            var cache = new ExperimentWatchInputCache();
            var first = cache.ReadLandings(directory);
            Assert.Equal(3, cache.LastParsedJournalCount);
            Assert.Equal(3, first.Count);
            Assert.Equal(AsOf.AddHours(-1), first["shared"]);

            File.AppendAllLines(firstPath, [Landing("shared", AsOf.AddHours(-2))]);
            File.Delete(removedPath);
            var second = cache.ReadLandings(directory);
            Assert.Equal(1, cache.LastParsedJournalCount);
            Assert.Equal(2, second.Count);
            Assert.Equal(AsOf.AddHours(-2), second["shared"]);
            Assert.False(second.ContainsKey("removed"));
            AssertLandingsEqual(ExperimentLandingTimes.Read(directory), second);

            AssertLandingsEqual(second, cache.ReadLandings(directory));
            Assert.Equal(0, cache.LastParsedJournalCount);
        });
    }

    [Fact]
    public void LandingReread_DetectsWriteTimeChangeWithoutLengthChangeAndNewJournal()
    {
        WithWorkspace(workspace =>
        {
            var directory = workspace.GoalLifecycleEventsDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "a.jsonl");
            File.WriteAllLines(path, [Landing("shared", AsOf)]);
            File.SetLastWriteTimeUtc(path, AsOf.UtcDateTime);
            var length = new FileInfo(path).Length;
            var cache = new ExperimentWatchInputCache();
            Assert.Equal(AsOf, cache.ReadLandings(directory)["shared"]);

            File.WriteAllLines(path, [Landing("shared", AsOf.AddHours(1))]);
            File.SetLastWriteTimeUtc(path, AsOf.AddMinutes(1).UtcDateTime);
            Assert.Equal(length, new FileInfo(path).Length);
            File.WriteAllLines(Path.Combine(directory, "new.jsonl"), [Landing("new", AsOf)]);
            var second = cache.ReadLandings(directory);
            Assert.Equal(2, cache.LastParsedJournalCount);
            Assert.Equal(AsOf.AddHours(1), second["shared"]);
            Assert.Equal(2, second.Count);
            AssertLandingsEqual(ExperimentLandingTimes.Read(directory), second);
        });
    }

    private static GoalSnapshot[] Seed(OrchestratorWorkspace workspace)
    {
        // Save order intentionally differs from primary-key and metadata order.
        var snapshots = new[] { Snapshot('c', 1), Snapshot('a', 2), Snapshot('e', 3),
            Snapshot('b', 1), Snapshot('d', 2) };
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            .SaveGoalSnapshotsAsync(snapshots).GetAwaiter().GetResult();
        return snapshots;
    }

    private static GoalSnapshot Snapshot(char key, int taskCount)
    {
        var id = new string(key, 32);
        var tasks = Enumerable.Range(0, taskCount).Select(index => new TaskSnapshot($"{id}-{index}",
            "Implement", AgentRole.Developer, WorkTaskStatus.Completed, null, null, null, [], null, null)).ToArray();
        return new(id, id, GoalStatus.Completed, tasks, [], CreatedAt: AsOf.AddDays(-1));
    }

    private static string Landing(string id, DateTimeOffset timestamp) =>
        JsonSerializer.Serialize(new { eventType = "GoalLanded", goalId = id, timestamp });

    private static void AssertGoalsEqual(IReadOnlyCollection<Goal> expected, IReadOnlyCollection<Goal> actual) =>
        Assert.Equal(expected.Select(goal => (goal.Id.Value, goal.Status, goal.Tasks.Count)).ToArray(),
            actual.Select(goal => (goal.Id.Value, goal.Status, goal.Tasks.Count)).ToArray());

    private static void AssertLandingsEqual(IReadOnlyDictionary<string, DateTimeOffset> expected,
        IReadOnlyDictionary<string, DateTimeOffset> actual) =>
        Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "experiment-watch-cache-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try { action(OrchestratorWorkspace.ForDirectory(root)); }
        finally { Directory.Delete(root, true); }
    }
}
