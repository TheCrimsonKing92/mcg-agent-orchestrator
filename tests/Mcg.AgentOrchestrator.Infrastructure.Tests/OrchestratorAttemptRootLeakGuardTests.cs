using Microsoft.Data.Sqlite;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OrchestratorAttemptRootLeakGuardTests
{
    [Fact]
    public void NewNonGoalEntriesAreFlaggedAndExistingEntriesAreIgnored()
    {
        var repository = Path.Combine(Path.GetTempPath(), $"mcg-attempt-root-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        try
        {
            var createdByThisProcess = "feedfacefeedfacefeedfacefeedface";
            GoalAcceptanceVerifier.ResolveOwnerResultsPrefix(repository, new GoalId(createdByThisProcess), "gate");
            var root = Path.Combine(repository, ".orchestrator", "acceptance-gate-attempts");
            var createdByAnotherProcess = Path.Combine(root, "other-process");
            var unmarked = Path.Combine(root, "unmarked");
            Directory.CreateDirectory(createdByAnotherProcess);
            Directory.CreateDirectory(unmarked);
            File.WriteAllText(
                Path.Combine(createdByAnotherProcess, GoalAcceptanceVerifier.OwnerResultsCreatorFileName),
                $"{Environment.ProcessId}:0");

            var baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "old-fixture" };
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "old-fixture", createdByThisProcess, "other-process", "unmarked", "operator",
                "aabbccddaabbccddaabbccddaabbccdd"
            };
            var goals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "aabbccddaabbccddaabbccddaabbccdd"
            };

            Assert.Equal([createdByThisProcess], OrchestratorAttemptRootLeakGuardFixture.FindLeaks(root, baseline, current, goals));
            Assert.Equal(
                ["other-process", "unmarked"],
                OrchestratorAttemptRootLeakGuardFixture.FindIgnoredEntries(root, baseline, current, goals));
            using var diagnostics = new StringWriter();
            OrchestratorAttemptRootLeakGuardFixture.WriteIgnoredEntries(
                diagnostics, "acceptance-gate-attempts", root, baseline, current, goals);
            Assert.Equal(
                $"attempt-root-leak-guard ignored root=acceptance-gate-attempts entry=other-process reason=creator-not-current-process{Environment.NewLine}" +
                $"attempt-root-leak-guard ignored root=acceptance-gate-attempts entry=unmarked reason=creator-not-current-process{Environment.NewLine}",
                diagnostics.ToString());
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
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
                root,
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
