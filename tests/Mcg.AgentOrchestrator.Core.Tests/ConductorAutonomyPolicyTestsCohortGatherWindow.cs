using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class ConductorAutonomyPolicyTestsCohortGatherWindow
{
    [Xunit.Fact]
    public void OptionalWindowDefaultsAndRoundTrips()
    {
        var original = ConductorAutonomyPolicy.Permissive;
        var absent = original.ToJson().Replace(
            "  \"acceptanceCohortGatherWindowSeconds\": 480," + Environment.NewLine, "");
        Assert.Equal(480, ConductorAutonomyPolicy.ParseJson(absent).AcceptanceCohortGatherWindowSeconds);
        Assert.Equal(480, ConductorAutonomyPolicy.ParseJson(original.ToJson()
            .Replace("\"acceptanceCohortGatherWindowSeconds\": 480", "\"acceptanceCohortGatherWindowSeconds\": null"))
            .AcceptanceCohortGatherWindowSeconds);
        Assert.Equal(0, ConductorAutonomyPolicy.ParseJson((original with
        {
            AcceptanceCohortGatherWindowSeconds = 0
        }).ToJson()).AcceptanceCohortGatherWindowSeconds);
        Assert.Equal(0, ConductorAutonomyPolicy.ParseJson(original.ToJson()
            .Replace("\"acceptanceCohortGatherWindowSeconds\": 480", "\"acceptanceCohortGatherWindowSeconds\": -1"))
            .AcceptanceCohortGatherWindowSeconds);
    }

    [Xunit.Fact]
    public void InvalidWindowValueFailsPolicyLoad()
    {
        var json = ConductorAutonomyPolicy.Permissive.ToJson()
            .Replace("\"acceptanceCohortGatherWindowSeconds\": 480", "\"acceptanceCohortGatherWindowSeconds\": \"bad\"");
        Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(json));
    }
}
