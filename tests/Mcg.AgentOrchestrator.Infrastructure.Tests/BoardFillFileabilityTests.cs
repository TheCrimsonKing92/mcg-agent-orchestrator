using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BoardFillFileabilityTests
{
    [Fact]
    public void All_conditions_pass_and_collisions_do_not_block_fileability()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var assessment = Evaluate(h, scope: new("overlap-detected", [new("goal", "ExactFile path<->path")]));
        Assert.True(assessment.Fileable);
        Assert.Empty(assessment.FailingReasons);
        Assert.Equal("goal", Assert.Single(assessment.Scope.Depends).GoalId);
    }

    [Theory]
    [InlineData("outcome", "outcome:stale")]
    [InlineData("structural", "structural:sections")]
    [InlineData("unreadable", "draft-unreadable")]
    [InlineData("unavailable", "verification:unavailable expected-one-binding")]
    [InlineData("contradicted", "premise-bullet-1:contradicted")]
    [InlineData("unverifiable", "premise-bullet-1:unverifiable")]
    [InlineData("empty-verification", "verification:incomplete")]
    [InlineData("missing-verdict", "verification:incomplete")]
    [InlineData("preflight", "preflight:build-output-path")]
    [InlineData("missing", "backlog-missing")]
    [InlineData("changed", "backlog-changed")]
    [InlineData("noted", "backlog-changed")]
    [InlineData("closed", "backlog-ineligible")]
    [InlineData("owner-gated", "backlog-ineligible")]
    [InlineData("readiness", "backlog-ineligible")]
    public void Each_failed_condition_records_a_reason_and_blocks(string fault, string reason)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var round = h.Finished();
        var verification = BoardFillAssessmentTestFixture.Verified;
        var current = h.Item;
        string? markdown = BoardFillAssessmentTestFixture.Brief;
        var preflight = BoardFillPreflightChecks.Run(markdown);
        Func<BacklogItem, BacklogReadiness> readiness = BoardFillAssessmentTestFixture.Ready;
        switch (fault)
        {
            case "outcome": round = round with { Outcome = "stale" }; break;
            case "structural": round = round with { Checks = [new("sections", false, "missing")] }; break;
            case "unreadable": markdown = null; break;
            case "unavailable": verification = new("unavailable", 1, [], "expected-one-binding"); break;
            case "contradicted": case "unverifiable": verification = new("complete", 1, [new(1, fault, "src/Feature/File.cs:1")]); break;
            case "empty-verification": verification = new("complete", 0, []); break;
            case "missing-verdict": verification = new("complete", 1, []); break;
            case "preflight": preflight = preflight.Select(check => check.Name == "preflight:build-output-path" ? check with { Passed = false } : check).ToArray(); break;
            case "missing": current = null; break;
            case "changed": current = current with { UpdatedAt = current.UpdatedAt.AddMinutes(1) }; break;
            case "noted": current = current with { Notes = [new(current.UpdatedAt.AddMinutes(1), "Edited")] }; break;
            case "closed": current = current with { Status = BacklogItemStatus.Done }; break;
            case "owner-gated": current = current with { Tags = "owner-gated" }; break;
            case "readiness": readiness = _ => new(false, false, "unsatisfied"); break;
        }
        var assessment = BoardFillFileability.Evaluate(round, markdown, verification, preflight,
            BoardFillAssessmentTestFixture.NoDepends, current, [], readiness);
        Assert.False(assessment.Fileable);
        Assert.Contains(reason, assessment.FailingReasons);
    }

    [Fact]
    public void All_failures_are_listed_and_a_new_link_blocks_even_verified_drafts()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var round = h.Finished();
        var goal = h.Kernel.CreateGoal("Linked after drafting");
        h.Kernel.SetGoalSourceBacklogItemLink(goal.Id, h.Item.Id, SourceBacklogCoverage.Full);
        var assessment = BoardFillFileability.Evaluate(round with { Checks = [new("sections", false, "missing")] },
            BoardFillAssessmentTestFixture.Brief, new("complete", 1, [new(1, "contradicted", "src/Feature/File.cs:1")]),
            BoardFillPreflightChecks.Run(BoardFillAssessmentTestFixture.Brief + "\nsrc/bin/output.cs"),
            BoardFillAssessmentTestFixture.NoDepends, h.Item with { UpdatedAt = h.Item.UpdatedAt.AddMinutes(1) },
            h.Kernel.Goals.ToArray(), BoardFillAssessmentTestFixture.Ready);
        Assert.False(assessment.Fileable);
        Assert.Equal(new[] { "structural:sections", "premise-bullet-1:contradicted", "preflight:build-output-path",
            "backlog-changed", "backlog-linked-goal:" + goal.Id.Value, "backlog-ineligible" }, assessment.FailingReasons);
    }

    internal static BoardFillDraftAssessment Evaluate(BoardFillAssessmentTestFixture h, BoardFillScopeProposal? scope = null) =>
        BoardFillFileability.Evaluate(h.Finished(), BoardFillAssessmentTestFixture.Brief, BoardFillAssessmentTestFixture.Verified,
            BoardFillPreflightChecks.Run(BoardFillAssessmentTestFixture.Brief), scope ?? BoardFillAssessmentTestFixture.NoDepends,
            h.Item, h.Kernel.Goals.ToArray(), BoardFillAssessmentTestFixture.Ready);
}
