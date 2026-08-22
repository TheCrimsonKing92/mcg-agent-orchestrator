using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class PlannerSamplingPolicyTests
{
    [Xunit.Fact]
    public void OnlyPlannerReceivesConfiguredSampleCount()
    {
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var effective = PlannerSamplingPolicy.EffectiveSampleCount(role, 3);
            Xunit.Assert.Equal(role == AgentRole.Planner ? 3 : 1, effective);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, 1)]
    [Xunit.InlineData(8, 5)]
    public void PlannerSampleCountIsBounded(int configured, int expected) =>
        Xunit.Assert.Equal(expected, PlannerSamplingPolicy.EffectiveSampleCount(AgentRole.Planner, configured));
}
