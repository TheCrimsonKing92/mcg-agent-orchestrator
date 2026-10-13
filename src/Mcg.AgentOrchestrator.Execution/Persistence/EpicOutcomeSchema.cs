using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class EpicOutcomeSchema
{
    internal static void Setup(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS epic_outcome_revisions (
                epic_id TEXT NOT NULL REFERENCES epics(id), version INTEGER NOT NULL CHECK(version > 0),
                payload_json TEXT NOT NULL, PRIMARY KEY(epic_id, version));
            CREATE TABLE IF NOT EXISTS epic_outcome_observations (
                epic_id TEXT NOT NULL, bar_version INTEGER NOT NULL, requirement_id TEXT NOT NULL,
                sequence INTEGER NOT NULL CHECK(sequence > 0), receipt_id TEXT NOT NULL UNIQUE,
                payload_json TEXT NOT NULL,
                PRIMARY KEY(epic_id, bar_version, requirement_id, sequence),
                FOREIGN KEY(epic_id, bar_version) REFERENCES epic_outcome_revisions(epic_id, version));
            CREATE TABLE IF NOT EXISTS epic_outcome_applied_requests (
                request_id TEXT PRIMARY KEY, epic_id TEXT NOT NULL REFERENCES epics(id),
                request_hash TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
}
