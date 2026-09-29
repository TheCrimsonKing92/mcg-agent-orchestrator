using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAcceptanceCohortAttributionVerdict(
    string Action,
    string Reason,
    string PartitionReceiptId,
    IReadOnlyList<string> FailedChecks,
    string Detail,
    bool AlreadyRecorded = false)
{
    internal bool ShouldRecord => Action == "recorded";

    internal string LogLine(GoalId goalId) =>
        $"ACCEPTANCE_COHORT_ATTRIBUTION_VERDICT goal={goalId.Value} action={Action} " +
        $"reason={Reason} partition={PartitionReceiptId}" +
        (AlreadyRecorded ? " disposition=already-recorded" : string.Empty);
}

internal static class ConductorAcceptanceCohortAttributionVerdicts
{
    internal const string EvidencePrefix = "cohort-attribution=";

    internal static ConductorAcceptanceCohortAttributionVerdict Apply(
        AcceptanceCohortReceipt cohort,
        Goal goal,
        AcceptanceCohortPartitionReceipt? partition,
        string? currentCandidate,
        string? currentMain,
        bool alreadyRecorded,
        Action<Goal, IReadOnlyList<string>, string, string, IReadOnlyList<AcceptanceCheckAttribution>> record,
        Action<string> emit)
    {
        var verdict = Evaluate(cohort, goal, partition, currentCandidate, currentMain, alreadyRecorded);
        if (verdict.ShouldRecord)
        {
            var attributions = verdict.FailedChecks.Select(check =>
                new AcceptanceCheckAttribution(check, AcceptanceFailureOrigin.Introduced, verdict.Detail)).ToArray();
            record(goal, verdict.FailedChecks, partition!.CandidateRevision,
                partition.ObservedMainRevision, attributions);
        }
        emit(verdict.LogLine(goal.Id));
        return verdict;
    }

    internal static ConductorAcceptanceCohortAttributionVerdict Evaluate(
        AcceptanceCohortReceipt cohort,
        Goal goal,
        AcceptanceCohortPartitionReceipt? partition,
        string? currentCandidate,
        string? currentMain,
        bool alreadyRecorded)
    {
        var receiptId = partition?.ReceiptId ?? string.Empty;
        var attributed = cohort.AttributedMembers.SingleOrDefault(member => member.GoalId == goal.Id);
        if (partition is null || partition.GoalId != goal.Id ||
            partition.Outcome != AcceptanceCohortGateOutcome.Failed ||
            attributed is null || attributed.ReproducedFailingTests.Count == 0 ||
            !partition.FailingTestIdentities.Intersect(
                attributed.ReproducedFailingTests, StringComparer.Ordinal).Any())
        {
            return Skipped("not-attributed", receiptId);
        }

        if (string.IsNullOrWhiteSpace(currentCandidate) ||
            !string.Equals(partition.CandidateRevision, currentCandidate, StringComparison.Ordinal))
        {
            return Skipped("candidate-changed", receiptId);
        }
        if (string.IsNullOrWhiteSpace(currentMain) ||
            !string.Equals(partition.ObservedMainRevision, currentMain, StringComparison.Ordinal))
        {
            return Skipped("main-changed", receiptId);
        }

        var reproduced = attributed.ReproducedFailingTests
            .Where(test => !string.IsNullOrWhiteSpace(test))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (reproduced.Length == 0)
        {
            return Skipped("not-attributed", receiptId);
        }
        var checks = (partition.FailedChecks.Count > 0 ? partition.FailedChecks : reproduced)
            .Where(check => !string.IsNullOrWhiteSpace(check))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var detail = $"{EvidencePrefix}{cohort.Identity.Value} cohort-partition={receiptId} " +
            $"reproduced={string.Join(',', reproduced)}";
        return new ConductorAcceptanceCohortAttributionVerdict(
            alreadyRecorded ? "skipped" : "recorded", "identity-match", receiptId,
            checks, detail, alreadyRecorded);
    }

    internal static bool IsRecordedFailure(Goal goal, string cohortId, string receiptId) =>
        goal.RetainedAcceptanceFailure?.CheckAttributions?.Any(attribution =>
            attribution.Evidence.Contains($"{EvidencePrefix}{cohortId} cohort-partition={receiptId} ",
                StringComparison.Ordinal)) == true;

    private static ConductorAcceptanceCohortAttributionVerdict Skipped(string reason, string receiptId) =>
        new("skipped", reason, receiptId, [], string.Empty);
}
