using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: immutable policies and private JSON values.
public sealed class ConductorAutonomyPolicyTestsFollowerGates
{
    [Fact]
    public void PresetsAndAbsentPropertyDefaultOff()
    {
        Assert.False(ConductorAutonomyPolicy.Conservative.FollowerGatesEnabled);
        Assert.False(ConductorAutonomyPolicy.Permissive.FollowerGatesEnabled);
        Assert.False(ConductorAutonomyPolicy.Manual.FollowerGatesEnabled);
        var json = JsonNode.Parse(ConductorAutonomyPolicy.Permissive.ToJson())!.AsObject();
        Assert.True(json.Remove("followerGatesEnabled"));
        Assert.False(ConductorAutonomyPolicy.ParseJson(json.ToJsonString()).FollowerGatesEnabled);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    public void ReadsOptionalBooleanAndRoundTrips(string value, bool expected)
    {
        var json = JsonNode.Parse(ConductorAutonomyPolicy.Permissive.ToJson())!.AsObject();
        json["followerGatesEnabled"] = JsonNode.Parse(value);
        var policy = ConductorAutonomyPolicy.ParseJson(json.ToJsonString());
        Assert.Equal(expected, policy.FollowerGatesEnabled);
        Assert.Equal(expected, ConductorAutonomyPolicy.ParseJson(policy.ToJson()).FollowerGatesEnabled);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("123")]
    [InlineData("[]")]
    public void RejectsInvalidKindWithPropertyName(string value)
    {
        var json = JsonNode.Parse(ConductorAutonomyPolicy.Permissive.ToJson())!.AsObject();
        json["followerGatesEnabled"] = JsonNode.Parse(value);
        Assert.Contains("followerGatesEnabled", Assert.Throws<FormatException>(() =>
            ConductorAutonomyPolicy.ParseJson(json.ToJsonString())).Message);
    }
}
