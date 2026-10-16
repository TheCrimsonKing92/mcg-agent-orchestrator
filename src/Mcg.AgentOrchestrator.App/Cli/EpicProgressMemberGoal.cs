using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record EpicProgressMemberGoal(
    string Id, EpicProgressBucket Bucket, string Status, DateTimeOffset? UpdatedAt, string Title,
    DateTimeOffset? CreatedAt = null,
    GoalPromotionFact? PromotionFact = null)
{
    internal bool LandedInWindow(DateTimeOffset? since) => Bucket == EpicProgressBucket.Landed &&
        (since is null || PromotionFact?.PromotedAt >= since);

    public string LandingDateStatus => PromotionFact is null ? "unknown" : "known";
    internal string PromotionExplanation => PromotionFact is { } fact
        ? $"Original promotion: {fact.PromotedAt:O} candidate={fact.CandidateRevision} prior-main={fact.PriorMainRevision} main={fact.PromotedMainRevision} provenance={fact.Provenance}"
        : "Original promotion date: unknown (no compatible successful-promotion fact)";
}
