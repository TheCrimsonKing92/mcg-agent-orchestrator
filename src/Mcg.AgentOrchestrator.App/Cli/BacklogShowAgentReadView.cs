using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class BacklogShowAgentReadView
{
    internal sealed record Row(string Id, string Title, string Status,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? SourceGoalId,
        string? Priority, string Tags, string? SupersededBy, string? OwnerGoalId,
        bool OwnerAmbiguous, IReadOnlyList<string> AmbiguousLinkedGoalIds,
        long? ClaimVersion, string? Coverage, IReadOnlyList<LinkedGoal> LinkedGoals,
        IReadOnlyList<Lineage> Lineage, IReadOnlyList<Dependency> Dependencies,
        IReadOnlyList<string> Dependents, string Body, IReadOnlyList<Note> Notes);

    internal sealed record LinkedGoal(string GoalId, string Status, string Authority, string Landing);
    internal sealed record Lineage(string PredecessorGoalId, string SuccessorGoalId,
        Guid RequestId, DateTimeOffset ReplacedAt);
    internal sealed record Dependency(string PrerequisiteId, string TargetKind, string State);
    internal sealed record Note(DateTimeOffset CreatedAt, string Text);

    internal static Row Read(CliExecutionContext context, BacklogStore store, BacklogItem item)
    {
        var claimStore = new SourceBacklogClaimStore(context.Workspace.SqliteStatePath);
        var claim = CliCommandHandlers.ResolveSourceBacklogClaimForRead(context, item.Id, out var ambiguity);
        var linkedGoals = CliCommandHandlers.BacklogReadGoalsForItem(context, item.Id)
            .Where(goal => string.Equals(goal.SourceBacklogItemId, item.Id, StringComparison.Ordinal))
            .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .Select(goal => new LinkedGoal(goal.Id.Value, goal.Status.ToString(),
                ambiguity is not null ? "ambiguous"
                    : claim?.OwnerGoalId == goal.Id.Value ? "authoritative" : "historical",
                CliCommandHandlers.ResolveBacklogShowGoalLandingState(context, goal)))
            .ToArray();
        var lineage = claimStore.ListLineage(item.Id)
            .Select(replacement => new Lineage(replacement.PredecessorGoalId, replacement.SuccessorGoalId,
                replacement.RequestId, replacement.ReplacedAt)).ToArray();
        return new Row(item.Id, item.Title, item.Status.ToString(), item.CreatedAt, item.UpdatedAt,
            item.SourceGoalId, item.Priority, item.Tags, item.SupersededBy,
            ambiguity is null ? claim?.OwnerGoalId : null, ambiguity is not null,
            ambiguity?.LinkedGoalIds ?? [], claim?.Version, claim?.Coverage.ToString().ToLowerInvariant(),
            linkedGoals, lineage,
            item.Dependencies.Select(dependency => new Dependency(dependency.PrerequisiteId,
                dependency.TargetKind.ToString().ToLowerInvariant(),
                CliCommandHandlers.RenderBacklogDependencyState(context, store, dependency))).ToArray(),
            item.Dependents.Select(dependent => dependent.DependentId).ToArray(), item.Body,
            item.Notes.Select(note => new Note(note.CreatedAt, note.Text)).ToArray());
    }

    internal static string Serialize(Row row, TimeProvider? time = null) =>
        AgentReadEnvelope.Serialize(AgentReadEnvelope.BacklogItemSchema, AgentReadEnvelope.BacklogItemSchemaVersion,
            new
            {
                id = row.Id,
                title = row.Title,
                status = row.Status,
                createdAt = row.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                updatedAt = row.UpdatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                sourceGoalId = string.IsNullOrWhiteSpace(row.SourceGoalId) ? null : row.SourceGoalId,
                priority = string.IsNullOrWhiteSpace(row.Priority) ? null : row.Priority,
                tags = row.Tags,
                supersededBy = string.IsNullOrWhiteSpace(row.SupersededBy) ? null : row.SupersededBy,
                owner = row.OwnerAmbiguous ? null : row.OwnerGoalId,
                ownerAmbiguous = row.OwnerAmbiguous,
                ambiguousLinkedGoalIds = row.OwnerAmbiguous ? row.AmbiguousLinkedGoalIds : [],
                claimVersion = row.ClaimVersion,
                coverage = row.Coverage,
                linkedGoals = row.LinkedGoals,
                lineage = row.Lineage.Select(replacement => new
                {
                    predecessorGoalId = replacement.PredecessorGoalId,
                    successorGoalId = replacement.SuccessorGoalId,
                    requestId = replacement.RequestId.ToString("D"),
                    replacedAt = replacement.ReplacedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                }).ToArray(),
                dependencies = row.Dependencies,
                dependents = row.Dependents,
                body = row.Body,
                notes = row.Notes.Select(note => new
                {
                    createdAt = note.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    text = note.Text
                }).ToArray()
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web), time);
}
