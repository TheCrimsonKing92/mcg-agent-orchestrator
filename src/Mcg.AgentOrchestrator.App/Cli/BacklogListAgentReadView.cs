using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class BacklogListAgentReadView
{
    internal static IReadOnlyList<BacklogItem> SelectItems(
        IReadOnlyList<BacklogItem> items, bool newestFirst, int? limit)
    {
        IEnumerable<BacklogItem> selected = newestFirst
            ? items.OrderByDescending(item => item.CreatedAt)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
            : items;
        if (limit is { } cap)
            selected = selected.Take(cap);
        return selected.ToArray();
    }

    internal sealed record Row(string Id, string Title, string Status, string? GoalId,
        bool OwnerAmbiguous, bool Eligible, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        string? Priority, string Tags, string? SupersededBy);

    internal static Row CreateRow(BacklogItem item, string status, string? goalId,
        bool ownerAmbiguous, bool eligible) =>
        new(item.Id, item.Title, status, goalId, ownerAmbiguous, eligible,
            item.CreatedAt, item.UpdatedAt, item.Priority, item.Tags, item.SupersededBy);

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
