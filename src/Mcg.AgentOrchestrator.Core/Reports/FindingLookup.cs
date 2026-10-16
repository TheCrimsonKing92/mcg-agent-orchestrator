namespace Mcg.AgentOrchestrator.Core;

/// <summary>Projects finding occurrences and their same-record evidence from loaded goals.</summary>
public static class FindingLookup
{
    public const string MissingReceiptReasonPrefix = "The verification record holds no receipt for the evidence request of finding";

    public static string MissingReceiptReason(string stableId) =>
        $"{MissingReceiptReasonPrefix} '{stableId}'.";

    public static FindingLookupResult Find(IEnumerable<Goal> goals, string stableIdOrPrefix)
    {
        ArgumentNullException.ThrowIfNull(goals);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableIdOrPrefix);
        var occurrences = goals.SelectMany(goal => goal.Tasks.SelectMany(task =>
            task.VerificationHistory.SelectMany(record => (record.MergedReviewFindings ?? [])
                .Where(finding => finding.StableId.StartsWith(stableIdOrPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(finding =>
                {
                    var receipts = (record.FindingEvidenceReceipts ?? [])
                        .Where(receipt => RefersTo(receipt, finding)).ToArray();
                    return new FindingLookupMatch(goal.Id, task.Id, TaskDisplayNumber.Resolve(goal, task.Id),
                        task.RequiredRole, record.ReviewedCommit, record.CompletedAt, finding, receipts,
                        finding.EvidenceRequest is not null && receipts.Length == 0
                            ? MissingReceiptReason(finding.StableId) : null);
                })))).ToArray();

        var exact = occurrences.Where(match => string.Equals(match.Finding.StableId, stableIdOrPrefix,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        return new FindingLookupResult(stableIdOrPrefix, (exact.Length > 0 ? exact : occurrences)
            .OrderBy(match => match.GoalId.Value, StringComparer.Ordinal)
            .ThenBy(match => match.TaskNumber).ThenBy(match => match.CompletedAt).ToArray());
    }

    private static bool RefersTo(FindingEvidenceReceipt receipt, ReviewFinding finding)
    {
        if (receipt.Request is null || finding.EvidenceRequest is null)
            return false;
        if (finding.EvidenceOutcome?.ReceiptId is { } receiptId &&
            string.Equals(receipt.ReceiptId, receiptId, StringComparison.Ordinal))
            return true;
        var dispositions = receipt.RequestDispositions ?? [];
        if (dispositions.Any(item => string.Equals(item.FindingStableId, finding.StableId, StringComparison.Ordinal)))
            return true;

        // Legacy receipts have no finding references. Never use request equivalence when
        // explicit dispositions attribute the receipt to a different finding.
        return dispositions.Count == 0 && finding.EvidenceRequest is { } request &&
            string.Equals(FindingEvidenceExecutionClassifier.BuildRequestIdentity(receipt.Request),
                FindingEvidenceExecutionClassifier.BuildRequestIdentity(request), StringComparison.Ordinal);
    }
}

public sealed record FindingLookupResult(string Query, IReadOnlyList<FindingLookupMatch> Matches);

public sealed record FindingLookupMatch(
    GoalId GoalId,
    TaskId TaskId,
    int TaskNumber,
    AgentRole Role,
    string? ReviewedCommit,
    DateTimeOffset CompletedAt,
    ReviewFinding Finding,
    IReadOnlyList<FindingEvidenceReceipt> Receipts,
    string? MissingReceiptReason);
