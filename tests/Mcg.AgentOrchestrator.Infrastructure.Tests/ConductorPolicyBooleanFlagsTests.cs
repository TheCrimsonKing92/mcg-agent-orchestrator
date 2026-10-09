using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Xunit;

public sealed class ConductorPolicyBooleanFlagsTests
{
    [Fact]
    public void Allowlist_EachRecordBooleanIsParsedAndRejectsNonBooleanValues()
    {
        Assert.NotEmpty(ConductorPolicyBooleanFlags.Names);
        foreach (var name in ConductorPolicyBooleanFlags.Names)
        {
            var json = JsonNode.Parse(ConductorAutonomyPolicy.Conservative.ToJson())!;
            foreach (var value in new[] { false, true })
            {
                json[name] = value;
                Assert.Equal(value, ConductorPolicyBooleanFlags.Read(ConductorAutonomyPolicy.ParseJson(json.ToJsonString()), name));
            }
            json[name] = "true";
            Assert.Contains("must be a boolean", Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(json.ToJsonString())).Message);
            Assert.False(ConductorPolicyBooleanFlags.IsAllowed(char.ToUpperInvariant(name[0]) + name[1..]));
        }
        Assert.False(ConductorPolicyBooleanFlags.IsAllowed("maxCriterionRetries"));
        Assert.False(ConductorPolicyBooleanFlags.IsAllowed("allowsAutonomousHighRiskOwnership"));
        Assert.False(ConductorPolicyBooleanFlags.IsAllowed(null));
    }
}
