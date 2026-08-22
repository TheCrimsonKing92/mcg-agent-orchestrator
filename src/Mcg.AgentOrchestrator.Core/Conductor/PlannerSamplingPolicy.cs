namespace Mcg.AgentOrchestrator.Core.Conductor;

public static class PlannerSamplingPolicy
{
    public const int MinimumSampleCount = 1;
    public const int MaximumSampleCount = 5;

    public static int EffectiveSampleCount(AgentRole role, int configuredSampleCount) =>
        role == AgentRole.Planner
            ? Math.Clamp(configuredSampleCount, MinimumSampleCount, MaximumSampleCount)
            : MinimumSampleCount;
}
