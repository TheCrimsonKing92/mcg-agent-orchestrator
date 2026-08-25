namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceLifecycleEventFormatter
{
    internal static string Format(string goalPrefix, int slotIndex, string result, string attemptId, int tick) =>
        $"ACCEPTANCE goal={goalPrefix} slot=slot-{slotIndex} result={result} attempt={attemptId} tick={tick}";
}
