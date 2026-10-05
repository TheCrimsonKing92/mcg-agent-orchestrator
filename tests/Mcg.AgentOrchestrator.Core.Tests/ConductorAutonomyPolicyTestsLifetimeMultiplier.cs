using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class ConductorAutonomyPolicyTestsLifetimeMultiplier
{
    [Fact]
    public void Lifetime_multiplier_round_trips_and_legacy_json_defaults_to_three()
    {
        var policy = ConductorAutonomyPolicy.Permissive with { ReviewAutoRetryLifetimeMultiplier = 4 };
        Assert.Equal(4, ConductorAutonomyPolicy.ParseJson(policy.ToJson()).ReviewAutoRetryLifetimeMultiplier);
        var legacy = JsonNode.Parse(policy.ToJson())!.AsObject();
        Assert.True(legacy.Remove("reviewAutoRetryLifetimeMultiplier"));
        Assert.Equal(3, ConductorAutonomyPolicy.ParseJson(legacy.ToJsonString()).ReviewAutoRetryLifetimeMultiplier);
    }

    [Fact]
    public void Lifetime_multiplier_below_two_is_rejected()
    {
        var policy = ConductorAutonomyPolicy.Permissive with { ReviewAutoRetryLifetimeMultiplier = 1 };
        Assert.Contains(policy.Validate(), error => error.Contains("reviewAutoRetryLifetimeMultiplier", StringComparison.Ordinal));
        Assert.Contains("reviewAutoRetryLifetimeMultiplier", Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(policy.ToJson())).Message);
    }
}
