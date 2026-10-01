using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OwnerReviewHoldReceipt(
    string? Fingerprint, DateTimeOffset FailureOccurredAt, string Reason);

internal static partial class GoalOperationJournal
{
    internal const string OwnerReviewHoldOperation = "conductor:owner-review-hold";

    internal static void OwnerReviewHoldEntered(
        string executionDirectory, Goal goal, string sha, string? mainSha,
        OwnerReviewHoldReceipt receipt, IReadOnlyList<string> failedChecks) =>
        Append(executionDirectory, goal.Id,
            $"{Key(goal.Id, OwnerReviewHoldOperation)}:{sha}:{receipt.Fingerprint ?? "none"}",
            OwnerReviewHoldOperation, GoalOperationStatus.Completed,
            JsonSerializer.Serialize(receipt, JsonOptions), sha, mainSha, null,
            failedCheckNames: failedChecks);

    internal static OwnerReviewHoldReceipt? ReadOwnerReviewHold(GoalOperationJournalEntry entry) =>
        entry.Operation == OwnerReviewHoldOperation && entry.Status == GoalOperationStatus.Completed &&
        entry.Detail is not null
            ? JsonSerializer.Deserialize<OwnerReviewHoldReceipt>(entry.Detail, JsonOptions)
            : null;
}
