using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private static (
        CleanTestBaselineReceipt Receipt,
        IReadOnlyList<AcceptanceCheckAttribution> Attributions)
        AttributeAcceptanceFailureWithExecutedBaseline(
            CleanTestBaselineReceipt baselineReceipt,
            IReadOnlyList<string> failedChecks,
            IReadOnlyList<CleanTestBaselineEvidence> baselineEvidence,
            GoalId goalId,
            string mainHeadSha,
            IReadOnlyList<AcceptanceCheckResult>? failedCheckReceipts)
    {
        var attributions = CleanTestBaseline.Attribute(
            baselineReceipt,
            failedChecks,
            baselineEvidence,
            goalId,
            mainHeadSha,
            failedCheckReceipts);

        // Replace the pre-gate candidate observation with the executed merge-base verdict whenever the
        // authoritative producer supplied one, so the retained attestation and brief report proof,
        // rather than correlation.
        var executedReceipt = CleanTestBaseline.WithExecutedBaselineAttestation(
            baselineReceipt,
            failedChecks,
            goalId,
            failedCheckReceipts);

        return (executedReceipt, attributions);
    }

    internal static void ReconcileCleanBaselineAttention(
        ICollaborationItemStore store,
        Goal goal,
        string? mainHeadSha,
        CleanTestBaselineReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(receipt);

        var currentCorrelationKey =
            CleanBaselineRedCorrelationKeyPrefix + (mainHeadSha?.Trim().ToLowerInvariant() ?? "unknown");
        var activeCorrelationKey = receipt.Attestation is
            CleanBaselineAttestation.AttestedRed or CleanBaselineAttestation.ObservedRedCorrelation
            ? currentCorrelationKey
            : null;
        if (activeCorrelationKey is not null)
        {
            store.RaiseAsync(
                CollaborationItemType.Decision,
                goal.Id.Value,
                CleanTestBaseline.FormatAttentionSubject(receipt, FormatShortSha(mainHeadSha)),
                CleanTestBaseline.FormatJournalDetail(receipt),
                activeCorrelationKey,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        var staleItems = store.ListAsync(cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .Where(item =>
                item.CorrelationKey is { Length: > 0 } key &&
                key.StartsWith(CleanBaselineRedCorrelationKeyPrefix, StringComparison.Ordinal) &&
                !string.Equals(key, activeCorrelationKey, StringComparison.Ordinal) &&
                (receipt.Attestation != CleanBaselineAttestation.Unattested ||
                 !string.Equals(key, currentCorrelationKey, StringComparison.Ordinal)))
            .ToArray();
        foreach (var item in staleItems)
        {
            store.TryResolveAsync(
                item.CorrelationKey!,
                $"clean-test failure correlation no longer active at main {FormatShortSha(mainHeadSha)}",
                CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
