namespace Mcg.AgentOrchestrator.App.Orchestration;

// Only exhausted acquisition rounds produce this signal; verifier faults retain their outcomes.
internal sealed class CohortStableSlotsDeferredException(string cohortId)
    : Exception($"Stable build slots remained busy for cohort {cohortId}.")
{
}
