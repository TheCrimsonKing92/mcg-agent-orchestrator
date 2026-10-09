using Mcg.AgentOrchestrator.App.Cli;
using Xunit;

public sealed class CliGoalPrefixArgumentsTests
{
    [Fact]
    public void BareNext_WithAutonomyPolicy_ReturnsNoPrefix() =>
        Assert.Null(CliGoalPrefixArguments.GetOptionalArgument(
            ["next", "--autonomy-policy", "observe"], "--full"));

    [Fact]
    public void ExplicitPrefix_WithAutonomyPolicy_ReturnsPrefix() =>
        Assert.Equal("abc10000", CliGoalPrefixArguments.GetOptionalArgument(
            ["next", "abc10000", "--autonomy-policy", "observe"], "--full"));

    [Theory]
    [InlineData("--goal")]
    [InlineData("--backlog-coverage")]
    [InlineData("--backlog-item")]
    [InlineData("--pipeline")]
    [InlineData("--role")]
    [InlineData("--task")]
    [InlineData("--autonomy")]
    [InlineData("--policy")]
    [InlineData("--autonomy-policy")]
    public void ValueFlag_BeforePrefix_ConsumesValue(string flag)
    {
        Assert.Equal("abc10000", CliGoalPrefixArguments.GetOptionalArgument(
            ["status", flag, "v", "abc10000"], "--tasks-only"));
        Assert.Equal("abc10000", CliGoalPrefixArguments.GetOptionalArgument(
            ["status", flag.ToUpperInvariant(), "v", "abc10000"], "--tasks-only"));
    }

    [Fact]
    public void PlainFlag_BeforePrefix_DoesNotConsumePrefix() =>
        Assert.Equal("abc10000", CliGoalPrefixArguments.GetOptionalArgument(
            ["next", "--full", "abc10000"], "--full"));
}
