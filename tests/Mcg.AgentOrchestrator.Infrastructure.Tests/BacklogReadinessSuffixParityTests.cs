using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BacklogReadinessSuffixParityTests
{
    [Theory]
    [InlineData("satisfied-backlog")]
    [InlineData("satisfied-goal")]
    [InlineData("blocked-backlog")]
    [InlineData("done-backlog-without-goal")]
    [InlineData("superseded-backlog-without-goal")]
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
        var backlogKind = scenario is "satisfied-backlog" or "blocked-backlog"
            or "done-backlog-without-goal" or "superseded-backlog-without-goal";
        await fixture.Store.AddDependencyAsync(dependent.Id, new(
            backlogKind ? prerequisite.Id : goal.Id.Value,
            backlogKind ? BacklogDependencyTargetKind.Backlog : BacklogDependencyTargetKind.Goal),
            goalExists: id => kernel.Goals.Any(candidate => candidate.Id.Value == id));
        if (scenario == "done-backlog-without-goal")
            await fixture.Store.CloseAsync(prerequisite.Id);
        if (scenario == "superseded-backlog-without-goal")
            await fixture.Store.SupersedeAsync(prerequisite.Id, (await fixture.Store.AddAsync("Successor")).Id);
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
            "satisfied-backlog" or "satisfied-goal" or "done-backlog-without-goal" => " [Ready: dependencies landed]",
            "blocked-backlog" or "superseded-backlog-without-goal" => $" [Blocked: waiting on open prerequisite {prerequisite.Id[..8]}]",
            "blocked-goal" => $" [Blocked: waiting on active prerequisite goal {goal.Id.Value[..8]}]",
            _ => $" [Blocked: dependency-terminal-without-landing {goal.Id.Value[..8]} state=Failed]"
        };
        var line = Assert.Single(output.Split(Environment.NewLine).Where(line =>
            line.Contains(dependent.Id + " | status=", StringComparison.Ordinal)));
        Assert.Contains(expected + " " + dependent.Id, line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BacklogItemStatus.Done, true)]
    [InlineData(BacklogItemStatus.Open, false)]
    [InlineData(BacklogItemStatus.Superseded, false)]
    public async Task Ownerless_prerequisite_is_ready_only_when_done(BacklogItemStatus status, bool eligible)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var prerequisite = await fixture.Store.AddAsync("Prerequisite");
        await fixture.Store.AddDependencyAsync(fixture.Item.Id, new(prerequisite.Id, BacklogDependencyTargetKind.Backlog));
        var dependent = (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!;
        var readiness = BacklogDependencyReadiness.Evaluate(dependent, _ => null,
            _ => prerequisite with { Status = status }, _ => new(null),
            _ => throw new InvalidOperationException("An ownerless prerequisite has no landing state."));

        Assert.Equal(eligible, readiness.Eligible);
        Assert.Equal(eligible ? " [Ready: dependencies landed]"
            : $" [Blocked: waiting on open prerequisite {prerequisite.Id[..8]}]", readiness.Suffix);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Done_prerequisite_with_active_or_ambiguous_owner_stays_blocked(bool ambiguous)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var prerequisite = await fixture.Store.AddAsync("Prerequisite");
        await fixture.Store.AddDependencyAsync(fixture.Item.Id, new(prerequisite.Id, BacklogDependencyTargetKind.Backlog));
        var dependent = (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!;
        var goal = new AgentOrchestratorKernel().CreateGoal("Active owner");
        var readiness = BacklogDependencyReadiness.Evaluate(dependent, _ => null,
            _ => prerequisite with { Status = BacklogItemStatus.Done },
            _ => ambiguous ? new(null, true) : new(goal), _ => "Running");

        Assert.False(readiness.Eligible);
        Assert.Equal(ambiguous
            ? $" [Blocked: reason=legacy-owner-ambiguous prerequisite {prerequisite.Id[..8]}]"
            : $" [Blocked: waiting on active prerequisite goal {goal.Id.Value[..8]}]", readiness.Suffix);
    }

    [Fact]
    public async Task Done_ownerless_prerequisite_keeps_list_show_and_board_fill_in_agreement()
    {
        // The fixture owns isolated SQLite files; construction does not start drafting or Git work.
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var prerequisite = await fixture.Store.AddAsync("Prerequisite");
        await fixture.Store.AddDependencyAsync(fixture.Item.Id, new(prerequisite.Id, BacklogDependencyTargetKind.Backlog));
        await fixture.Store.CloseAsync(prerequisite.Id);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var listing = AsyncLocalConsoleRouter.Capture(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-list", "--status", "open"], kernel, fixture.Workspace,
            ref agents, providers, ref profiles, ref currentGoal));
        var show = AsyncLocalConsoleRouter.Capture(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-show", fixture.Item.Id], kernel, fixture.Workspace,
            ref agents, providers, ref profiles, ref currentGoal));
        var host = ConductorBoardFillHost.CreateDefault(fixture.Workspace,
            () => ConductorAutonomyPolicy.Permissive, () => CliAuthorDraftCommandTests.Fixture.MainSha);
        var field = typeof(ConductorBoardFillHost).GetField("_readiness", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var factory = Assert.IsType<Func<AgentOrchestratorKernel, IReadOnlyList<BacklogItem>, Func<BacklogItem, BacklogReadiness>>>(
            field.GetValue(host));
        var items = BoardFillBacklogSnapshot.Read(fixture.Workspace.BacklogStorePath);
        var readiness = factory(kernel, items)(Assert.Single(items.Where(item => item.Id == fixture.Item.Id)));

        Assert.True(readiness.Eligible);
        Assert.Equal(" [Ready: dependencies landed]", readiness.Suffix);
        var line = Assert.Single(listing.Split(Environment.NewLine).Where(line =>
            line.Contains(fixture.Item.Id + " | status=", StringComparison.Ordinal)));
        Assert.Contains(readiness.Suffix + " " + fixture.Item.Id, line, StringComparison.Ordinal);
        Assert.Contains($"- {prerequisite.Id} state=Done", show, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Done_ownerless_prerequisite_does_not_skip_a_remaining_open_dependency()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var done = await fixture.Store.AddAsync("Done prerequisite");
        var open = await fixture.Store.AddAsync("Open prerequisite");
        await fixture.Store.AddDependencyAsync(fixture.Item.Id, new(done.Id, BacklogDependencyTargetKind.Backlog));
        await fixture.Store.AddDependencyAsync(fixture.Item.Id, new(open.Id, BacklogDependencyTargetKind.Backlog));
        await fixture.Store.CloseAsync(done.Id);
        var items = BoardFillBacklogSnapshot.Read(fixture.Workspace.BacklogStorePath).ToDictionary(item => item.Id);
        var readiness = BacklogDependencyReadiness.Evaluate(items[fixture.Item.Id], _ => null,
            id => items[id], _ => new(null), _ => throw new InvalidOperationException("No owner goal."));

        Assert.False(readiness.Eligible);
        Assert.Equal($" [Blocked: waiting on open prerequisite {open.Id[..8]}]", readiness.Suffix);
    }
}
