namespace Mcg.AgentOrchestrator.App.Orchestration;

// Evaluated and reserved on the loop thread, where dispatch, landing and intent application are admitted.
internal static class ProjectConductorIdleCheck
{
    internal static IReadOnlyList<string> ReadBlockers(Func<int> countRunningWorkers,
        ConductorDriver driver, Func<int> countLiveGroupedGates, OperatorIntentCoordinator? operatorIntents,
        Func<IReadOnlyList<string>>? additionalBlockers)
    {
        try
        {
            // Landings and intent application are synchronous on the loop thread. Durable claimed
            // intents and acceptance holders cover work surviving a previous conductor generation.
            var gateCount = countLiveGroupedGates() + driver.ParallelAcceptanceAttemptCoordinator
                .GetAllCapacityReservingAttempts().Count + driver._focusedEvidenceAttemptCoordinator
                .GetAllCapacityReservingAttempts().Count;
            return Blockers(countRunningWorkers(), gateCount, landingInProgress: false,
                pendingIntents: operatorIntents?.HasRelaunchBlockingIntents() == true,
                applyingIntents: false).Concat(additionalBlockers?.Invoke() ?? []).ToArray();
        }
        catch (Exception ex)
        {
            // An unreadable holder/intent census cannot authorize shutdown.
            return [$"idle-state-unavailable:{ex.GetType().Name}"];
        }
    }

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
