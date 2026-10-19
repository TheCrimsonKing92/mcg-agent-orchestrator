using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorCohortAttributionSkip
{
    internal static AcceptanceCohortReceipt Record(CohortAcceptanceStore store, AcceptanceCohortIdentity identity,
        string pairFingerprint, IReadOnlyList<string> cohortFailingTests, int failedCheckCount,
        ConductEventLogWriter writer)
    {
        try
        {
            writer.AppendRequired("cohort-attribution-skipped", null,
                $"ATTRIBUTION_SKIPPED cohort={identity.Value[..Math.Min(18, identity.Value.Length)]} reason=no-failing-test-identities failed-checks={failedCheckCount}",
                eventId: $"cohort-attribution-skipped:{identity.Value}");
        }
        catch (Exception) { /* The conduct event is observational; persistence still owns the outcome. */ }
        return store.SaveAttribution(identity.Value, AcceptanceCohortAttributionOutcome.InteractionOnly,
            [], pairFingerprint, null, [], [], AcceptanceCohortAttributionSources.NoFailingTestIdentities,
            suppressPair: true, cohortFailingTests: cohortFailingTests);
    }
}
