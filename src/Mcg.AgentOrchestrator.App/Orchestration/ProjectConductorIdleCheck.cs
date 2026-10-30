namespace Mcg.AgentOrchestrator.App.Orchestration;

// Evaluated and reserved on the loop thread, where dispatch, landing and intent application are admitted.
internal static class ProjectConductorIdleCheck
{
    internal static IReadOnlyList<string> Blockers(int runningWorkers, int gateHolders,
        bool landingInProgress, bool pendingIntents, bool applyingIntents)
    {
        var blockers = new List<string>();
        if (runningWorkers > 0) blockers.Add($"running-workers:{runningWorkers}");
        if (gateHolders > 0) blockers.Add($"gate-holders:{gateHolders}");
        if (landingInProgress) blockers.Add("landing-in-progress");
        if (pendingIntents) blockers.Add("pending-operator-intent");
        if (applyingIntents) blockers.Add("applying-operator-intent");
        return blockers;
    }
}
