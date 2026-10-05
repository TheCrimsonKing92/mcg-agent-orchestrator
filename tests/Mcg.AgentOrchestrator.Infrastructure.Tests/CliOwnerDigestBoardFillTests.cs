using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class CliOwnerDigestBoardFillTests
{
    [Fact]
    public void Seven_day_digest_lists_drafts_failures_depends_and_matching_operator_goal()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var first = h.Finished(h.Clock.UtcNow.AddDays(-1));
        var second = h.Finished(h.Clock.UtcNow.AddDays(-2), BoardFillReadyItemSelectorTests.Item('b'));
        var third = h.Finished(h.Clock.UtcNow.AddDays(-3), BoardFillReadyItemSelectorTests.Item('d'));
        h.Finished(h.Clock.UtcNow.AddDays(-7).AddTicks(-1), BoardFillReadyItemSelectorTests.Item('e'));
        h.Finished(h.Clock.UtcNow.AddTicks(1), BoardFillReadyItemSelectorTests.Item('f'));
        var goal = h.Kernel.CreateGoal("Operator authored");
        h.Kernel.SetGoalSourceBacklogItemLink(goal.Id, second.BacklogItemId, SourceBacklogCoverage.Full);
        var assessment = new BoardFillDraftAssessment(BoardFillAssessmentTestFixture.Verified,
            BoardFillPreflightChecks.Run(BoardFillAssessmentTestFixture.Brief), BoardFillAssessmentTestFixture.NoDepends, true, []);
        h.Store.Assess(first.Id, assessment);
        h.Store.Assess(second.Id, assessment with { Fileable = false, FailingReasons = ["backlog-changed", "preflight:build-output-path"],
            Scope = new("overlap-detected", [new(goal.Id.Value, "ExactFile path<->path")]) });
        var rows = CliOwnerDigestBoardFill.Build(h.Store.ReadAll(), h.Kernel.Goals.ToArray(), h.Clock.UtcNow);
        Assert.Equal(new[]
        {
            "dddddddd | draft | fileable=false | reasons=not-assessed | depends=none | operator-goal=none",
            $"bbbbbbbb | draft | fileable=false | reasons=backlog-changed; preflight:build-output-path | depends={goal.Id.Value[..8]} | operator-goal={goal.Id.Value}",
            "aaaaaaaa | draft | fileable=true | reasons=none | depends=none | operator-goal=none"
        }, rows);
        Assert.Equal(third.Id, h.Store.ReadAll().Single(round => round.BacklogItemId == third.BacklogItemId).Id);
    }

    [Fact]
    public void Seven_day_boundary_is_included_using_supplied_clock()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var round = h.Finished(h.Clock.UtcNow.AddDays(-7));
        Assert.Single(CliOwnerDigestBoardFill.Build([round], [], h.Clock.UtcNow));
        h.Clock.UtcNow = h.Clock.UtcNow.AddTicks(1);
        Assert.Empty(CliOwnerDigestBoardFill.Build([round], [], h.Clock.UtcNow));
    }

    [Fact]
    public async Task Digest_command_renders_the_store_and_absence_writes_nothing()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        using var output = new StringWriter();
        var workspace = fixture.Workspace;
        CliOwnerDigestBoardFill.WriteText(output, workspace, [], fixture.Clock.UtcNow);
        Assert.Equal("", output.ToString());
        Assert.False(File.Exists(ConductorBoardFillDraftStore.DefaultPath(workspace)));
        var store = new ConductorBoardFillDraftStore(ConductorBoardFillDraftStore.DefaultPath(workspace));
        var round = store.Begin(BoardFillReadyItemSelectorTests.Item() with { UpdatedAt = fixture.Clock.UtcNow }, fixture.Clock.UtcNow);
        store.Finish(round, new("failed", 1, null, null, null, [], "fixture"), fixture.Clock.UtcNow);
        Assert.Equal(0, CliOwnerDigestCommand.Run(["owner-digest"], workspace, fixture.Clock, output));
        Assert.Contains("Board-fill drafts (last 7 days):" + Environment.NewLine +
            "aaaaaaaa | failed | fileable=false | reasons=not-assessed | depends=none | operator-goal=none", output.ToString());
    }
}
