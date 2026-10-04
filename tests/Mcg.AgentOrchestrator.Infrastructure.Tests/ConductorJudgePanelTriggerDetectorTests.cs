using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorJudgePanelTriggerDetectorTests
{
    [Fact]
    public async Task Six_persisted_disputes_have_exact_keys_and_survive_restart()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        var trx = f.Trx();
        // Match producer text: these records do not invent candidate_sha/base_sha fields.
        f.Timeline(0, $"Tester task 1234abcd stayed verification-inconclusive on unchanged inputs; operator action required. Candidate: {f.Panel.Candidate}; receipt ids: none; latest current-round receipt: {trx}");
        f.Timeline(1, $"PRE_REVIEW_RED_UNCHANGED_CANDIDATE: candidate {f.Panel.Candidate} failed again without typed test identities; diagnostic: build failed.");
        var redRuns = string.Join(" | ", Enumerable.Range(1, 3).Select(index =>
            $"finding-evidence pre-tester outcome=actionable-red; candidate_sha={f.Panel.Candidate}; receipt_id=red-{index}; selections=ExampleTests; not_run=; result_path={Uri.EscapeDataString(trx)}; failing_tests=ExampleTests.Dispute"));
        f.Timeline(2, "PRE_TESTER_RED_LOOP: three consecutive candidate RED runs without Tester dispatch; failing_sets=" + redRuns);
        f.Timeline(3, "Acceptance RED classified as apparatus (test-host); restored Verified for re-gate 1/2.", decision: true);
        var authorId = f.AuthorQuestion();
        f.CohortFailure("both-failed", trx);
        f.PrepareProducerReadArtifacts();
        var hashes = f.ProducerHashes();
        var cases = f.Detector().Detect();
        Assert.Equal(6, cases.Count);
        var expected = new Dictionary<string, string>
        {
            ["tester-inconclusive-unchanged"] = $"goal-event:{f.GoalId}:0",
            ["pre-review-evidence"] = $"goal-event:{f.GoalId}:1",
            ["pre-tester-red-loop"] = $"goal-event:{f.GoalId}:2",
            ["apparatus-red"] = $"goal-event:{f.GoalId}:3",
            ["author-ask-owner"] = authorId,
            ["cohort-both-failed"] = "cohort-receipt:both-failed"
        };
        Assert.Equal(expected.Keys.Order(), cases.Select(item => item.Key.TriggerKind).Order());
        Assert.All(cases, item =>
        {
            var candidateRecorded = item.Key.TriggerKind is "pre-review-evidence" or "pre-tester-red-loop" or "cohort-both-failed";
            var baseRecorded = item.Key.TriggerKind == "cohort-both-failed";
            var key = new PanelCaseKey(f.GoalId, candidateRecorded ? f.Panel.Candidate : PanelTrigger.UnrecordedSha,
                baseRecorded ? f.BaseSha : PanelTrigger.UnrecordedSha, f.CriteriaVersion,
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
        f.Timeline(100, $"PRE_REVIEW_RED_UNCHANGED_CANDIDATE: candidate {f.Panel.Candidate} failed again without typed test identities; diagnostic: build failed.");
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
        var reason = $"Acceptance RED classified as apparatus (test-host) for candidate branch={f.Panel.Candidate[..12]} main={f.BaseSha[..12]}: every failing test lies outside the candidate's changed paths (ExampleTests.Dispute). Re-gating on the next conduct tick (1/2); no worker was reopened.";
        f.Conduct("goal", $"GOAL goal={f.GoalId[..8]} result=held state=Verified reason={ConductorBatchLoop.SanitizeReason(reason)}",
            rotated: true, goalId: f.GoalId[..8]);
        var item = Assert.Single(f.Detector().Detect(f.GoalId));
        Assert.Equal(f.GoalId, item.Key.GoalId);
        Assert.Equal(f.CriteriaVersion, item.Key.CriteriaVersion);
        Assert.Equal("pending", item.Status);
        Assert.Equal("apparatus-red", item.Key.TriggerKind);
        Assert.Equal(f.Panel.Candidate[..12], item.Key.CandidateSha);
        Assert.Equal(f.BaseSha[..12], item.Key.BaseSha);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Apparatus_timeline_and_held_conduct_enroll_one_case(
        bool enrollTimelineFirst, bool sameTimestamp, bool truncatedConduct)
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        f.Timeline(0, "Acceptance RED classified as apparatus (test-host); restored Verified for re-gate 1/2.", decision: true);
        if (enrollTimelineFirst) Assert.Single(f.Detector().Detect());
        if (!sameTimestamp) f.Panel.Time.UtcNow += TimeSpan.FromSeconds(1);
        var failingTests = truncatedConduct ? new string('f', 512) : "ExampleTests.Dispute";
        var reason = $"Acceptance RED classified as apparatus (test-host) for candidate branch={f.Panel.Candidate[..12]} main={f.BaseSha[..12]}: every failing test lies outside the candidate's changed paths ({failingTests}). Re-gating on the next conduct tick (1/2); no worker was reopened.";
        var sanitized = ConductorBatchLoop.SanitizeReason(reason);
        if (truncatedConduct) Assert.DoesNotContain("conduct_tick_(1/2)", sanitized);
        f.Conduct("goal", $"GOAL goal={f.GoalId[..8]} result=held state=Verified reason={sanitized}",
            rotated: true, goalId: f.GoalId[..8]);
        f.PrepareProducerReadArtifacts();
        var hashes = f.ProducerHashes();
        var reopened = new ConductorJudgePanelCaseStore(Path.Combine(f.Panel.Root, "panel.db"));
        var added = f.Detector(reopened).Detect();
        Assert.Equal(enrollTimelineFirst ? 0 : 1, added.Count);
        var item = Assert.Single(reopened.Cases());
        Assert.Equal("apparatus-red", item.Key.TriggerKind);
        Assert.Equal($"goal-event:{f.GoalId}:0", item.Key.TriggerId);
        Assert.Equal(PanelTrigger.UnrecordedSha, item.Key.CandidateSha);
        Assert.Equal(PanelTrigger.UnrecordedSha, item.Key.BaseSha);
        Assert.Equal(f.CriteriaVersion, item.Key.CriteriaVersion);
        Assert.Empty(f.Detector(reopened).Detect());
        Assert.Single(reopened.Cases());
        Assert.Equal(hashes.OrderBy(pair => pair.Key), f.ProducerHashes().OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData("test-host", "2/2", false)]
    [InlineData("assembly-host", "1/2", false)]
    [InlineData("test-host", "1/2", true)]
    public async Task Apparatus_conduct_without_matching_prior_regate_remains_eligible(
        string evidenceKind, string ordinal, bool laterTimeline)
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        var at = f.Panel.Time.UtcNow;
        if (laterTimeline) f.Panel.Time.UtcNow += TimeSpan.FromSeconds(1);
        f.Timeline(0, "Acceptance RED classified as apparatus (test-host); restored Verified for re-gate 1/2.", decision: true);
        f.Panel.Time.UtcNow = at;
        var reason = $"Acceptance RED classified as apparatus ({evidenceKind}) for candidate branch={f.Panel.Candidate[..12]} main={f.BaseSha[..12]}: every failing test lies outside the candidate's changed paths (ExampleTests.Dispute). Re-gating on the next conduct tick ({ordinal}); no worker was reopened.";
        f.Conduct("goal", $"GOAL goal={f.GoalId[..8]} result=held state=Verified reason={ConductorBatchLoop.SanitizeReason(reason)}",
            goalId: f.GoalId[..8]);
        var cases = f.Detector().Detect();
        Assert.Equal(2, cases.Count);
        Assert.Contains(cases, item => item.Key.TriggerId == $"goal-event:{f.GoalId}:0");
        Assert.Contains(cases, item => item.Key.CandidateSha == f.Panel.Candidate[..12]);
    }

    [Fact]
    public async Task Apparatus_timeline_copy_does_not_hide_a_later_conduct_only_candidate()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        f.Timeline(0, "Acceptance RED classified as apparatus (test-host); restored Verified for re-gate 1/2.", decision: true);
        foreach (var candidate in new[] { f.Panel.Candidate[..12], new string('d', 12) })
        {
            f.Panel.Time.UtcNow += TimeSpan.FromSeconds(1);
            var reason = $"Acceptance RED classified as apparatus (test-host) for candidate branch={candidate} main={f.BaseSha[..12]}: every failing test lies outside the candidate's changed paths (ExampleTests.Dispute). Re-gating on the next conduct tick (1/2); no worker was reopened.";
            f.Conduct("goal", $"GOAL goal={f.GoalId[..8]} result=held state=Verified reason={ConductorBatchLoop.SanitizeReason(reason)}",
                goalId: f.GoalId[..8]);
        }
        var cases = f.Detector().Detect();
        Assert.Equal(2, cases.Count);
        Assert.Contains(cases, item => item.Key.TriggerId == $"goal-event:{f.GoalId}:0");
        Assert.Contains(cases, item => item.Key.CandidateSha == new string('d', 12));
        Assert.DoesNotContain(cases, item => item.Key.CandidateSha == f.Panel.Candidate[..12]);
        Assert.Empty(f.Detector().Detect());
        Assert.Equal(2, f.Panel.Store.Cases().Count);
    }

    [Theory]
    [InlineData("PRE_REVIEW_RED_UNCHANGED_CANDIDATE")]
    [InlineData("PRE_REVIEW_EVIDENCE_TIMEOUT")]
    public async Task Timeline_and_sanitized_conduct_enroll_one_case(string code)
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        var text = code == "PRE_REVIEW_EVIDENCE_TIMEOUT"
            ? $"{code}: candidate {f.Panel.Candidate} focused evidence hit its time budget on consecutive runs without failing test identities; operator action required."
            : $"{code}: candidate {f.Panel.Candidate} failed again without typed test identities; diagnostic: build failed.";
        f.Timeline(0, text);
        // Conduct writes the same dispute later using an eight-character goal prefix and sanitized reason.
        f.Panel.Time.UtcNow += TimeSpan.FromSeconds(1);
        f.Conduct("goal", $"GOAL goal={f.GoalId[..8]} result=escalated state=Active reason={ConductorBatchLoop.SanitizeReason(text)}",
            goalId: f.GoalId[..8]);
        Assert.Equal(2, f.Sources.Read().Count);
        var item = Assert.Single(f.Detector().Detect());
        Assert.Equal(f.Panel.Candidate, item.Key.CandidateSha);
        Assert.Equal(PanelTrigger.UnrecordedSha, item.Key.BaseSha);
        Assert.Equal($"goal-event:{f.GoalId}:0", item.Key.TriggerId);
        Assert.Equal("pre-review-evidence", item.Key.TriggerKind);
        Assert.Equal(f.CriteriaVersion, item.Key.CriteriaVersion);
        var reopened = new ConductorJudgePanelCaseStore(Path.Combine(f.Panel.Root, "panel.db"));
        Assert.Empty(f.Detector(reopened).Detect());
        Assert.Single(reopened.Cases());
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
