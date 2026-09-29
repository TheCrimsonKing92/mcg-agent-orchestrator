namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum GateShardLaneClass { Gate, Evidence }

internal enum GateShardPollOutcome { Acquired, Waiting, YieldingToGate, AcquiredAfterForcedYield }

internal static class GateShardLanePriorityProgress
{
    internal static string Format(GateShardLaneClass laneClass, GateShardPollOutcome outcome) =>
        $"lane_class={(laneClass == GateShardLaneClass.Evidence ? "evidence" : "gate")}" +
        (outcome switch
        {
            GateShardPollOutcome.YieldingToGate => " permit_priority=yielding-to-gate",
            GateShardPollOutcome.AcquiredAfterForcedYield => " permit_priority=forced-after-yield",
            _ => " permit_priority=waiting"
        });
}
