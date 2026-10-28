using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Cli;

// Invocation-local presentation only; the ordinary read row owns membership and claim resolution.
internal static class BacklogItemSummaryView
{
    internal sealed record Summary(string Id, string Title, string Status,
        string CreatedAt, string UpdatedAt, string? SourceGoalId, string? Priority,
        string Tags, string? SupersededBy, string? Owner, bool OwnerAmbiguous,
        string? ClaimAmbiguityReason, int AmbiguousLinkedGoalCount, long? ClaimVersion,
        string? Coverage, int LinkedGoalCount, int LineageCount, int DependencyCount,
        int DependentCount, int NoteCount, IReadOnlyList<string> OmittedSections,
        string FullDetailCommand);

    internal static Summary Project(BacklogShowAgentReadView.Row row, bool json) => new(
        row.Id, row.Title, row.Status,
        row.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        row.UpdatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        Optional(row.SourceGoalId), Optional(row.Priority), row.Tags, Optional(row.SupersededBy),
        row.OwnerAmbiguous ? null : row.OwnerGoalId, row.OwnerAmbiguous,
        row.OwnerAmbiguous ? "legacy-owner-ambiguous" : null,
        row.OwnerAmbiguous ? row.AmbiguousLinkedGoalIds.Count : 0,
        row.ClaimVersion, row.Coverage, row.LinkedGoals.Count, row.Lineage.Count,
        row.Dependencies.Count, row.Dependents.Count, row.Notes.Count,
        ["body", "notes", "linkedGoals", "lineage", "dependencies", "dependents", "ambiguousLinkedGoalIds"],
        $"backlog-show {row.Id}{(json ? " --json" : "")}");

    internal static void Write(BacklogShowAgentReadView.Row row, bool summary, bool json)
    {
        if (!summary)
        {
            Console.WriteLine(BacklogShowAgentReadView.Serialize(row));
            return;
        }
        var projection = Project(row, json);
        Console.WriteLine(json ? Serialize(projection) : Render(projection));
    }

    internal static string Serialize(Summary summary, TimeProvider? time = null) =>
        AgentReadEnvelope.Serialize("mcg.backlog-item-summary", 1, summary,
            new JsonSerializerOptions(JsonSerializerDefaults.Web), time);

    internal static string Render(Summary summary) => string.Join(Environment.NewLine,
        $"Id:      {summary.Id}",
        $"Title:   {summary.Title}",
        $"Status:  {summary.Status}",
        $"Created: {summary.CreatedAt}",
        $"Updated: {summary.UpdatedAt}",
        $"Goal:    {summary.SourceGoalId ?? "-"}",
        $"Owner:   {(summary.OwnerAmbiguous ? summary.ClaimAmbiguityReason : summary.Owner ?? "-")}",
        $"Version: {summary.ClaimVersion?.ToString(CultureInfo.InvariantCulture) ?? "-"}",
        $"Coverage: {summary.Coverage ?? "-"}",
        $"Claim ambiguity: {summary.ClaimAmbiguityReason ?? "none"}; ambiguous linked goals={summary.AmbiguousLinkedGoalCount}",
        $"Counts: linkedGoals={summary.LinkedGoalCount} lineage={summary.LineageCount} dependencies={summary.DependencyCount} dependents={summary.DependentCount} notes={summary.NoteCount}",
        $"Omitted: {string.Join(", ", summary.OmittedSections)} (body, note text and history arrays)",
        $"Full detail: {summary.FullDetailCommand}");

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
