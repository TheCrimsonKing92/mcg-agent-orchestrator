using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: JSON strings only, no global configuration changes.
public sealed class ProgramHealthThresholdsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("malformed")]
    [InlineData("[]")]
    [InlineData("{\"programHealth\":false}")]
    [InlineData("{}")]
    public void MissingOrInvalidTextUsesDefaults(string? json) => Assert.Equal(new(), ProgramHealthThresholds.Read(json));

    [Fact]
    public void ValidObjectAndCommentsAreRead()
    {
        Assert.Equal(new(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(10)),
            ProgramHealthThresholds.Read("{ /* comment */ \"programHealth\": { \"noLandingMinutes\":60, \"heldReceiptMinutes\":10 }}"));
        Assert.Equal(new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10080)),
            ProgramHealthThresholds.Read("{\"programHealth\":{\"noLandingMinutes\":1,\"heldReceiptMinutes\":10080}}"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10081")]
    [InlineData("\"20\"")]
    [InlineData("1.5")]
    [InlineData("null")]
    [InlineData("true")]
    public void InvalidKeyDoesNotDiscardTheOtherValidKey(string value)
    {
        Assert.Equal(new(TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(7)),
            ProgramHealthThresholds.Read("{\"programHealth\":{\"noLandingMinutes\":" + value + ",\"heldReceiptMinutes\":7}}"));
        Assert.Equal(new(TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(30)),
            ProgramHealthThresholds.Read("{\"programHealth\":{\"noLandingMinutes\":8,\"heldReceiptMinutes\":" + value + "}}"));
    }

    [Fact]
    public void PrimaryPolicyParserStillAcceptsTheSharedFile()
    {
        var expected = ConductorAutonomyPolicy.Permissive;
        var json = expected.ToJson().Insert(1, "\"programHealth\":{\"noLandingMinutes\":60,\"heldReceiptMinutes\":10},");
        var policy = ConductorAutonomyPolicy.ParseJson(json);
        Assert.Equal(expected.Name, policy.Name);
        Assert.Equal(expected.MaxConcurrentPaidWorkers, policy.MaxConcurrentPaidWorkers);
        Assert.Equal(expected.AcceptanceWidth, policy.AcceptanceWidth);
    }
}
