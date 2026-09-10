namespace Mcg.AgentOrchestrator.App.Orchestration;

// Absence in the working set cannot reject an in-scope intent until a reload established its state.
internal abstract record UnloadedGoalIntentDisposition
{
    public sealed record AwaitingReload : UnloadedGoalIntentDisposition;
    public sealed record Rejected(string Reason, string? ReasonCode = null) : UnloadedGoalIntentDisposition;

    public static UnloadedGoalIntentDisposition Decide(
        string goalId, string? onlyGoalId, Func<string, ConductorGoalReloadObservation> observe)
    {
        if (onlyGoalId is not null && goalId != onlyGoalId)
            return new Rejected($"goal is outside conductor scope {onlyGoalId[..Math.Min(8, onlyGoalId.Length)]}");
        return observe(goalId) switch
        {
            ConductorGoalReloadObservation.NotObserved => new AwaitingReload(),
            ConductorGoalReloadObservation.Terminal terminal => new Rejected(
                $"goal was evicted from the conductor working set because its stored status is {terminal.Status}; the intent was not applicable",
                OperatorIntentCoordinator.TerminalGoalEvictedReasonCode),
            _ => new Rejected("goal was not found in conductor state")
        };
    }
}