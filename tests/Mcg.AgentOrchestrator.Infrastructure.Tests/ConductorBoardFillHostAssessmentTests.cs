using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class ConductorBoardFillHostAssessmentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completed_assessment_extends_the_original_event_prefix(bool verified)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var first = h.Kernel.CreateGoal("Change src/Feature/File.cs.");
        var second = h.Kernel.CreateGoal("Change src/Feature/File.cs.");
        h.Kernel.CreateGoal("Change src/Other/File.cs.");
        var calls = 0;
        var host = h.Host(new BoardFillAssessmentTestFixture.Verifier((premise, head, _) =>
        {
            calls++;
            Assert.Equal(BoardFillVerifierContract.Premise(BoardFillAssessmentTestFixture.Brief), premise);
            Assert.Equal(BoardFillAssessmentTestFixture.Head, head);
            return Task.FromResult(verified ? BoardFillAssessmentTestFixture.Verified :
                new BoardFillPremiseVerification("complete", 1, [new(1, "contradicted", "src/Feature/File.cs:1")]));
        }));
        host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(host.CurrentRound!, "verification finished");
        Assert.Null(Assert.Single(h.Store.ReadAll()).Outcome);
        Assert.False(File.Exists(h.EventPath));
        host.ServiceTick(h.Kernel);
        Assert.Equal(1, calls);
        var round = Assert.Single(h.Store.ReadAll());
        Assert.NotNull(round.Assessment);
        Assert.Equal(verified, round.Assessment.Fileable);
        Assert.Equal(new[] { first.Id.Value, second.Id.Value }.Order(), round.Assessment.Scope.Depends.Select(dependency => dependency.GoalId));
        Assert.All(round.Assessment.Scope.Depends, dependency => Assert.Equal("ExactFile src/Feature/File.cs<->src/Feature/File.cs", dependency.Reason));
        using var entry = BoardFillTestHarness.Event(Assert.Single(File.ReadAllLines(h.EventPath)));
        var prefix = $"BOARD_FILL_DRAFT backlog={h.Item.Id[..8]} outcome=draft checks=4/4 draft={h.DraftPath}";
        var depends = string.Join(',', new[] { first.Id.Value, second.Id.Value }.Order().Select(id => id[..8]));
        Assert.Equal(prefix + $" verified={(verified ? 1 : 0)}/1 fileable={verified.ToString().ToLowerInvariant()} depends={depends}",
            entry.RootElement.GetProperty("detail").GetString());
        Assert.True(round.Reported);
        host.ServiceTick(h.Kernel);
        Assert.Single(File.ReadAllLines(h.EventPath));
        Assert.Equal(4, round.Checks.Count);
    }

    [Fact]
    public async Task Pending_verifier_keeps_tick_idle_and_assessment_unpublished()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<BoardFillPremiseVerification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = h.Host(new BoardFillAssessmentTestFixture.Verifier((_, _, token) =>
        {
            started.SetResult(true);
            return release.Task.WaitAsync(token);
        }));
        host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(started.Task, "verifier entered process seam");
        var task = host.CurrentRound;
        host.ServiceTick(h.Kernel);
        Assert.Same(task, host.CurrentRound);
        Assert.Null(Assert.Single(h.Store.ReadAll()).Assessment);
        Assert.False(File.Exists(h.EventPath));
        release.SetResult(BoardFillAssessmentTestFixture.Verified);
        await PanelTestHarness.Signal(task!, "released verification finished");
        host.ServiceTick(h.Kernel);
        Assert.True(Assert.Single(h.Store.ReadAll()).Assessment!.Fileable);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("linked")]
    public async Task Harvest_rechecks_backlog_changes_and_new_links(string change)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var host = h.Host(new BoardFillAssessmentTestFixture.Verifier((_, _, _) => Task.FromResult(BoardFillAssessmentTestFixture.Verified)));
        host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(host.CurrentRound!, "draft and verification finished");
        string reason;
        if (change == "changed")
        {
            h.Item = h.Item with { UpdatedAt = h.Item.UpdatedAt.AddMinutes(1), Body = "Edited brief source" };
            reason = "backlog-changed";
        }
        else
        {
            var goal = h.Kernel.CreateGoal("Operator goal after drafting");
            h.Kernel.SetGoalSourceBacklogItemLink(goal.Id, h.Item.Id, SourceBacklogCoverage.Full);
            reason = "backlog-linked-goal:" + goal.Id.Value;
        }
        host.ServiceTick(h.Kernel);
        var assessment = Assert.Single(h.Store.ReadAll()).Assessment!;
        Assert.False(assessment.Fileable);
        Assert.Contains(reason, assessment.FailingReasons);
    }

    [Fact]
    public void Recovery_marks_unassessed_drafts_unavailable_and_store_rejects_reassessment()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var round = h.Finished();
        var host = h.Host(new BoardFillAssessmentTestFixture.Verifier((_, _, _) => throw new Exception("Recovery must not call model")));
        host.ServiceTick(h.Kernel);
        var assessed = Assert.Single(h.Store.ReadAll());
        Assert.Equal("assessment-interrupted", assessed.Assessment!.Verification.Detail);
        Assert.False(assessed.Assessment.Fileable);
        Assert.True(assessed.Reported);
        Assert.Throws<InvalidOperationException>(() => h.Store.Assess(round.Id, assessed.Assessment));
    }
}
