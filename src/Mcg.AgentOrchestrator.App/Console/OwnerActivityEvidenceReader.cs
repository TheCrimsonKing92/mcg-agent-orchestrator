using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Read-only access: opening the owner console must never create or migrate a conductor store.
internal sealed class OwnerActivityEvidenceReader(string databasePath)
{
    internal OwnerActivityTestEvidence? Read(OwnerConductEvent item)
    {
        var id = OwnerActivityNarrator.Field(item, "receipt");
        if (id is null || !File.Exists(databasePath)) return null;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT failed_checks_json, gate_test_result_paths_json, attributed_members_json
                FROM cohort_receipts WHERE receipt_id=$id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var checks = JsonSerializer.Deserialize<string[]>(reader.GetString(0)) ?? [];
            var paths = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [];
            var own = JsonSerializer.Deserialize<AcceptanceCohortAttributedMember[]>(reader.GetString(2)) ?? [];
            var tests = own.SelectMany(member => member.ReproducedFailingTests).Distinct().ToArray();
            if (tests.Length == 0) tests = paths.SelectMany(path => AcceptanceTrxFailureReader.Read(path).Failures)
                .Select(failure => failure.TestName).OfType<string>().Distinct().ToArray();
            return new(tests, checks, own.Length > 0);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }
}
