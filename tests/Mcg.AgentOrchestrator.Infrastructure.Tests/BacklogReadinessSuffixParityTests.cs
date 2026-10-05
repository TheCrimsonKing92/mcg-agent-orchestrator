using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BacklogReadinessSuffixParityTests
{
    [Theory]
    [InlineData("satisfied-backlog")]
    [InlineData("satisfied-goal")]
    [InlineData("blocked-backlog")]
    [InlineData("blocked-goal")]
    [InlineData("terminal-without-landing")]
    public async Task Listing_keeps_the_exact_readiness_suffix(string scenario)
    {
        // Each fixture has independent backlog and journal files, with no Git or model process.
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var dependent = fixture.Item;
        var prerequisite = await fixture.Store.AddAsync("Prerequisite");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Dependency goal");
        var backlogKind = scenario is "satisfied-backlog" or "blocked-backlog";
        await fixture.Store.AddDependencyAsync(dependent.Id, new(
            backlogKind ? prerequisite.Id : goal.Id.Value,
            backlogKind ? BacklogDependencyTargetKind.Backlog : BacklogDependencyTargetKind.Goal));
        if (scenario == "satisfied-backlog")
            kernel.SetGoalSourceBacklogItemLink(goal.Id, prerequisite.Id, SourceBacklogCoverage.Full);
        if (scenario.StartsWith("satisfied", StringComparison.Ordinal))
        {
            kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id) with { Status = GoalStatus.Completed });
            goal = kernel.Goals.Single();
            GoalOperationJournal.Completed(fixture.Workspace.ExecutionDirectory, goal, "conductor:land");
            GoalOperationJournal.Completed(fixture.Workspace.ExecutionDirectory, goal, "conductor:record");
        }
        if (scenario == "terminal-without-landing")
            kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id) with { Status = GoalStatus.Failed });
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var output = AsyncLocalConsoleRouter.Capture(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-list", "--status", "open"], kernel, fixture.Workspace,
            ref agents, providers, ref profiles, ref currentGoal));
        var expected = scenario switch
        {
            "satisfied-backlog" or "satisfied-goal" => " [Ready: dependencies landed]",
            "blocked-backlog" => $" [Blocked: waiting on open prerequisite {prerequisite.Id[..8]}]",
            "blocked-goal" => $" [Blocked: waiting on active prerequisite goal {goal.Id.Value[..8]}]",
            _ => $" [Blocked: dependency-terminal-without-landing {goal.Id.Value[..8]} state=Failed]"
        };
        var line = Assert.Single(output.Split(Environment.NewLine).Where(line =>
            line.Contains(dependent.Id + " | status=", StringComparison.Ordinal)));
        Assert.Contains(expected + " " + dependent.Id, line, StringComparison.Ordinal);
    }
}
