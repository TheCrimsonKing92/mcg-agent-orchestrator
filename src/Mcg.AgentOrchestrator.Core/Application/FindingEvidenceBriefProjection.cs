using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

internal static class FindingEvidenceBriefProjection
{
    internal static void AppendContinuation(List<string> lines, Goal goal, TaskSpec task)
    {
        if (task.RequiredRole == AgentRole.Reviewer && task.PendingRetryCause == RetryCause.NewTestFinding &&
            task.FindingEvidenceReviewContinuation is { } continuation)
        {
            lines.Add(continuation.Render());
            foreach (var evidenceSelection in continuation.Selections.DistinctBy(item => item.ReceiptId))
            {
                var receipt = goal.Tasks.Single(source => source.Id.Value == evidenceSelection.SourceTaskId)
                    .VerificationHistory.Where(record => record.CompletedAt == evidenceSelection.SourceVerificationAt)
                    .SelectMany(record => record.FindingEvidenceReceipts ?? [])
                    .LastOrDefault(receipt => receipt.ReceiptId == evidenceSelection.ReceiptId &&
                        FindingEvidenceReviewContext.ExecutionIdentity(receipt) == evidenceSelection.ExecutionIdentity)
                    ?? throw new InvalidOperationException("Selected continuation receipt is unavailable during Reviewer context preparation.");
                lines.Add($"evidence_receipt: {JsonSerializer.Serialize(receipt)}");
            }
        }
    }

    internal static void AppendOwnedReceiptSummary(List<string> lines, Goal goal, AgentRole role, string receiptId)
    {
        var receipt = goal.Tasks
            .Where(candidate => candidate.RequiredRole == role)
            .SelectMany(candidate => candidate.VerificationHistory)
            .OrderByDescending(verification => verification.CompletedAt)
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .FirstOrDefault(candidate => string.Equals(candidate.ReceiptId, receiptId, StringComparison.Ordinal));
        if (receipt is not null)
        {
            lines.Add(
                $"  evidence_receipt: id={receipt.ReceiptId}; candidate_sha={receipt.CandidateSha}; " +
                $"accepted={receipt.Accepted}; passed={receipt.Passed}; summary={PromptContextFormatter.TrimPromptBlock(receipt.Summary)}");
            foreach (var arm in receipt.Arms ?? [])
            {
                lines.Add(
                    $"    evidence_arm: arm={arm.Arm.ToString().ToLowerInvariant()}; sha={arm.Sha}; " +
                    $"disposition={arm.Disposition.ToString().ToLowerInvariant()}; accepted={arm.Accepted}; " +
                    $"passed={arm.Passed}; summary={PromptContextFormatter.TrimPromptBlock(arm.Summary)}");
                if (arm.FailingTestIdentities is { Count: > 0 })
                {
                    lines.Add($"      failing_tests: {string.Join(", ", arm.FailingTestIdentities)}");
                }
            }
        }
    }
}
