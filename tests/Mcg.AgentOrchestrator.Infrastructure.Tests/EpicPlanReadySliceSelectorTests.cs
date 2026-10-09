using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class EpicPlanReadySliceSelectorTests
{
    [Fact]
    public void Ready_slices_keep_position_order_with_deterministic_epic_order()
    {
        var a = View("a", [Slice(2, "second", EpicPlanSliceStatus.Ready),
            Slice(3, "blocked", EpicPlanSliceStatus.Blocked), Slice(1, "first", EpicPlanSliceStatus.Ready),
            Slice(4, "live", EpicPlanSliceStatus.InFlight),
            new(new(5, EpicPlanItemKind.Step, null, "Manual step", false), null, "Manual step")]);
        var b = View("b", [Slice(1, "other", EpicPlanSliceStatus.Ready)]);
        Assert.Equal(new[] { "first", "second", "other" }, EpicPlanReadySliceSelector.Order([b, a]));
    }

    [Fact]
    public async Task Missing_store_and_epics_without_plans_contribute_no_preference()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        Assert.Empty(EpicPlanReadySliceSelector.Read(fixture.Workspace));
        Assert.False(File.Exists(fixture.Workspace.PortfolioStorePath));
        await new PortfolioStore(fixture.Workspace.PortfolioStorePath).AddEpicAsync("No plan");
        var before = File.ReadAllBytes(fixture.Workspace.PortfolioStorePath);
        Assert.Empty(EpicPlanReadySliceSelector.Read(fixture.Workspace));
        Assert.Equal(before, File.ReadAllBytes(fixture.Workspace.PortfolioStorePath));
    }

    [Fact]
    public async Task Workspace_read_uses_status_reader_to_exclude_blocked_slices()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var epic = await new PortfolioStore(fixture.Workspace.PortfolioStorePath).AddEpicAsync("Plan");
        var blocked = await fixture.Store.AddAsync("Blocked");
        await fixture.Store.AddDependencyAsync(blocked.Id, new(fixture.Item.Id, BacklogDependencyTargetKind.Backlog));
        var plan = new EpicPlanStore(fixture.Workspace.PortfolioStorePath);
        await plan.AddSliceAsync(epic.Id, blocked.Id);
        await plan.AddSliceAsync(epic.Id, fixture.Item.Id);
        var before = File.ReadAllBytes(fixture.Workspace.PortfolioStorePath);
        Assert.Equal(new[] { fixture.Item.Id }, EpicPlanReadySliceSelector.Read(fixture.Workspace));
        Assert.Equal(before, File.ReadAllBytes(fixture.Workspace.PortfolioStorePath));
    }

    private static EpicPlanItemStatus Slice(int position, string id, EpicPlanSliceStatus status) =>
        new(new(position, EpicPlanItemKind.Slice, id, null, false), status, id);

    private static EpicPlanView View(string epicId, IReadOnlyList<EpicPlanItemStatus> items) =>
        new(new(epicId, null, items.Select(item => item.Item).ToArray(), []), items, []);
}
