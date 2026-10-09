using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: per-test logs and headless application instances; no time-zone globals.
public sealed class OwnerConsoleActivityPresentationTests
{
    // Literal conductor reasons: FailedGoalRecoveryPolicy, LandingRebasePolicy, GoalRefinementGate.
    private const string RetryDetail = "GOAL goal=11111111 result=escalated state=Failed reason=" +
        "Task_aaaaaaaa_exhausted_bounded_real-failure_retries_(2/2);_failed_command:_codex_exec_--json_--skip-git-repo-check_--sandbox_workspace-write_--cd_C:\\wt;_failure_evidence:_exit_code_1";
    private const string RebaseDetail = "GOAL goal=11111111 result=escalated state=Verified reason=" +
        "pre-landing_rebase_conflict_(src/Example.cs);_use_'workspace_rebase'_to_resolve decision=landing-rebase/2/rebase-conflict";
    private const string ClarificationDetail = "GOAL goal=11111111 result=escalated state=Failed reason=" +
        "Resolve_spec_clarification_before_dispatching_planner_work:_1_pending_for_goal_11111111._Run_`attention_show_11111111`_to_read_and_answer_them.";

    [Theory]
    [InlineData(RetryDetail, "the worker failed and used up its retries (2 of 2).")]
    [InlineData(RebaseDetail, "conflicts with main in src/Example.cs; needs a rebase.")]
    public void ConductorEscalationReasonsUsePlainWordsAndKeepRawDetail(string detail, string reason)
    {
        var activity = Assert.Single(OwnerActivityNarrator.Narrate(
            [new(DateTimeOffset.UnixEpoch, "goal-escalation", "11111111", detail)], _ => "Search"));
        Assert.Equal("11111111 escalated: " + reason, activity.Phrase);
        Assert.Equal(reason, activity.Why);
        Assert.Equal(detail, activity.Detail);
        Assert.DoesNotContain("_", activity.Phrase + activity.Why);
        Assert.DoesNotContain("codex exec", activity.Phrase + activity.Why);
        Assert.DoesNotContain("workspace rebase", activity.Phrase + activity.Why);
    }

    [Fact]
    public void RetryNamesOnlyTheEarlierWorkerOnTheSameGoalAndTask()
    {
        var time = DateTimeOffset.UnixEpoch;
        var items = OwnerActivityNarrator.Narrate([
            new(time, "goal-lifecycle", "11111111", "TaskFailed task=aaaaaaaaffffffff role=Developer"),
            new(time.AddSeconds(1), "goal-lifecycle", "22222222", "TaskFailed task=aaaaaaaa role=Reviewer"),
            new(time.AddSeconds(2), "goal-lifecycle", "11111111", "TaskFailed task=bbbbbbbb role=Planner"),
            new(time.AddSeconds(3), "goal-escalation", "11111111", RetryDetail),
            new(time.AddSeconds(4), "goal-lifecycle", "11111111", "TaskFailed task=aaaaaaaa role=Tester")], _ => "Search");
        Assert.Equal("Developer failed and used up its retries (2 of 2).",
            Assert.Single(items, item => item.Kind == "goal-escalation").Why);
    }

    [Theory]
    [InlineData("pre-merge rebase conflict (src/ConductorBatchLoop.TickPersistence.cs, src/my_file.cs); use 'workspace rebase' to resolve",
        "conflicts with main in src/ConductorBatchLoop.TickPersistence.cs, src/my_file.cs; needs a rebase.")]
    [InlineData("PRE-LANDING_REBASE_CONFLICT_WITH_release_(src/Example.cs);_use_'workspace_rebase'_to_resolve",
        "conflicts with release in src/Example.cs; needs a rebase.")]
    [InlineData("Task_aaaaaaaa_exhausted_bounded_real-failure_retries;_failed_command:_codex_exec",
        "the worker failed and used up its retries.")]
    public void KnownReasonsPreserveSuppliedFacts(string reason, string expected)
    {
        var activity = Assert.Single(OwnerActivityNarrator.Narrate(
            [new(default, "goal-escalation", "11111111", "reason=" + reason)], _ => "Search"));
        Assert.Equal(expected, activity.Why);
    }

    [Theory]
    [InlineData("codex_exec --json")]
    [InlineData("codex__exec --json")]
    [InlineData("workspace_rebase 11111111")]
    [InlineData("attention_show 11111111")]
    [InlineData("mcg-orchestrator.cmd status")]
    [InlineData("mcg status")]
    [InlineData("`secret command`")]
    [InlineData(" $ secret command")]
    [InlineData("command: secret command")]
    [InlineData("use 'workspace_rebase' to resolve")]
    public void UnknownReasonsCutTheFirstCommandMarkerAfterVocabularyTranslation(string command)
    {
        var detail = "reason=receipt_waiting_for_capacity; " + command + "\nraw=full raw second line";
        var activity = Assert.Single(OwnerActivityNarrator.Narrate(
            [new(default, "goal-escalation", "11111111", detail)], _ => "Search"));
        Assert.Equal("proof waiting for capacity.", activity.Why);
        Assert.Equal("11111111 escalated: proof waiting for capacity.", activity.Phrase);
        Assert.Equal(detail, activity.Detail);
    }

    [Theory]
    [InlineData(ClarificationDetail, "Resolve spec clarification before dispatching planner work: 1 pending")]
    [InlineData("reason=Resolve_the_spec_clarification_item_before_dispatching_planner_work.",
        "Resolve the spec clarification item before dispatching planner work")]
    public void SpecClarificationIsARoutineAuthorQuestion(string detail, string question)
    {
        var activity = Assert.Single(OwnerActivityNarrator.Narrate(
            [new(default, "goal-escalation", "11111111", detail)], _ => "Search"));
        Assert.Equal("11111111 question for the Author: " + question + ".", activity.Phrase);
        Assert.Equal("question for the Author: " + question + ".", activity.Why);
        Assert.Equal(detail, activity.Detail);
        Assert.DoesNotContain("_", activity.Phrase + activity.Why);
        Assert.DoesNotContain("attention show", activity.Phrase + activity.Why);
        Assert.DoesNotContain("escalated", activity.Phrase);
        Assert.Null(activity.Resolution);
    }

    [Theory]
    [InlineData("GoalPolicyDecision resolution-verb=answer resolution-actor=author", "11111111", true)]
    [InlineData("GoalPolicyDecision resolution-verb=answer resolution-actor=operator", "11111111", false)]
    [InlineData("GoalPolicyDecision resolution-verb=answer resolution-actor=author", "22222222", false)]
    [InlineData("TaskDispatched role=Planner", "11111111", false)]
    public void SpecClarificationAnswerRequiresRecordedAuthorEvidence(string detail, string goal, bool answered)
    {
        var time = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var items = OwnerActivityNarrator.Narrate([
            new(time, "goal-escalation", "11111111", ClarificationDetail),
            new(time.AddSeconds(5), "goal-lifecycle", goal, detail)], _ => "Search");
        var activity = Assert.Single(items, item => item.Kind == "goal-escalation");
        if (answered)
        {
            Assert.Contains($"answered by the Author at {time.AddSeconds(5).ToLocalTime():HH:mm:ss}", activity.Phrase);
            Assert.Equal(new(time.AddSeconds(5), Actor: "author"), activity.Resolution);
        }
        else
        {
            Assert.DoesNotContain("answered by", activity.Phrase);
            if (detail.StartsWith("TaskDispatched", StringComparison.Ordinal))
                Assert.True(activity.Resolution?.AutomaticRetry);
            else Assert.Null(activity.Resolution);
        }
    }

    [Theory]
    [InlineData("Stale_spec_clarification_detected_before_dispatching_planner_work._Run_`attention_show_11111111`")]
    [InlineData("Resolve_spec_clarification_in_a_different_stage;_command:_secret")]
    public void OtherClarificationReasonsRemainEscalations(string reason)
    {
        var activity = Assert.Single(OwnerActivityNarrator.Narrate(
            [new(default, "goal-escalation", "11111111", "reason=" + reason)], _ => "Search"));
        Assert.StartsWith("11111111 escalated: ", activity.Phrase);
        Assert.DoesNotContain("question for the Author", activity.Phrase);
        Assert.DoesNotContain("_", activity.Phrase);
        Assert.DoesNotContain("attention show", activity.Phrase);
    }

    [Fact]
    public async Task HeadlessActivityUsesLocalTimeWordsAndTitlesAndOmitsDiagnostics()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-goal", "# Improve the owner console\nImplementation details", Mcg.AgentOrchestrator.Core.AgentRole.Developer);
        var instant = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        // A non-local source offset also catches missing conversion on machines configured for UTC.
        var offset = TimeZoneInfo.Local.GetUtcOffset(instant) == TimeSpan.Zero ? TimeSpan.FromHours(9) : TimeSpan.Zero;
        var escalation = new OwnerConductEvent(instant.ToOffset(offset), "goal-escalation", "11111111-goal",
            "ownerless-hold-stalled state=blocked heldForSeconds=30 blocker=owner review required");
        var passed = escalation with { Timestamp = escalation.Timestamp.AddSeconds(1), EventKind = "acceptance",
            Detail = "result=passed commit=internal-sha" };
        var diagnostic = escalation with { Timestamp = escalation.Timestamp.AddSeconds(2), EventKind = "state-log-divergence",
            Detail = "STATE_LOG_DIVERGENCE lost=0 repeated=0 stored_only=0 by_design=7" };
        var path = Path.Combine(Path.GetTempPath(), "owner-human-activity-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllLines(path, new[] { escalation, passed, diagnostic }.Select(LogLine));
            var builder = Builder(harness);
            var dialogs = new Dialogs();
            var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, dialogs,
                harness.State, new Tail(escalation.Detail), harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
            using IApplication app = Terminal.Gui.App.Application.Create();
            using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
            var (model, recent) = await OwnerConsoleFullScreenHost.BuildInitialViewModelAsync(builder, path,
                harness.Clock.GetUtcNow(), null, TestContext.Current.CancellationToken);
            view.Render(model);

            Assert.Equal(new[]
            {
                $"{passed.Timestamp.ToLocalTime():HH:mm:ss} 11111111 Improve the owner console: passed its tests, landing next",
                $"{escalation.Timestamp.ToLocalTime():HH:mm:ss} 11111111 Waiting: Improve the owner console has been held 0 min: owner review required"
            }, view.ActivityLines);
            Assert.Equal(2, recent.Count);
            Assert.DoesNotContain(view.ActivityLines, line => line.Contains("STATE_LOG_DIVERGENCE", StringComparison.Ordinal));
            Assert.DoesNotContain(view.ActivityLines, line => line.Contains("internal-", StringComparison.Ordinal));
            Assert.Equal(escalation.Detail, model.Activity[1].Detail); // Original diagnostics remain intact.

            // Live append and direct model inputs obey the same visibility rule as the startup scan.
            OwnerConsoleStartupActivity.Append(recent, diagnostic);
            Assert.Equal(2, recent.Count);
            view.Render(await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, [escalation, diagnostic, passed], 0)));
            Assert.Equal(2, view.ActivityLines.Count);
            await view.HandleKeyAsync(Key.Tab);
            await view.HandleKeyAsync(Key.Enter);
            Assert.DoesNotContain(escalation.Detail, Assert.Single(dialogs.Texts));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("STATE_LOG_DIVERGENCE lost=0 repeated=0 stored_only=0 by_design=9", false)]
    [InlineData("STATE_LOG_DIVERGENCE lost=0 repeated=0 stored_only=3 by_design=9", false)]
    [InlineData("STATE_LOG_DIVERGENCE lost=1 repeated=0 stored_only=0 by_design=9", true)]
    [InlineData("STATE_LOG_DIVERGENCE lost=0 repeated=2 stored_only=0 by_design=9", true)]
    [InlineData("STATE_LOG_DIVERGENCE lost=unknown repeated=0 by_design=9", false)]
    [InlineData("STATE_LOG_DIVERGENCE lost=0 lost=1 repeated=0 by_design=9", false)]
    public void DivergenceRequiresPositiveFaultEvidence(string detail, bool visible)
    {
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "state-log-divergence", "11111111", detail);
        Assert.Equal(visible, OwnerConsoleStartupActivity.IsOperatorEvent(item));
    }

    [Theory]
    [InlineData("author", "kind=ask-owner item=internal-id", "unknown- question sent to the operator: the conductor reported a hold on unknown-")]
    [InlineData("acceptance", "result=failed code=1", "unknown-: failed its tests (the failure reason has not been recorded); awaiting the conductor's next step")]
    [InlineData("acceptance", "result=blocked reason=missing_evidence", "unknown-: failed its tests (the failure reason has not been recorded); awaiting the conductor's next step")]
    [InlineData("goal-escalation", "ownerless-hold-stalled state=blocked heldForSeconds=30 blocker=waiting for owner approval", "Waiting: unknown- has been held 0 min: waiting for owner approval")]
    public async Task StructuredEventsRenderPlainPhrasesWithoutRawFields(string kind, string detail, string phrase)
    {
        var harness = new OwnerConsoleHarness();
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, kind, "unknown-goal", detail);
        var model = await Builder(harness).BuildAsync(new(harness.Clock.GetUtcNow(), null, [item], 0));
        var activity = Assert.Single(model.Activity);
        Assert.Equal(phrase, activity.Phrase);
        Assert.Equal("unknown-", activity.GoalTitle);
        Assert.Equal(detail, activity.Detail);
        Assert.Equal($"{item.Timestamp.ToLocalTime():HH:mm:ss} " + (phrase.Contains("unknown-") ? "" : "unknown- ") + phrase,
            OwnerActivityNarrator.Line(activity));
    }

    // Matches FormatOwnerReviewEscalation, including its multiline command payload.
    private const string OwnerReviewDetail = "owner-review-hold goal=11111111 candidate=0123456789012345678901234567890123456789\n" +
        "checks=owner approval\nchanged-protected-fields=conductor policy\nfingerprint=internal-fingerprint\n" +
        "approve: .\\mcg-orchestrator.cmd approve-policy-change 11111111 0123456789012345678901234567890123456789 --text-file <reason-file>\n" +
        "decline: cancel-goal 11111111 or abandon-goal 11111111";

    [Theory]
    [InlineData(OwnerReviewDetail, "11111111 escalated: approval of the completed work")]
    [InlineData("steward-owner-question case=C trigger=internal-trigger question=Should we retry this task? evidence=[internal-evidence]", "11111111 escalated: Should we retry this task?")]
    [InlineData("author-owner-question item=Goal:internal-goal reason=choose recovery question=Should we retry? recommendation=Retry with evidence", "11111111 question sent to the operator: Should we retry?")]
    [InlineData("unrecognized-escalation raw payload with commands and evidence", "11111111 escalated: the conductor reported a hold on Improve the owner console")]
    public async Task EscalationPayloadsStayOutOfGoalDetail(string detail, string phrase)
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-goal", "# Improve the owner console", Mcg.AgentOrchestrator.Core.AgentRole.Developer);
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "goal-escalation", "11111111-goal", detail);
        var dialogs = new Dialogs();
        var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, dialogs,
            harness.State, new Tail(detail), harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        var model = await Builder(harness).BuildAsync(new(harness.Clock.GetUtcNow(), null, [item], 0));
        view.Render(model);

        Assert.Equal($"{item.Timestamp.ToLocalTime():HH:mm:ss} {phrase}", Assert.Single(view.ActivityLines));
        Assert.Equal(detail, Assert.Single(model.Activity).Detail);
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Enter);
        Assert.DoesNotContain(detail, Assert.Single(dialogs.Texts));
    }

    [Theory]
    [InlineData("goal-escalation", OwnerReviewDetail, "escalated: approval of the completed work")]
    [InlineData("acceptance", "result=passed commit=internal-sha", "passed its tests, landing next")]
    public async Task LongGoalTitlesLeaveTheActivityPhraseVisible(string kind, string detail, string phrase)
    {
        var harness = new OwnerConsoleHarness();
        var title = "Improve the owner console " + new string('x', 100);
        harness.AddGoal("11111111-goal", "# " + title, Mcg.AgentOrchestrator.Core.AgentRole.Developer);
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, kind, "11111111-goal", detail);
        var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, new Dialogs(),
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Builder(harness).BuildAsync(new(harness.Clock.GetUtcNow(), null, [item], 0)));
        view.Fit(118, 40);

        var line = Assert.Single(view.ActivityLines);
        Assert.Contains(phrase, line[..Math.Min(118, line.Length)]);
        if (kind == "acceptance") Assert.Contains("…", line);
    }

    [Fact]
    public async Task ActivityScrollKeepsTheViewedLineWhenNewEventsArrive()
    {
        var harness = new OwnerConsoleHarness();
        var first = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "acceptance", "11111111", "result=passed");
        var second = first with { Timestamp = first.Timestamp.AddSeconds(1) };
        var builder = Builder(harness);
        var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, new Dialogs(),
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, [first, second], 0)));
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.CursorDown);
        var viewed = view.ActivityLines[1];

        var third = first with { Timestamp = first.Timestamp.AddSeconds(2) };
        view.Render(await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, [first, second, third], 0)));

        Assert.Equal(2, view.ActivityPane.SelectedItem);
        Assert.Equal(viewed, view.ActivityLines[view.ActivityPane.SelectedItem!.Value]);
    }

    private static OwnerConsoleViewModelBuilder Builder(OwnerConsoleHarness harness) =>
        new(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock);

    private static string LogLine(OwnerConductEvent item) => JsonSerializer.Serialize(new
    { timestamp = item.Timestamp, eventKind = item.EventKind, goalId = item.GoalId, detail = item.Detail });

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class Tail(string detail) : IGoalEventTail
    {
        public IReadOnlyList<string> ReadLast(string goalId, int count) => [detail];
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Activity must not prompt.");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Activity must not prompt.");
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
    }
}
