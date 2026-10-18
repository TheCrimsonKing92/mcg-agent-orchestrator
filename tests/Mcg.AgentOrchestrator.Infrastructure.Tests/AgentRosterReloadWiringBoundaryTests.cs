// Parallel-safe: reads source from the explicitly verified repository root only.
public sealed class AgentRosterReloadWiringBoundaryTests
{
    [Xunit.Fact]
    public void ConductLoop_UsesLiveRosterAndRefreshesBeforeReaping()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.App", "Cli", "CliCommandHandlers.Goals.cs"));
        Assert.Equal(1, source.Split("AgentRosterReloader.ForConductLoop(", StringSplitOptions.None).Length - 1);
        var assignment = source.IndexOf("context.Agents = agentRoster.View;", StringComparison.Ordinal);
        var driver = source.IndexOf("var loopDriver = new ConductorDriver(", StringComparison.Ordinal);
        var sweep = source.IndexOf("TerminalGoalSweepResult reconcileSweep(", StringComparison.Ordinal);
        var refresh = source.IndexOf("agentRoster.Refresh();", StringComparison.Ordinal);
        var reaper = source.IndexOf("loopReaper.BeginRefreshCycle();", StringComparison.Ordinal);
        Assert.True(assignment >= 0 && assignment < driver);
        Assert.True(sweep >= 0 && sweep < refresh && refresh < reaper);
    }
}
