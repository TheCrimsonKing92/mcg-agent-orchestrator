using Mcg.AgentOrchestrator.App.OwnerConsole;
using Microsoft.Data.Sqlite;

// Parallel-safe: pure inputs; the date tests use an explicit clock and zone.
public sealed class OwnerActivityNarratorTests
{
    public static TheoryData<string, string> VocabularyCases => new()
    {
        { "loop-relaunch", "LOOP_RELAUNCH_SCHEDULED tick=1" },
        { "loop-relaunch", "LOOP_RELAUNCH_NOT_REQUIRED tick=1" },
        { "loop-handoff", "ACTIVATION_ADOPTED reason=none" },
        { "loop-handoff", "ACTIVATION_REVERTED reason=receipt_missing" },
        { "loop-handoff", "ACTIVATION_FAILED_BOTH reason=canary_failed" },
        { "loop-handoff", "LOOP_HANDOFF_FAILED reason=publish_failed" },
        { "loop-handoff", "LOOP_HANDOFF reason=none" },
        { "canary-gate", "CANARY_GATE result=passed reason=none" },
        { "canary-gate", "CANARY_GATE result=failed reason=canary_failed tests=Check.Main" },
        { "canary-gate", "CANARY_GATE result=unverified reason=receipt_missing" },
        { "canary-gate", "CANARY_GATE result=deferred reason=tick_budget" },
        { "acceptance", "ACCEPTANCE result=started" },
        { "acceptance", "ACCEPTANCE result=passed reason=none" },
        { "acceptance", "ACCEPTANCE result=failed reason=child-result-published" },
        { "acceptance", "ACCEPTANCE result=blocked reason=missing_receipt" },
        { "acceptance-cohort", "ACCEPTANCE_COHORT members=11111111,22222222 outcome=failed attribution=Indeterminate partitions=11111111:Passed,22222222:Passed" },
        { "acceptance-cohort", "ACCEPTANCE_COHORT members=11111111,22222222 outcome=passed reason=none" },
        { "acceptance-cohort", "ACCEPTANCE_COHORT_CHILD_COMPLETED members=11111111+22222222 verdict=failed reason=child-result-published" },
        { "goal-escalation", "steward-owner-question question=Retry? evidence=[receipt]" },
        { "goal-escalation", "author-owner-question question=Retry? recommendation=Retry" },
        { "author", "kind=ask-owner item=internal-id" },
        { "goal-escalation", "owner-review-hold reason=none" },
        { "goal-escalation", "ownerless-hold-stalled heldForSeconds=600 blocker=receipt-missing" },
        { "goal-stalled", "GOAL_STALLED repeatedForSeconds=120 owner=none blocker=cohort-tick" },
        { "train-receipt-released", "released reason=none" },
        { "owner-hold-cleared", "" },
        { "owner-question-resolved", "question=Retry?" },
        { "state-log-divergence", "STATE_LOG_DIVERGENCE lost=1 repeated=0" },
        { "loop-start", "LOOP_START selfCheck=false" },
        { "loop-stop", "LOOP_STOP reason=stop-file" },
        { "goal-lifecycle", "TaskDispatched role=Developer" },
        { "goal-lifecycle", "TaskCompleted role=Tester" },
        { "goal-lifecycle", "TaskFailed role=Reviewer finding=cohort-tick" },
        { "goal-lifecycle", "HumanInputReceived" },
        { "goal-lifecycle", "HumanInputSuperseded" },
        { "admission", "ADMISSION result=held reason=acceptance-engine-circuit" },
        { "infrastructure-deferral", "ACCEPTANCE_INFRASTRUCTURE_DEFERRED reason=baseline-unavailable" }
    };

    [Theory]
    [MemberData(nameof(VocabularyCases))]
    public void EveryMappedKindKeepsInternalTermsOutOfSentencesAndExplanations(string kind, string detail)
    {
        var input = new OwnerConductEvent(DateTimeOffset.UnixEpoch, kind, "11111111", detail);
        Assert.True(OwnerActivityNarrator.Maps(input));
        var lines = OwnerActivityNarrator.Narrate([input], id => id == "22222222" ? "Export" : "Search");
        foreach (var line in lines)
            foreach (var forbidden in new[] { "none", "child result", "handoff", "cohort", "receipt", "canary", "tick", "eventKind" })
                Assert.DoesNotContain(forbidden, OwnerActivityNarrator.Explain(line), StringComparison.OrdinalIgnoreCase);
        if (detail.Contains("ACTIVATION_ADOPTED") || kind == "canary-gate" && detail.Contains("result=passed")) Assert.Empty(lines);
    }

    [Theory]
    [InlineData("cohort-tick", "joint run-step")]
    [InlineData("receipt-missing", "waiting for verification to finish")]
    [InlineData("The cohort receipt is missing for Search.\nDiagnostic details", "The cohort receipt is missing for Search.")]
    public void WaitingOnTranslatesCategoryTokensAndPreservesRecordedReasonSentences(string blocker, string expected)
    {
        Assert.Equal(expected, OwnerActivityNarrator.WaitingOn(blocker));
    }

    [Fact]
    public void ARealRestartDoesNotAddStopOrStartLines()
    {
        var time = DateTimeOffset.UnixEpoch;
        Assert.Empty(OwnerActivityNarrator.Narrate([
            new(time, "loop-stop", null, "LOOP_STOP reason=self-relaunch-handoff"),
            new(time.AddSeconds(1), "loop-start", null, "LOOP_START selfCheck=true"),
            new(time.AddSeconds(2), "loop-handoff", null, "ACTIVATION_ADOPTED reason=none")], _ => ""));
    }

    [Fact]
    public void ScheduledRestartIsSilentAndFailedTransferNeverClaimsTheOldLoopIsRunning()
    {
        var time = DateTimeOffset.UnixEpoch;
        Assert.Empty(OwnerActivityNarrator.Narrate([
            new(time, "loop-stop", null, "LOOP_STOP reason=max-duration"),
            new(time.AddSeconds(1), "loop-start", null, "LOOP_START"),
            new(time.AddSeconds(2), "loop-handoff", null, "ACTIVATION_ADOPTED reason=none")], _ => ""));
        var failed = OwnerActivityNarrator.Narrate([
            new(time, "loop-handoff", null, "LOOP_HANDOFF_FAILED continuing=false reason=publish_failed")], _ => "");
        Assert.Equal("Conductor stopped", Assert.Single(failed).Phrase);
        Assert.DoesNotContain("previous version continues", failed[0].Next);
    }

    [Fact]
    public void WorkerExecutionFailureAndBlockingFindingHaveDifferentReasonsAndNextSteps()
    {
        var time = DateTimeOffset.UnixEpoch;
        var lines = OwnerActivityNarrator.Narrate([
            new(time, "goal-lifecycle", "11111111", "TaskFailed role=Developer"),
            new(time.AddSeconds(1), "goal-lifecycle", "22222222", "TaskFailed role=Reviewer outcome=finding")], _ => "Search");
        var crash = Assert.Single(lines, line => line.GoalPrefix == "11111111");
        var finding = Assert.Single(lines, line => line.GoalPrefix == "22222222");
        Assert.Contains("the worker could not finish", crash.Phrase);
        Assert.Contains("retry the worker", crash.Next);
        Assert.Contains("a problem needs correction", finding.Phrase);
        Assert.Contains("Developer will address", finding.Next);
    }

    [Fact]
    public void TitlesAndQuestionsContainingImplementationTermsKeepTheirSubjectInWords()
    {
        var time = DateTimeOffset.UnixEpoch;
        var lines = OwnerActivityNarrator.Narrate([
            new(time, "loop-relaunch", "11111111", "LOOP_RELAUNCH_SCHEDULED"),
            new(time.AddSeconds(1), "goal-escalation", "11111111", "steward-owner-question question=Keep the sticky ticket handoff?" )],
            _ => "The train receipt hold across ticks", attention: [new(new("q1", "11111111", OwnerQuestionKind.HumanInput,
                "Keep the sticky ticket handoff?"), time.AddSeconds(1))]);
        Assert.Contains(lines, line => line.Phrase == "Landed: The train proof hold across steps (11111111)");
        Assert.Contains(lines, line => line.Phrase == "Needs you: 11111111 Keep the persistent issue switch?");
    }

    [Fact]
    public void InteractionFailureDoesNotClaimAnUnrelatedFlakyTest()
    {
        var lines = OwnerActivityNarrator.Narrate([
            new(DateTimeOffset.UnixEpoch, "acceptance-cohort", null,
                "ACCEPTANCE_COHORT members=11111111,22222222 outcome=interaction-only attribution=InteractionOnly partitions=11111111:Passed,22222222:Passed")], _ => "Search");
        Assert.Equal("Joint test run for Search, Search: failed (the changes failed when tested together); awaiting the conductor's next step", Assert.Single(lines).Phrase);
    }

    [Fact]
    public void LifecycleAnswerAndObservedQuestionRemovalProduceOneResolution()
    {
        var time = DateTimeOffset.UnixEpoch;
        var lines = OwnerActivityNarrator.Narrate([
            new(time, "goal-escalation", "11111111", "steward-owner-question question=Keep the ticket direction?"),
            new(time.AddSeconds(1), "goal-lifecycle", "11111111", "HumanInputReceived"),
            new(time.AddSeconds(2), "owner-question-resolved", "11111111", "question=Keep the ticket direction?")], _ => "Search",
            attention: [new(new("q1", "11111111", OwnerQuestionKind.HumanInput, "Keep the ticket direction?"), time, time.AddSeconds(1))]);
        Assert.Single(lines, line => line.Phrase == "Resolved: 11111111 Search: Keep the issue direction?");
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void EvidenceLookupReadsRecordedNamesWithoutCreatingOrMigratingTheStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "owner-evidence-" + Guid.NewGuid().ToString("N") + ".db");
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "acceptance-cohort", null, "receipt=R1");
        try
        {
            var lookup = new OwnerActivityEvidenceReader(path);
            Assert.Null(lookup.Read(item));
            Assert.False(File.Exists(path));
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE cohort_receipts (receipt_id TEXT, failed_checks_json TEXT,
                        gate_test_result_paths_json TEXT, attributed_members_json TEXT);
                    INSERT INTO cohort_receipts VALUES ('R1', '["Build main"]', '[]',
                        '[{"GoalId":{"Value":"11111111"},"MemberOrdinal":0,"CandidateRevision":"C1","ReproducedFailingTests":["NewTests.Behavior"]}]');
                    """;
                command.ExecuteNonQuery();
            }
            var before = File.ReadAllBytes(path);
            var evidence = lookup.Read(item);
            Assert.NotNull(evidence);
            Assert.True(evidence.OwnTest);
            Assert.Equal("NewTests.Behavior", Assert.Single(evidence.Tests));
            Assert.Equal("Build main", Assert.Single(evidence.Checks));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TrainGroupingUsesLoopNumberAndDoesNotMergeAnotherStart()
    {
        var time = DateTimeOffset.UnixEpoch;
        var lines = OwnerActivityNarrator.Narrate([
            new(time, "loop-relaunch", "11111111", "LOOP_RELAUNCH_SCHEDULED tick=7"),
            new(time.AddMilliseconds(20), "loop-relaunch", "22222222", "LOOP_RELAUNCH_SCHEDULED tick=7"),
            new(time.AddSeconds(1), "loop-start", null, "LOOP_START"),
            new(time.AddSeconds(2), "loop-relaunch", "33333333", "LOOP_RELAUNCH_SCHEDULED tick=7")], id => id ?? "");
        Assert.Single(lines, line => line.Phrase.StartsWith("Landed together:"));
        Assert.Single(lines, line => line.Phrase.StartsWith("Landed:"));
    }

    [Theory]
    [InlineData("held", "retrying automatically")]
    [InlineData("escalated", "awaiting the conductor's next step")]
    public void JointFailureUsesTheObservedNextStep(string follow, string expected)
    {
        var time = DateTimeOffset.UnixEpoch;
        var lines = OwnerActivityNarrator.Narrate([
            new(time, "acceptance-cohort", null, "ACCEPTANCE_COHORT members=11111111,22222222 outcome=failed partitions=11111111:Passed,22222222:Passed"),
            new(time.AddSeconds(1), "acceptance-cohort", "11111111", "result=" + follow)], _ => "Search");
        Assert.EndsWith("; " + expected, Assert.Single(lines).Phrase);
    }

    [Fact]
    public void RecordedCandidateTestAndCheckAreUsedForFailureAndMainExplanation()
    {
        var time = DateTimeOffset.UnixEpoch;
        var lines = OwnerActivityNarrator.Narrate([
            new(time, "acceptance", "11111111", "ACCEPTANCE goal=11111111 slot=slot-0 result=failed attempt=A1 tick=4"),
            new(time.AddMilliseconds(1), "goal-lifecycle", "11111111", "TaskDispatched role=Developer"),
            new(time.AddSeconds(1), "loop-relaunch", "11111111", "LOOP_RELAUNCH_NOT_REQUIRED"),
            new(time.AddSeconds(2), "canary-gate", null, "CANARY_GATE result=failed")], _ => "Search",
            _ => new(["NewTests.Behavior"], ["Build main"], true));
        Assert.Contains(lines, line => line.Phrase == "Search: failed its tests (its own new test NewTests.Behavior failed); sent back to the Developer");
        Assert.Contains(lines, line => line.Phrase == "Main broke after landing Search: Build main");
    }

    [Fact]
    public void LandedTodayUsesActualLandingEventsAndTheClocksLocalDate()
    {
        var clock = new ZoneClock();
        var now = clock.GetUtcNow();
        var landing = new OwnerConductEvent(now, "loop-relaunch", "11111111", "LOOP_RELAUNCH_SCHEDULED");
        Assert.Equal(1, OwnerActivityNarrator.LandedToday([
            landing, landing,
            landing with { Timestamp = now.AddHours(-3), GoalId = "22222222" },
            new(now, "acceptance", "33333333", "result=passed")], clock));
    }

    [Theory]
    [InlineData("goal-lifecycle", "TaskDispatched role=Developer", "sent back to the Developer")]
    [InlineData("acceptance", "ACCEPTANCE goal=11111111 slot=slot-0 result=started attempt=A2 tick=5", "retrying automatically")]
    [InlineData("admission", "ADMISSION tick=5 result=held reason=acceptance-engine-circuit goal=11111111", "retrying automatically")]
    [InlineData("goal-escalation", "steward-owner-question question=How should we repair this?", "needs you")]
    public void RealAcceptanceFailureUsesTheGoalsFollowingDecision(string kind, string detail, string next)
    {
        var time = DateTimeOffset.UnixEpoch;
        var lines = OwnerActivityNarrator.Narrate([
            new(time, "acceptance", "11111111", "ACCEPTANCE goal=11111111 slot=slot-0 result=failed attempt=A1 tick=4"),
            new(time.AddSeconds(1), kind, "11111111-full", detail)], _ => "Search", attention: kind == "goal-escalation" ?
                [new(new("q1", "11111111-full", OwnerQuestionKind.StewardHold, "How should we repair this?"), time.AddSeconds(1))] : []);
        var failure = Assert.Single(lines, line => line.Kind == "acceptance" && line.Phrase.Contains("failed"));
        Assert.Equal("Search: failed its tests (the failure reason has not been recorded); " + next, failure.Phrase);
        Assert.DoesNotContain("machine", failure.Why);
        Assert.Equal(next + ".", failure.Next);
        Assert.Equal(kind == "goal-escalation", failure.Act.Contains("DECISIONS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("checks=Build_main", "the check Build main failed")]
    [InlineData("stage=rebase checks=rebase", "the changes could not be updated to main")]
    [InlineData("stage=merge checks=merge", "the changes could not be added to main")]
    [InlineData("stage=source-size-preflight checks=source-size", "a source file exceeded its size limit")]
    [InlineData("type=timeout checks=Build_main", "the test run timed out")]
    public void RealAcceptanceFailureUsesRecordedCheckStageAndType(string fields, string reason)
    {
        var time = DateTimeOffset.UnixEpoch;
        var lines = OwnerActivityNarrator.Narrate([
            new(time, "acceptance", "11111111", "ACCEPTANCE goal=11111111 result=failed " + fields),
            new(time.AddSeconds(1), "goal-lifecycle", "11111111", "TaskDispatched role=Developer")], _ => "Search");
        Assert.Equal("Search: failed its tests (" + reason + "); sent back to the Developer",
            Assert.Single(lines, line => line.Kind == "acceptance").Phrase);
    }

    [Fact]
    public void FailureDoesNotBorrowAnotherGoalsOrALaterRunsNextStep()
    {
        var time = DateTimeOffset.UnixEpoch;
        var lines = OwnerActivityNarrator.Narrate([
            new(time, "acceptance", "11111111", "ACCEPTANCE result=failed attempt=A1 tick=4"),
            new(time.AddSeconds(1), "goal-lifecycle", "22222222", "TaskDispatched role=Developer"),
            new(time.AddSeconds(2), "acceptance", "11111111", "ACCEPTANCE result=passed attempt=A2 tick=5"),
            new(time.AddSeconds(3), "goal-escalation", "11111111", "steward-owner-question question=Approve?")], _ => "Search");
        var failure = Assert.Single(lines, line => line.Kind == "acceptance" && line.Phrase.Contains("failed"));
        Assert.EndsWith("; awaiting the conductor's next step", failure.Phrase);
        Assert.DoesNotContain("DECISIONS", failure.Act);
    }

    [Fact]
    public void AttributedJointFailureWithoutTestNamesDoesNotClaimAMachineFault()
    {
        var lines = OwnerActivityNarrator.Narrate([
            new(DateTimeOffset.UnixEpoch, "acceptance-cohort", null,
                "ACCEPTANCE_COHORT members=11111111,22222222 outcome=failed attribution=FirstMemberFailed")], _ => "Search");
        Assert.Equal("Joint test run for Search, Search: failed (its own checks failed); sent back to the Developer",
            Assert.Single(lines).Phrase);
    }

    private sealed class ZoneClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
