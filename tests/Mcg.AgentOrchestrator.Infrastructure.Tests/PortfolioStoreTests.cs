using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PortfolioStoreTests
{
    [Xunit.Fact(DisplayName = "PortfolioStore_entities_round_trip_with_optional_parent")]
    public async Task EntitiesRoundTripWithOptionalParent()
    {
        var store = new PortfolioStore(TempDb());
        var root = await store.AddProjectAsync("Control plane");
        var child = await store.AddProjectAsync("Orchestration", root.Id);
        var parentedEpic = await store.AddEpicAsync("Wave 2", child.Id);
        var unparentedEpic = await store.AddEpicAsync("Independent work");

        var projects = await store.ListProjectsAsync();
        var epics = await store.ListEpicsAsync();

        var roundTrippedRoot = Assert.Single(projects, project => project.Id == root.Id);
        Assert.Equal("Control plane", roundTrippedRoot.Title);
        Assert.Null(roundTrippedRoot.ParentProjectId);
        var roundTrippedChild = Assert.Single(projects, project => project.Id == child.Id);
        Assert.Equal("Orchestration", roundTrippedChild.Title);
        Assert.Equal(root.Id, roundTrippedChild.ParentProjectId);
        var roundTrippedParentedEpic = Assert.Single(epics, epic => epic.Id == parentedEpic.Id);
        Assert.Equal("Wave 2", roundTrippedParentedEpic.Title);
        Assert.Equal(child.Id, roundTrippedParentedEpic.ProjectId);
        var roundTrippedUnparentedEpic = Assert.Single(epics, epic => epic.Id == unparentedEpic.Id);
        Assert.Equal("Independent work", roundTrippedUnparentedEpic.Title);
        Assert.Null(roundTrippedUnparentedEpic.ProjectId);
    }

    [Xunit.Fact(DisplayName = "PortfolioStore_membership_round_trips_for_goal_and_backlog_members")]
    public async Task MembershipRoundTripsForGoalAndBacklogMembers()
    {
        var store = new PortfolioStore(TempDb());
        var project = await store.AddProjectAsync("Control plane");
        var epic = await store.AddEpicAsync("Wave 2", project.Id);
        const string goalId = "aaaaaaaa111111111111111111111111";
        const string backlogItemId = "backlog11111111111111111111111111";

        await store.AssignGoalToEpicAsync(goalId, epic.Id);
        await store.AssignBacklogItemToEpicAsync(backlogItemId, epic.Id);

        var goalMembership = await store.GetGoalMembershipAsync(goalId);
        var backlogMembership = await store.GetBacklogMembershipAsync(backlogItemId);

        Assert.Equal(epic.Id, goalMembership!.EpicId);
        Assert.Equal("Wave 2", goalMembership.EpicTitle);
        Assert.Equal("Control plane", goalMembership.ProjectTitle);
        Assert.Equal(epic.Id, backlogMembership!.EpicId);
        Assert.Equal("Wave 2", backlogMembership.EpicTitle);
        Assert.Equal("Control plane", backlogMembership.ProjectTitle);
    }

    [Xunit.Fact(DisplayName = "PortfolioStore_membership_survives_terminal_member_states")]
    public async Task MembershipSurvivesTerminalMemberStates()
    {
        var store = new PortfolioStore(TempDb());
        var epic = await store.AddEpicAsync("Terminal members");
        var kernel = new AgentOrchestratorKernel();
        var terminalStatuses = new[] { GoalStatus.Completed, GoalStatus.Failed, GoalStatus.Cancelled, GoalStatus.Superseded };
        var goals = terminalStatuses
            .Select((status, index) => (Goal: kernel.CreateGoal(new GoalId($"{index + 1:D8}aaaaaaaaaaaaaaaaaaaaaaaa"), $"{status} goal"), Status: status))
            .ToArray();
        foreach (var (goal, _) in goals)
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        foreach (var (goal, status) in goals)
            kernel = WithStatus(kernel, goal.Id, status);
        foreach (var (goal, status) in goals)
            Assert.Contains(kernel.Goals, current => current.Id == goal.Id && current.Status == status);

        var backlogStore = new BacklogStore(TempDb());
        var done = await backlogStore.AddAsync("Done backlog member");
        var superseded = await backlogStore.AddAsync("Superseded backlog member");
        var replacement = await backlogStore.AddAsync("Replacement backlog member");
        Assert.True(await backlogStore.TryCloseByIdAsync(done.Id));
        var supersededResult = await backlogStore.SupersedeAsync(superseded.Id, replacement.Id);
        Assert.Equal(BacklogItemStatus.Done, (await backlogStore.ListAsync(includeAll: true)).Single(item => item.Id == done.Id).Status);
        Assert.Equal(BacklogItemStatus.Superseded, supersededResult.Status);

        foreach (var (goal, _) in goals)
            await store.AssignGoalToEpicAsync(goal.Id.Value, epic.Id);
        await store.AssignBacklogItemToEpicAsync(done.Id, epic.Id);
        await store.AssignBacklogItemToEpicAsync(superseded.Id, epic.Id);

        var members = await store.ListEpicMembersAsync(epic.Id);
        var rollup = Assert.Single(await store.BuildEpicRollupsAsync(Array.Empty<Goal>()));

        Assert.Equal(4, members.Count(member => member.Kind == PortfolioMemberKind.Goal));
        Assert.Equal(2, members.Count(member => member.Kind == PortfolioMemberKind.BacklogItem));
        foreach (var (goal, _) in goals)
            Assert.Equal(epic.Id, (await store.GetGoalMembershipAsync(goal.Id.Value))!.EpicId);
        Assert.Equal(epic.Id, (await store.GetBacklogMembershipAsync(done.Id))!.EpicId);
        Assert.Equal(epic.Id, (await store.GetBacklogMembershipAsync(superseded.Id))!.EpicId);
        Assert.Equal(4, rollup.GoalCount);
        Assert.Equal(2, rollup.BacklogItemCount);
    }

    [Xunit.Fact(DisplayName = "PortfolioStore_list_epic_members_rejects_unknown_epic")]
    public async Task ListEpicMembersRejectsUnknownEpic()
    {
        var store = new PortfolioStore(TempDb());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListEpicMembersAsync("missing-epic"));

        Assert.Equal("Epic 'missing-epic' was not found.", error.Message);
    }

    [Xunit.Fact(DisplayName = "PortfolioStore_crud_membership_and_rollups")]
    public async Task CrudMembershipAndRollups()
    {
        var store = new PortfolioStore(TempDb());
        var project = await store.AddProjectAsync("Control plane");
        var epic = await store.AddEpicAsync("At-a-glance board");
        await store.AssignEpicToProjectAsync(epic.Id, project.Id);

        var kernel = new AgentOrchestratorKernel();
        var active = kernel.CreateGoal(new GoalId("aaaaaaaa111111111111111111111111"), "Active feature");
        var verified = kernel.CreateGoal(new GoalId("bbbbbbbb222222222222222222222222"), "Verified feature");
        var parked = kernel.CreateGoal(new GoalId("cccccccc333333333333333333333333"), "Parked feature");
        var landed = kernel.CreateGoal(new GoalId("dddddddd444444444444444444444444"), "Landed feature");
        kernel.ActivateGoal(active.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(verified.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(parked.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(landed.Id, AgentCatalog.Default().Agents);
        kernel = WithStatus(kernel, verified.Id, GoalStatus.Verified);
        kernel = WithStatus(kernel, parked.Id, GoalStatus.Parked);
        kernel = WithStatus(kernel, landed.Id, GoalStatus.Completed);

        await store.AssignGoalToEpicAsync(active.Id.Value, epic.Id);
        await store.AssignGoalToEpicAsync(verified.Id.Value, epic.Id);
        await store.AssignGoalToEpicAsync(parked.Id.Value, epic.Id);
        await store.AssignGoalToEpicAsync(landed.Id.Value, epic.Id);
        await store.AssignBacklogItemToEpicAsync("backlog11111111111111111111111111", epic.Id);

        var membership = await store.GetGoalMembershipAsync(active.Id.Value);
        var rollup = (await store.BuildEpicRollupsAsync(kernel.Goals)).Single();

        Assert.Equal("At-a-glance board", membership!.EpicTitle);
        Assert.Equal("Control plane", membership.ProjectTitle);
        Assert.Equal(4, rollup.GoalCount);
        Assert.Equal(1, rollup.BacklogItemCount);
        Assert.Equal(1, rollup.ActiveCount);
        Assert.Equal(1, rollup.VerifiedCount);
        Assert.Equal(1, rollup.ParkedCount);
        Assert.Equal(1, rollup.LandedCount);
        Assert.NotNull(rollup.NewestTransitionAt);
    }

    [Xunit.Fact(DisplayName = "PortfolioStore_goal_membership_is_many_to_one")]
    public async Task GoalMembershipIsManyToOne()
    {
        var store = new PortfolioStore(TempDb());
        var first = await store.AddEpicAsync("First epic");
        var second = await store.AddEpicAsync("Second epic");

        await store.AssignGoalToEpicAsync("aaaaaaaa111111111111111111111111", first.Id);
        await store.AssignGoalToEpicAsync("aaaaaaaa111111111111111111111111", second.Id);

        var membership = await store.GetGoalMembershipAsync("aaaaaaaa111111111111111111111111");

        Assert.Equal(second.Id, membership!.EpicId);
    }

    private static string TempDb() => Path.Combine(CreateTempDirectory(), "portfolio.db");

    private static AgentOrchestratorKernel WithStatus(AgentOrchestratorKernel kernel, GoalId goalId, GoalStatus status)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(goal => goal.Id == goalId.Value ? goal with { Status = status } : goal)
                .ToArray()
        });
    }
}
