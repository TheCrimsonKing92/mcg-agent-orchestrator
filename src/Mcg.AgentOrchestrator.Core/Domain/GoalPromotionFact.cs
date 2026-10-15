namespace Mcg.AgentOrchestrator.Core;

// An observation of a successful native main compare-and-swap, never an intent
// or a later confirmation of reachability. One original fact belongs to each goal.
public sealed record GoalPromotionFact
{
    public const string SoloProvenance = "native-main-compare-and-swap";
    public const string CohortProvenance = "cohort-main-compare-and-swap";
    public const string MergeTrainProvenance = "merge-train-main-compare-and-swap";

    public GoalPromotionFact(string goalId, string candidateRevision, string priorMainRevision,
        string promotedMainRevision, DateTimeOffset promotedAt, string provenance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        if (promotedAt == default || promotedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Promotion time must be a non-default UTC instant.", nameof(promotedAt));
        if (provenance is not (SoloProvenance or CohortProvenance or MergeTrainProvenance))
            throw new ArgumentException("Unrecognized successful promotion provenance.", nameof(provenance));
        GoalId = goalId;
        CandidateRevision = Revision(candidateRevision);
        PriorMainRevision = Revision(priorMainRevision);
        PromotedMainRevision = Revision(promotedMainRevision);
        if (PriorMainRevision == PromotedMainRevision)
            throw new ArgumentException("A promotion must advance main.");
        PromotedAt = promotedAt;
        Provenance = provenance;
    }

    public string GoalId { get; }
    public string CandidateRevision { get; }
    public string PriorMainRevision { get; }
    public string PromotedMainRevision { get; }
    public DateTimeOffset PromotedAt { get; }
    public string Provenance { get; }

    private static string Revision(string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        if (revision.Length is not (40 or 64) || !revision.All(Uri.IsHexDigit))
            throw new ArgumentException("Promotion identities require full Git object revisions.", nameof(revision));
        return revision.ToLowerInvariant();
    }
}
