using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliOwnerDigestRetryIntents
{
    internal static IReadOnlyList<AppliedRetryIntent> Read(OrchestratorWorkspace workspace, DateTimeOffset until)
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("Warning: owner-digest retry intents unavailable: operator-intents.db is missing.");
            return [];
        }
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT task_id, completed_at, actor_kind FROM operator_intents
                WHERE status = 'Applied' AND verb IN ('retry', 'adjudicate')
                    AND task_id IS NOT NULL AND completed_at IS NOT NULL
                """;
            using var reader = command.ExecuteReader();
            var intents = new List<AppliedRetryIntent>();
            while (reader.Read())
            {
                if (!DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var appliedAt) || appliedAt >= until) continue;
                var actor = OperatorActorKind.Human;
                if (!reader.IsDBNull(2) && (!Enum.TryParse(reader.GetString(2), out actor) ||
                    !Enum.IsDefined(actor))) continue;
                // No lower bound: a retry just before Since can explain a dispatch inside the window.
                intents.Add(new AppliedRetryIntent(reader.GetString(0), appliedAt, actor));
            }
            return intents;
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine($"Warning: owner-digest retry intents unavailable: {ex.Message}");
            return [];
        }
    }
}
