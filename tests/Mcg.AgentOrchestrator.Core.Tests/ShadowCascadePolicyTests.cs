using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure policy decisions.
public sealed class ShadowCascadePolicyTests
{
    [Fact(DisplayName = "Only developer and tester move or documentation tasks lower effort")]
    public void EveryRoleClassPairUsesTheVersionedTable()
    {
        Assert.Equal("shadow-cascade-v1", ShadowCascadePolicy.PolicyVersion);
        foreach (var role in Enum.GetValues<AgentRole>())
        foreach (var taskClass in Enum.GetValues<DispatchTaskClass>())
        {
            var lowers = role is AgentRole.Developer or AgentRole.Tester &&
                         taskClass is DispatchTaskClass.PureMove or DispatchTaskClass.DocsOnly;
            var expected = new DispatchShadowDecision(taskClass, "provider", "actual-model",
                lowers ? "low" : "high", ShadowCascadePolicy.PolicyVersion, lowers);
            var actual = ShadowCascadePolicy.Decide(role, taskClass, "provider", "actual-model", "high");
            Assert.Equal(expected, actual);
            Assert.Equal(actual, ShadowCascadePolicy.Decide(role, taskClass, "provider", "actual-model", "high"));
        }
    }

    [Theory(DisplayName = "Shadow difference reflects the resolved effort")]
    [InlineData("low", false)]
    [InlineData("LOW", false)]
    [InlineData(null, true)]
    [InlineData("medium", true)]
    public void LowAndUnknownActualEffortHaveExplicitDifference(string? effort, bool differs)
    {
        var decision = ShadowCascadePolicy.Decide(AgentRole.Developer, DispatchTaskClass.DocsOnly,
            "provider", "model", effort);
        Assert.Equal("low", decision.ShadowReasoningEffort);
        Assert.Equal(differs, decision.Differs);
    }
}
