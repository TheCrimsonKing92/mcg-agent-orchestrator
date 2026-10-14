using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class BacklogListAgentReadView
{
    internal sealed record Row(string Id, string Title, string Status, string? GoalId,
        bool OwnerAmbiguous, bool Eligible, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        string? Priority, string Tags, string? SupersededBy);

    internal static string Serialize(IReadOnlyList<Row> rows, TimeProvider? time = null) =>
        AgentReadEnvelope.Serialize(AgentReadEnvelope.BacklogListSchema, AgentReadEnvelope.BacklogListSchemaVersion,
            new
            {
                items = rows.Select(row => new
                {
                    id = row.Id,
                    status = row.Status,
                    goalId = row.OwnerAmbiguous ? null : row.GoalId,
                    ownerAmbiguous = row.OwnerAmbiguous,
                    eligible = row.Eligible,
                    title = row.Title,
                    createdAt = row.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    updatedAt = row.UpdatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    priority = string.IsNullOrEmpty(row.Priority) ? null : row.Priority,
                    tags = row.Tags,
                    supersededBy = string.IsNullOrEmpty(row.SupersededBy) ? null : row.SupersededBy
                }).ToArray()
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web), time);
}
