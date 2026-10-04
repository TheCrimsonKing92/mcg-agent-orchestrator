using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorJudgePanelTriggerDetectorTests
{
    [Fact]
    public async Task Six_persisted_disputes_have_exact_keys_and_survive_restart()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        var trx = f.Trx();
        f.Timeline(0, f.BoundText("Tester stayed verification-inconclusive on unchanged inputs", trx));
        f.Timeline(1, f.BoundText("PRE_REVIEW_RED_UNCHANGED_CANDIDATE: repeated red", trx));
        f.Timeline(2, f.BoundText("PRE_TESTER_RED_LOOP: three candidate RED runs", trx), decision: true);
        f.Conduct("goal", f.BoundText("Acceptance RED classified as apparatus (test-host)", trx), rotated: true);
        var authorId = f.AuthorQuestion();
        f.CohortFailure("both-failed", trx);
        var hashes = f.ProducerHashes();
        var cases = f.Detector().Detect();
        Assert.Equal(6, cases.Count);
        var expected = new Dictionary<string, string>
        {
            ["tester-inconclusive-unchanged"] = $"goal-event:{f.GoalId}:0",
            ["pre-review-evidence"] = $"goal-event:{f.GoalId}:1",
            ["pre-tester-red-loop"] = $"goal-event:{f.GoalId}:2",
            ["apparatus-red"] = $"conduct-event:{f.GoalId}:{f.Panel.Time.UtcNow:O}",
            ["author-ask-owner"] = authorId,
            ["cohort-both-failed"] = "cohort-receipt:both-failed"
        };
        Assert.Equal(expected.Keys.Order(), cases.Select(item => item.Key.TriggerKind).Order());
        Assert.All(cases, item =>
        {
            var unknown = item.Key.TriggerKind == "author-ask-owner";
            var key = new PanelCaseKey(f.GoalId, unknown ? PanelTrigger.UnrecordedSha : f.Panel.Candidate,
                unknown ? PanelTrigger.UnrecordedSha : f.BaseSha, f.CriteriaVersion,
                expected[item.Key.TriggerKind], item.Key.TriggerKind, item.Key.Packet);
            Assert.Equal(key, item.Key);
            Assert.Equal(ConductorJudgePanelCaseStore.CaseId(key), item.Id);
            Assert.Equal("pending", item.Status);
            Assert.Contains(ConductorJudgePanelPacketBuilder.AuthorityBoundary, item.Key.Packet);
        });
        var diffCalls = f.DiffCalls;
        var reopened = new ConductorJudgePanelCaseStore(Path.Combine(f.Panel.Root, "panel.db"));
        Assert.Empty(f.Detector(reopened).Detect());
        Assert.Equal(6, reopened.Cases().Count);
        Assert.Equal(diffCalls, f.DiffCalls);
        Assert.Equal(hashes.OrderBy(pair => pair.Key), f.ProducerHashes().OrderBy(pair => pair.Key));
        // A producer can re-raise with a new cursor; it is still the same candidate dispute.
        f.Timeline(100, f.BoundText("PRE_REVIEW_RED_UNCHANGED_CANDIDATE: raised again", trx));
        Assert.Empty(f.Detector(reopened).Detect());
    }

    [Fact]
    public async Task Enrollment_overflow_keeps_trigger_and_never_calls_a_judge()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        for (var index = 0; index < ConductorJudgePanelBudgets.MaxCasesPerEnrollmentWindow; index++)
        {
            f.Panel.Enqueue("already-enrolled-" + index);
            var claim = f.Panel.Store.ClaimNext(f.Panel.Time.UtcNow)!;
            f.Panel.Store.Complete(claim, PanelCaseTerminal.Completed, null); // Completed enrollments still spend budget.
        }
        f.Timeline(0, "PRE_TESTER_RED_LOOP: next dispute");
        var skipped = Assert.Single(f.Detector().Detect());
        Assert.Equal(PanelCaseTerminal.BudgetSkip, skipped.Terminal);
        Assert.Equal("enrollment-window", skipped.Reason);
        Assert.Equal($"goal-event:{f.GoalId}:0", skipped.Key.TriggerId);
        Assert.Empty(f.Panel.Store.Results(skipped.Id));
        var host = new ConductorJudgePanelHost(f.Panel.Store, f.Panel.Sol, f.Panel.Sonnet,
            _ => f.Panel.Candidate, new ConductEventLogWriter(f.Panel.ConductPath), () => f.Panel.Time.UtcNow)
            { Triggers = f.Detector() };
        try
        {
            host.ServiceTick(f.Panel.Kernel);
            Assert.Null(host.CurrentCase);
            Assert.Equal(0, f.Panel.Sol.Calls);
            Assert.Equal(0, f.Panel.Sonnet.Calls);
            Assert.Empty(f.Panel.Store.Results(skipped.Id));
            Assert.Equal(ConductorJudgePanelBudgets.MaxCasesPerEnrollmentWindow + 1, f.Panel.Store.Cases().Count);
        }
        finally { host.Stop(); }
    }

    [Fact]
    public async Task Conduct_prefix_resolves_to_enrolled_persisted_goal()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        f.Conduct("goal", f.BoundText("Acceptance RED classified as apparatus (test-host)", f.Trx()),
            goalId: f.GoalId[..8]);
        var item = Assert.Single(f.Detector().Detect(f.GoalId));
        Assert.Equal(f.GoalId, item.Key.GoalId);
        Assert.Equal(f.CriteriaVersion, item.Key.CriteriaVersion);
        Assert.Equal("pending", item.Status);
        Assert.Equal("apparatus-red", item.Key.TriggerKind);
    }

    [Fact]
    public async Task Host_detects_before_claim_and_timeout_code_uses_pre_review_kind()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        f.Timeline(0, "PRE_REVIEW_EVIDENCE_TIMEOUT: candidate " + f.Panel.Candidate + " hit budget");
        var host = new ConductorJudgePanelHost(f.Panel.Store, f.Panel.Sol, f.Panel.Sonnet,
            _ => f.Panel.Candidate, new ConductEventLogWriter(f.Panel.ConductPath), () => f.Panel.Time.UtcNow)
            { Triggers = f.Detector() };
        try
        {
            host.ServiceTick(f.Panel.Kernel);
            Assert.NotNull(host.CurrentCase);
            await PanelTestHarness.Signal(host.CurrentCase!, "detected panel case judges completed");
            var item = Assert.Single(f.Panel.Store.Cases());
            Assert.Equal("pre-review-evidence", item.Key.TriggerKind);
            Assert.Contains("Trigger detail\nPRE_REVIEW_EVIDENCE_TIMEOUT", item.Key.Packet);
            Assert.Equal(2, f.Panel.Store.Results(item.Id).Count);
            Assert.Equal(1, f.Panel.Sol.Calls);
            Assert.Equal(1, f.Panel.Sonnet.Calls);
        }
        finally { host.Stop(); }
    }
}
