namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ActivationAdmissionHealthPolicy
{
    // At the 19.4 s median tick, 8 ticks is about 2.6 min and 12 about 3.9 min.
    // The 5-minute cap bounds slow ticks (p90 128 s).
    internal const int CensusFailureTicks = 8;
    internal const int OccupiedWithoutLiveGateTicks = 12;
    internal static readonly TimeSpan StreakElapsedCap = TimeSpan.FromMinutes(5);
}
