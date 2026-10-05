using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorCohortBlameEvidence(string EarlierCohortId,
    AcceptanceCohortAttributedMember Member);

internal sealed record ConductorCohortIdenticalFailureDecision(
    IReadOnlyList<ConductorCohortBlameEvidence> Withheld,
    IReadOnlyList<ConductorCohortBlameEvidence> Retractions, IReadOnlyList<string> Tests);

internal static class ConductorAcceptanceCohortIdenticalFailure
{
    internal const string ReasonDetail = "identical-failure-without-member";

    internal static ConductorCohortIdenticalFailureDecision? Decide(string cohortId,
        IReadOnlyList<GoalId> members, string observedMainRevision,
        IReadOnlyList<string> cohortFailingTests, IReadOnlyList<AcceptanceCohortAttributedMember> wouldBlame,
        IReadOnlyList<CohortFailureEvidence> earlier)
    {
        var tests = new HashSet<string>(cohortFailingTests, StringComparer.Ordinal);
        if (tests.Count == 0) return null;
        var withheld = new List<ConductorCohortBlameEvidence>();
        var retractions = new List<ConductorCohortBlameEvidence>();
        foreach (var receipt in earlier)
        {
            if (StringComparer.Ordinal.Equals(receipt.CohortId, cohortId) ||
                !StringComparer.Ordinal.Equals(receipt.ObservedMainRevision, observedMainRevision)) continue;
            foreach (var member in wouldBlame)
                if (!receipt.MemberGoalIds.Contains(member.GoalId) && receipt.CohortFailingTests is { Count: > 0 } &&
                    tests.SetEquals(receipt.CohortFailingTests))
                    withheld.Add(new(receipt.CohortId, member));
            foreach (var member in receipt.AttributedMembers)
                if (!members.Contains(member.GoalId) && tests.SetEquals(member.ReproducedFailingTests) &&
                    // Legacy rows have only the reproducer set. When the complete gate set is
                    // recorded, a subset reproduced by this member must not establish a match.
                    (receipt.CohortFailingTests is null || tests.SetEquals(receipt.CohortFailingTests)))
                    retractions.Add(new(receipt.CohortId, member));
        }
        return withheld.Count == 0 && retractions.Count == 0 ? null :
            new(withheld, retractions.Distinct().ToArray(), tests.Order(StringComparer.Ordinal).ToArray());
    }

    internal static string FormatRetractionEvent(CohortAttributionRetraction retraction) =>
        $"COHORT_ATTRIBUTION_RETRACTED goal={retraction.GoalId.Value[..8]} earlier={retraction.EarlierCohortId} later={retraction.LaterCohortId} tests={retraction.FailingTestCount}";

    internal static string RetractionEventId(CohortAttributionRetraction retraction) =>
        $"cohort-attribution-retracted:{retraction.EarlierCohortId}:{retraction.GoalId.Value}:{retraction.CandidateRevision}";
}
