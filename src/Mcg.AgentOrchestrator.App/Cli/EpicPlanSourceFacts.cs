using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal enum EpicPlanOwnershipResolution { Unclaimed, Persisted, Legacy, Ambiguous, MissingOwner, InvalidCoverage }
internal sealed record EpicPlanLinkedGoal(string Id, string Status, string? Coverage);
internal sealed record EpicPlanSourceClaim(string OwnerGoalId, string? Coverage);

// Backlog owns scope completion; claims own current ownership; links retain delivery history.
internal sealed record EpicPlanSourceFacts(BacklogItemStatus SourceStatus, string? OwnerGoalId,
    SourceBacklogCoverage? Coverage, EpicPlanOwnershipResolution Resolution, EpicPlanLinkedGoal? Owner,
    IReadOnlyList<EpicPlanLinkedGoal> LinkedGoals, IReadOnlyList<EpicPlanLinkedGoal> DeliveredGoals)
{
    internal bool ScopeComplete => SourceStatus is BacklogItemStatus.Done or BacklogItemStatus.Superseded;
    internal bool AutomaticIntakeEligible => SourceStatus == BacklogItemStatus.Open &&
        Resolution == EpicPlanOwnershipResolution.Unclaimed;

    internal string Render()
    {
        var ownership = OwnerGoalId is null ? "none" : $"{Short(OwnerGoalId)} {Owner?.Status ?? "Missing"}";
        var delivered = DeliveredGoals.Count == 0 ? "none" :
            string.Join(", ", DeliveredGoals.Select(goal => $"{Short(goal.Id)} {goal.Status}"));
        var linked = LinkedGoals.Count == 0 ? "none" :
            string.Join(", ", LinkedGoals.Select(goal => $"{Short(goal.Id)} {goal.Status}"));
        return $"Source: {SourceStatus}; owner: {ownership}; coverage: {Coverage?.ToString() ?? "unknown"}; " +
            $"ownership: {Resolution}; delivered: {delivered}; linked: {linked}";
    }

    private static string Short(string id) => id[..Math.Min(8, id.Length)];
}
