using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = ExecutorScreeningRunnerTests.ExecutorScreeningFixture;

public sealed class RemoteLaneExecutorConfigurationTestsScreening
{
    [Fact]
    public void OptionalScreeningBlockNeverDisablesTheRemoteConfiguration()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "config.json");
        RemoteLaneExecutorConfiguration Load(string extra)
        {
            File.WriteAllText(path, "{\"executors\":[{\"id\":\"laptop\"}],\"lanes\":[\"*\"]" + extra + "}");
            return RemoteLaneExecutorConfiguration.Load(path);
        }
        var baseline = Load("");
        Assert.True(baseline.Enabled);
        Assert.Equal("off", baseline.Screening.Mode);
        Assert.Empty(baseline.Screening.KnownDivergentTests);
        Assert.Null(baseline.Screening.FaultReason);
        var valid = Load(",\"screening\":{\"mode\":\"shadow\",\"knownDivergentTests\":[\"A.B\",\"C.D\"]}");
        Assert.True(valid.Enabled);
        Assert.Equal("shadow", valid.Screening.Mode);
        Assert.Equal(new[] { "A.B", "C.D" }, valid.Screening.KnownDivergentTests);
        var invalid = Load(",\"screening\":{\"mode\":\"on\"}");
        Assert.True(invalid.Enabled);
        Assert.Equal(baseline.Executors, invalid.Executors);
        Assert.Equal(baseline.Lanes, invalid.Lanes);
        Assert.Equal("off", invalid.Screening.Mode);
        Assert.Equal("invalid", invalid.Screening.FaultReason);
        Assert.Empty(invalid.Screening.KnownDivergentTests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"mode\":true}")]
    [InlineData("{\"mode\":\"Shadow\"}")]
    [InlineData("{\"knownDivergentTests\":null}")]
    [InlineData("{\"knownDivergentTests\":[\"\"]}")]
    [InlineData("{\"knownDivergentTests\":[7]}")]
    public void InvalidBlocksFailClosedForScreeningOnly(string block)
    {
        using var json = JsonDocument.Parse("{\"screening\":" + block + "}");
        var settings = ExecutorScreeningSettings.Parse(json.RootElement);
        Assert.Equal("off", settings.Mode);
        Assert.Equal("invalid", settings.FaultReason);
        Assert.Empty(settings.KnownDivergentTests);
    }

    [Fact]
    public void KnownDivergenceUsesOrdinalWholeIdentityOrDotSuffix()
    {
        var settings = new ExecutorScreeningSettings { KnownDivergentTests = ["ExampleTests.Broken"] };
        Assert.True(settings.IsKnownDivergent("ExampleTests.Broken"));
        Assert.True(settings.IsKnownDivergent("Suite.ExampleTests.Broken"));
        Assert.False(settings.IsKnownDivergent("OtherExampleTests.Broken"));
        Assert.False(settings.IsKnownDivergent("Suite.ExampleTests.broken"));
    }
}
