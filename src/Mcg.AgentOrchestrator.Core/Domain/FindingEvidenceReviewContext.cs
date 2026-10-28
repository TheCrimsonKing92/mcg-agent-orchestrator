using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

// What was selected for delivery to one review dispatch. Null on legacy dispatches means unknown.
public sealed record FindingEvidenceReviewContext(
    string GoalId, string TaskId, string CandidateSha, DateTimeOffset DispatchAt,
    IReadOnlyList<string> ReceiptIds, IReadOnlyList<string> ExecutionIdentities)
{
    public static FindingEvidenceReviewContext Capture(Goal goal, TaskSpec task, string candidateSha,
        DateTimeOffset dispatchAt, bool includePeerEvidence = true)
    {
        var selected = task.PendingRetryCause == RetryCause.NewTestFinding
            ? task.FindingEvidenceReviewContinuation?.Selections.Select(selection => selection.ReceiptId).ToHashSet(StringComparer.Ordinal)
            : null;
        var receipts = goal.Tasks.SelectMany(source => source.VerificationHistory.SelectMany(record =>
                (record.FindingEvidenceReceipts ?? []).Where(receipt => includePeerEvidence ||
                    source.RequiredRole == task.RequiredRole || selected?.Contains(receipt.ReceiptId) == true)))
            .Where(receipt => string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new(goal.Id.Value, task.Id.Value, candidateSha, dispatchAt,
            receipts.Select(receipt => receipt.ReceiptId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            receipts.Select(ExecutionIdentity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    // Content identity ignores receipt copies, file locations, summaries and timestamps.
    public static string ExecutionIdentity(FindingEvidenceReceipt receipt) => Hash(JsonSerializer.Serialize(new
    {
        Candidate = receipt.CandidateSha.ToLowerInvariant(),
        Arms = (receipt.Arms ?? []).Select(arm => new
        {
            arm.Arm, Sha = arm.Sha.ToLowerInvariant(), arm.Disposition, arm.ExecutedTestCount,
            Artifacts = (arm.ReceiptArtifacts ?? []).Select(artifact => artifact.Sha256.ToLowerInvariant()).Order(StringComparer.Ordinal)
        }).OrderBy(arm => arm.Arm)
    }));

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record FindingEvidenceReviewSelection(
    string FindingId, string RequestIdentity, string SourceTaskId, DateTimeOffset SourceVerificationAt,
    DateTimeOffset SourceDispatchAt, string FindingRoundFingerprint, string ReceiptId, string ExecutionIdentity,
    string? RequestRoundFingerprint = null)
{
    public string Canonical => JsonSerializer.Serialize(new
    {
        FindingId, RequestIdentity, SourceTaskId, SourceVerificationAt, SourceDispatchAt, FindingRoundFingerprint, ExecutionIdentity,
        RequestRoundFingerprint
    });
}

// One reconsideration of a specific held decision, never a source verdict or an acceptance waiver.
public sealed record FindingEvidenceReviewContinuation(
    string GoalId, string TaskId, string CandidateSha, CandidateIdentity CandidateIdentity,
    DateTimeOffset PriorVerificationAt, DateTimeOffset PriorDispatchAt,
    IReadOnlyList<string> PriorExecutionIdentities, IReadOnlyList<FindingEvidenceReviewSelection> Selections)
{
    public RetryCause Cause => RetryCause.NewTestFinding;
    public string DecisionKey => FindingEvidenceReviewContext.Hash(
        $"{GoalId}:{TaskId}:{CandidateSha.ToLowerInvariant()}:{PriorVerificationAt:O}:{PriorDispatchAt:O}");
    public string Key => FindingEvidenceReviewContext.Hash(DecisionKey + ":" +
        string.Join(':', Selections.Select(selection => selection.Canonical).Order(StringComparer.Ordinal)));

    public bool AppliesTo(Goal goal, TaskSpec task, TaskVerificationRecord prior, CandidateIdentity current) =>
        GoalId == goal.Id.Value && TaskId == task.Id.Value && Selections.Count > 0 &&
        prior.CompletedAt == PriorVerificationAt && prior.DispatchStartedAt == PriorDispatchAt &&
        string.Equals(prior.ReviewedCommit, CandidateSha, StringComparison.OrdinalIgnoreCase) &&
        CandidateIdentity.AreSameCandidate(CandidateIdentity, current) &&
        Selections.Any(selection => !PriorExecutionIdentities.Contains(selection.ExecutionIdentity, StringComparer.Ordinal));

    public string Render() => $"FINDING_EVIDENCE_REVIEW_CONTINUATION cause={Cause}; key={Key}; " +
        $"candidate_sha={CandidateSha}; prior_verification={PriorVerificationAt:O}; prior_dispatch={PriorDispatchAt:O}; " +
        $"prior_evidence={string.Join(',', PriorExecutionIdentities)}; " +
        $"selected={JsonSerializer.Serialize(Selections)}; freshness=absent-from-recorded-review-context; " +
        "one bounded Reviewer reconsideration; independent review and normal acceptance remain required.";
}
