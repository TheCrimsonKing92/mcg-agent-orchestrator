using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PortfolioStoreTests
{
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
