using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorBoardFillFilingTests
{
    [Fact]
    public async Task Held_intake_does_not_block_ticks_and_only_one_filing_is_in_flight()
    {
        using var h = new BoardFillFilingTestFixture();
        h.Assessed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Intake.Filing = _ =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            return new("filed", "aaaaaaaa1111", "held stdout", "", 0);
        };
        try
        {
            h.Host.ServiceTick(h.Drafts.Kernel);
            await PanelTestHarness.Signal(entered.Task, "intake entered its gate");
            var filing = h.Host.CurrentFiling!;
            h.Host.ServiceTick(h.Drafts.Kernel);
            Assert.Same(filing, h.Host.CurrentFiling);
            Assert.False(filing.IsCompleted); // The unreleased gate, not elapsed time, determines this state.
            Assert.Single(h.Intake.Requests);
            Assert.Empty(h.FilingEvents());
            release.SetResult();
            await PanelTestHarness.Signal(filing, "released filing completed");
            h.Host.ServiceTick(h.Drafts.Kernel);
            Assert.Null(h.Host.CurrentFiling);
            Assert.Single(h.FilingEvents());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public void Daily_cap_counts_only_new_filings_on_the_injected_utc_date()
    {
        using var h = new BoardFillFilingTestFixture();
        var first = h.Assessed();
        Assert.Equal("filed", h.Host.AttemptFiling(first.Id).Result);
        h.Drafts.Item = h.Drafts.Item with { Id = Guid.NewGuid().ToString("N") };
        var second = h.Assessed();
        Assert.Equal("daily-cap-reached", h.Host.AttemptFiling(second.Id).Reason);
        Assert.Single(h.Intake.Requests);
        // An offset on the same UTC date does not replenish the cap.
        h.Drafts.Clock.UtcNow = h.Drafts.Clock.UtcNow.ToOffset(TimeSpan.FromHours(-6));
        Assert.Equal(1, h.Drafts.Store.FiledOnUtcDay(h.Drafts.Clock.UtcNow));
        h.Drafts.Clock.UtcNow = h.Drafts.Clock.UtcNow.AddDays(1);
        Assert.Equal(0, h.Drafts.Store.FiledOnUtcDay(h.Drafts.Clock.UtcNow));
        Assert.Equal("filed", h.Host.AttemptFiling(second.Id).Result);
        Assert.Equal(2, h.Intake.Requests.Count);
        Assert.Equal(1, h.Drafts.Store.FiledOnUtcDay(h.Drafts.Clock.UtcNow));
    }

    [Fact]
    public void File_uses_exact_keyed_intake_and_second_attempt_replays_receipt()
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed();
        var first = h.Host.AttemptFiling(draft.Id);
        // New-goal admission limits do not block reading the already-completed receipt.
        h.Board = new(100, new HashSet<string> { draft.BacklogItemId });
        var replay = h.Host.AttemptFiling(draft.Id);
        var request = Assert.Single(h.Intake.Requests);
        Assert.Equal(new[] { "goal", "--brief-file", h.Drafts.DraftPath, "--backlog-item", draft.BacklogItemId,
            "--backlog-coverage", "full", "--request-key", "board-fill-" + draft.Id }, request.Arguments);
        Assert.Equal("filed", first.Result);
        Assert.Equal("replayed", replay.Result);
        Assert.Equal(first.GoalId, replay.GoalId);
        Assert.Single(h.Intake.Goals);
        Assert.Equal(1, h.Drafts.Store.FiledOnUtcDay(h.Drafts.Clock.UtcNow));
        Assert.Equal("intake stdout", first.Stdout);
        Assert.Equal("intake stderr", first.Stderr);
        Assert.Equal(2, Assert.Single(h.Drafts.Store.ReadAll()).Filings!.Count);
    }

    [Theory]
    [InlineData(ConductorBoardFillMode.Shadow)]
    [InlineData(ConductorBoardFillMode.Off)]
    public void Shadow_and_off_do_not_attempt_filing(ConductorBoardFillMode mode)
    {
        using var h = new BoardFillFilingTestFixture();
        h.Assessed();
        h.Policy = h.Policy with { BoardFillMode = mode };
        h.Host.ServiceTick(h.Drafts.Kernel);
        Assert.Null(h.Host.CurrentFiling);
        Assert.Empty(h.Intake.Requests);
        Assert.Null(Assert.Single(h.Drafts.Store.ReadAll()).Filings);
        Assert.Empty(h.FilingEvents());
    }

    [Theory]
    [InlineData("mode", "mode-changed")]
    [InlineData("target", "target-reached")]
    [InlineData("cap", "daily-cap-reached")]
    [InlineData("fileability", "not-fileable:changed")]
    [InlineData("head", "stale-head")]
    [InlineData("linked", "backlog-linked")]
    [InlineData("owner", "not-fileable:owner-gated")]
    [InlineData("missing-head", "main-head-unavailable")]
    [InlineData("changed-backlog", "not-fileable:backlog-changed")]
    [InlineData("missing-draft", "not-fileable:draft-missing")]
    public void Immediate_recheck_refuses_without_intake(string fault, string reason)
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed(fileable: fault != "fileability");
        switch (fault)
        {
            case "mode": h.BeforeHead = () => h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow }; break;
            case "target": h.Board = new(h.Policy.BoardFillTargetActiveGoals, new HashSet<string>()); break;
            case "cap": h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 0 }; break;
            case "head": h.Head = "moved"; break;
            case "linked": h.Board = new(0, new HashSet<string> { draft.BacklogItemId }); break;
            case "owner": h.Drafts.Item = h.Drafts.Item with { Tags = "owner-gated" }; break;
            case "missing-head": h.Head = null; break;
            case "changed-backlog": h.Drafts.Item = h.Drafts.Item with { UpdatedAt = h.Drafts.Item.UpdatedAt.AddMinutes(1) }; break;
            case "missing-draft": File.Delete(h.Drafts.DraftPath); break;
        }
        var result = h.Host.AttemptFiling(draft.Id);
        Assert.Equal("refused", result.Result);
        Assert.Equal(reason, result.Reason);
        Assert.Empty(h.Intake.Requests);
        Assert.Equal(fault == "head", Assert.Single(h.Drafts.Store.ReadAll()).StaleHead);
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        h.Host.ServiceTick(h.Drafts.Kernel);
        var json = Assert.Single(h.FilingEvents());
        using var receipt = JsonDocument.Parse(json);
        Assert.Equal(BoardFillFiledEvent.Format(draft, result), receipt.RootElement.GetProperty("detail").GetString());
        Assert.Equal("decision", receipt.RootElement.GetProperty("operator").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Owner_gated_or_nonfileable_candidates_never_start_an_attempt(bool ownerGated)
    {
        using var h = new BoardFillFilingTestFixture();
        h.Assessed(fileable: ownerGated);
        if (ownerGated) h.Drafts.Item = h.Drafts.Item with { Body = "Requires owner approval" };
        h.Host.ServiceTick(h.Drafts.Kernel);
        h.Host.ServiceTick(h.Drafts.Kernel);
        Assert.Null(h.Host.CurrentFiling);
        Assert.Empty(h.Intake.Requests);
        Assert.Null(Assert.Single(h.Drafts.Store.ReadAll()).Filings);
        Assert.Empty(h.FilingEvents());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dependencies_apply_after_filing_and_failures_keep_the_goal(bool throws)
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed(scope: new("overlap", [new("bbbbbbbb1111", "scope"), new("cccccccc2222", "scope")]));
        h.Intake.Dependency = id =>
        {
            Assert.Single(h.Intake.Goals); // The dependency seam must run after the intake committed.
            if (id.StartsWith('c'))
            {
                if (throws) throw new InvalidOperationException("dependency fault");
                return new("failed", null, "dep out", "dep err", 7);
            }
            return new("applied", null, "dep success", "", 0);
        };
        var result = h.Host.AttemptFiling(draft.Id);
        Assert.Equal("filed", result.Result);
        Assert.Equal(Assert.Single(h.Intake.Goals).Value, result.GoalId);
        Assert.Equal(2, h.Intake.Dependencies.Count);
        Assert.All(h.Intake.Dependencies, dependency => Assert.Equal(result.GoalId, dependency.Goal));
        Assert.True(result.Dependencies![0].Applied);
        Assert.False(result.Dependencies[1].Applied);
        Assert.Contains(throws ? "dependency fault" : "dep err", result.Dependencies[1].Stderr);
        Assert.Contains("depends=bbbbbbbb reason=dependency-failed:cccccccc", BoardFillFiledEvent.Format(draft, result));
        Assert.Equal(2, Assert.Single(h.Drafts.Store.ReadAll()).Filings!.Single().Dependencies!.Count);
        var replay = h.Host.AttemptFiling(draft.Id);
        Assert.Equal("replayed", replay.Result);
        Assert.Equal(result.Reason, replay.Reason);
        Assert.Equal(2, h.Intake.Dependencies.Count);
    }

    [Theory]
    [InlineData("filed", "ok", "aaaaaaaa", "intake stdout", "intake stderr", 0)]
    [InlineData("replayed", "ok", "aaaaaaaa", "replay out", "", 0)]
    [InlineData("refused", "stale-head", "none", "", "", 0)]
    [InlineData("failed", "intake-failed", "none", "failure out", "failure err", 9)]
    public void Each_result_has_one_exact_operator_event_and_retains_outputs(string kind, string reason,
        string goal, string stdout, string stderr, int exitCode)
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed();
        if (kind == "refused") h.Head = "changed";
        if (kind is "failed" or "replayed")
            h.Intake.Filing = _ => new(kind, kind == "replayed" ? "aaaaaaaa1111" : null, stdout, stderr, exitCode);
        var result = h.Host.AttemptFiling(draft.Id);
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        h.Host.ServiceTick(h.Drafts.Kernel);
        h.Host.ServiceTick(h.Drafts.Kernel);
        using var receipt = JsonDocument.Parse(Assert.Single(h.FilingEvents()));
        Assert.Equal($"BOARD_FILL_FILED backlog={draft.BacklogItemId[..8]} draft={draft.Id} result={kind} " +
            $"goal={goal} depends=none reason={reason}", receipt.RootElement.GetProperty("detail").GetString());
        Assert.Equal("decision", receipt.RootElement.GetProperty("operator").GetString());
        var stored = Assert.Single(h.Drafts.Store.ReadAll()).Filings!.Single();
        Assert.Equal(stdout, stored.Stdout);
        Assert.Equal(stderr, stored.Stderr);
        Assert.Equal(exitCode, stored.ExitCode);
        Assert.True(stored.Reported);
        Assert.Equal(kind, result.Result);
    }

    [Fact]
    public async Task Intake_exception_is_harvested_and_later_ticks_continue_without_retrying_failed_draft()
    {
        using var h = new BoardFillFilingTestFixture();
        h.Assessed();
        h.Intake.Filing = _ => throw new InvalidOperationException("intake broke");
        h.Host.ServiceTick(h.Drafts.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentFiling!, "failed filing completed");
        h.Host.ServiceTick(h.Drafts.Kernel);
        h.Host.ServiceTick(h.Drafts.Kernel);
        Assert.Null(h.Host.CurrentFiling);
        Assert.Single(h.Intake.Requests);
        Assert.Single(h.FilingEvents());
        var attempt = Assert.Single(h.Drafts.Store.ReadAll()).Filings!.Single();
        Assert.Equal("failed", attempt.Result);
        Assert.Equal(1, attempt.ExitCode);
        Assert.Contains("intake broke", attempt.Stderr);
        Assert.Equal(0, h.Drafts.Store.FiledOnUtcDay(h.Drafts.Clock.UtcNow));
    }
}
