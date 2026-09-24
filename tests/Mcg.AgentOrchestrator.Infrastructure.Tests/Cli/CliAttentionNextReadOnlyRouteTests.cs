using Mcg.AgentOrchestrator.App.Cli;

public sealed class CliAttentionNextReadOnlyRouteTests
{
    [Xunit.Theory]
    [Xunit.InlineData("attention")]
    [Xunit.InlineData("attention", "show")]
    [Xunit.InlineData("next", "--full", "abc10000")]
    public void PureReadFormSkipsStartupHydration(params string[] args) =>
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
}
