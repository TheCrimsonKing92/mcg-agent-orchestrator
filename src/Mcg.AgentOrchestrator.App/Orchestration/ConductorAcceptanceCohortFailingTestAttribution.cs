using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAcceptanceCohortIdentityAttribution(
    AcceptanceCohortAttributionOutcome Outcome,
    IReadOnlyList<AcceptanceCohortAttributedMember> AttributedMembers,
    IReadOnlyList<AcceptanceCohortUnrelatedFailure> UnrelatedFailures);

internal static class ConductorAcceptanceCohortAttributedMembers
{
    internal static string Key(GoalId goalId, string candidateRevision) =>
        $"{goalId.Value}:{candidateRevision}";
}

internal static class ConductorAcceptanceCohortFailingTestAttribution
{
    internal static ConductorAcceptanceCohortIdentityAttribution Classify(
        IReadOnlyCollection<string> cohortFailingTests,
        AcceptanceCohortPartitionReceipt first,
        AcceptanceCohortPartitionReceipt second)
    {
        var original = ConductorAcceptanceCohortAttribution.Classify(first.Outcome, second.Outcome);
        if (original == AcceptanceCohortAttributionOutcome.Indeterminate)
        {
            return new(original, [], []);
        }

        var cohort = cohortFailingTests.ToHashSet(StringComparer.Ordinal);
        var partitions = new[] { first, second };
        var reproduced = partitions.Select(partition => partition.FailingTestIdentities
            .Where(cohort.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()).ToArray();
        var unrelated = partitions.Select(partition => partition.FailingTestIdentities
            .Where(test => !cohort.Contains(test)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()).ToArray();
        var effective = partitions.Select((partition, index) =>
            partition.Outcome == AcceptanceCohortGateOutcome.Failed &&
            reproduced[index].Length == 0
                ? AcceptanceCohortGateOutcome.Passed : partition.Outcome).ToArray();
        var outcome = ConductorAcceptanceCohortAttribution.Classify(effective[0], effective[1]);
        var attributed = partitions.Select((partition, index) => (partition, index))
            .Where(item => effective[item.index] == AcceptanceCohortGateOutcome.Failed && reproduced[item.index].Length > 0)
            .Select(item => new AcceptanceCohortAttributedMember(
                item.partition.GoalId, item.partition.MemberOrdinal, item.partition.CandidateRevision, reproduced[item.index]))
            .ToArray();
        var unrelatedFailures = partitions.Select((partition, index) => (partition, index))
            .Where(item => unrelated[item.index].Length > 0)
            .Select(item => new AcceptanceCohortUnrelatedFailure(
                item.partition.GoalId, item.partition.MemberOrdinal, unrelated[item.index]))
            .ToArray();
        return new(outcome, attributed, unrelatedFailures);
    }
}
