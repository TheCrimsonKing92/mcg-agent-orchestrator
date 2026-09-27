namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static void RecordSuccessfulLanding(HashSet<string> landedGoalIds, string goalId)
    {
        lock (landedGoalIds)
            landedGoalIds.Add(goalId);
    }

    private static int GetSuccessfulLandingCount(HashSet<string> landedGoalIds)
    {
        lock (landedGoalIds)
            return landedGoalIds.Count;
    }
}
