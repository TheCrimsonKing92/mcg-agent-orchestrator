using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;
using Microsoft.Data.Sqlite;

// Parallel-safe: private SQLite/file roots, injected clock, no model or CLI process.
public sealed class ConductorBoardFillFilingRecoveryTests
{
    [Fact]
    public void Board_read_fault_is_a_retryable_refusal_without_intake()
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed();
        h.BeforeBoard = () => throw new InvalidOperationException("board read broke");
        var result = h.Host.AttemptFiling(draft.Id);
        Assert.Equal("refused", result.Result);
        Assert.Equal("board-unavailable", result.Reason);
        Assert.False(result.Terminal);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("board read broke", result.Stderr);
        Assert.Empty(h.Intake.Requests);
        Assert.Null(result.GoalId);
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        h.Host.ServiceTick(h.Drafts.Kernel);
        var filingEvent = Assert.Single(h.FilingEvents());
        Assert.Contains("result=refused goal=none depends=none reason=board-unavailable", filingEvent);
        h.BeforeBoard = null;
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.File };
        Assert.Equal("filed", h.Host.AttemptFiling(draft.Id).Result);
        Assert.Single(h.Intake.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_before_intake_is_a_retryable_refusal(bool atBoundary)
    {
        using var h = new BoardFillFilingTestFixture();
        using var cancellation = new CancellationTokenSource();
        var draft = h.Assessed();
        if (atBoundary) h.BeforeHead = cancellation.Cancel;
        else cancellation.Cancel();
        var result = h.Host.AttemptFiling(draft.Id, cancellation.Token);
        Assert.Equal("refused", result.Result);
        Assert.Equal("filing-cancelled", result.Reason);
        Assert.False(result.Terminal);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(h.Intake.Requests);
        Assert.Null(result.GoalId);
        Assert.Null(Assert.Single(h.Drafts.Store.ReadAll()).Filings!.Single().Intake);
        h.BeforeHead = null;
        Assert.Equal("filed", h.Host.AttemptFiling(draft.Id).Result);
    }

    [Fact]
    public async Task Pending_committed_request_replays_when_board_admission_is_closed()
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed(scope: new("overlap", [new("bbbbbbbb1111", "scope")]));
        var pending = h.Drafts.Store.BeginFiling(draft.Id, h.Drafts.Clock.UtcNow);
        // Simulate the crash gap: intake committed, but no final attempt outcome was persisted.
        var created = h.Intake.File(new(draft.Id, h.Drafts.DraftPath, draft.BacklogItemId), default);
        h.Board = new(h.Policy.BoardFillTargetActiveGoals, new HashSet<string> { draft.BacklogItemId });
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 0 };
        h.Drafts.Item = h.Drafts.Item with { UpdatedAt = h.Drafts.Item.UpdatedAt.AddMinutes(1) };
        h.Head = "moved-after-commit";
        h.BeforeBoard = () => throw new InvalidOperationException("Committed replay needs no admission read.");
        h.Host.ServiceTick(h.Drafts.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentFiling!, "pending committed request replayed");
        h.Host.ServiceTick(h.Drafts.Kernel);
        var result = Assert.Single(h.Drafts.Store.ReadAll()).Filings!.Single();
        Assert.Equal(pending.Id, result.Id);
        Assert.Equal("replayed", result.Result);
        Assert.Equal(created.GoalId, result.GoalId);
        Assert.Equal(pending.RequestKey, h.Intake.Requests.Last().RequestKey);
        Assert.Equal(2, h.Intake.Requests.Count);
        Assert.Single(h.Intake.Goals);
        Assert.Equal((created.GoalId!, "bbbbbbbb1111"), Assert.Single(h.Intake.Dependencies));
        Assert.True(Assert.Single(result.Dependencies!).Applied);
        Assert.Single(h.FilingEvents());
    }

    [Fact]
    public void Pending_uncommitted_attempt_still_obeys_admission()
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed();
        var pending = h.Drafts.Store.BeginFiling(draft.Id, h.Drafts.Clock.UtcNow);
        h.Board = new(h.Policy.BoardFillTargetActiveGoals, new HashSet<string>());
        var result = h.Host.AttemptFiling(draft.Id);
        Assert.Equal(pending.Id, result.Id);
        Assert.Equal("refused", result.Result);
        Assert.Equal("target-reached", result.Reason);
        Assert.False(result.Terminal);
        Assert.Empty(h.Intake.Requests);
        Assert.Empty(h.Intake.Goals);
    }

    [Fact]
    public async Task Cancellation_during_recovery_does_not_discard_committed_request()
    {
        using var h = new BoardFillFilingTestFixture();
        using var cancellation = new CancellationTokenSource();
        var draft = h.Assessed();
        h.Drafts.Store.BeginFiling(draft.Id, h.Drafts.Clock.UtcNow);
        var created = h.Intake.File(new(draft.Id, h.Drafts.DraftPath, draft.BacklogItemId), default);
        cancellation.Cancel();
        Assert.Equal("refused", h.Host.AttemptFiling(draft.Id, cancellation.Token).Result);
        h.Board = new(100, new HashSet<string> { draft.BacklogItemId });
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 0 };
        h.Drafts.Clock.UtcNow = h.Drafts.Clock.UtcNow.AddMinutes(1);
        h.Host.ServiceTick(h.Drafts.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentFiling!, "cancelled recovery replayed committed request");
        h.Host.ServiceTick(h.Drafts.Kernel);
        var result = Assert.Single(h.Drafts.Store.ReadAll()).Filings!.Last();
        Assert.Equal("replayed", result.Result);
        Assert.Equal(created.GoalId, result.GoalId);
        Assert.Single(h.Intake.Goals);
        Assert.Equal(2, h.FilingEvents().Length);
    }

    [Theory]
    [InlineData("mode", "mode-changed")]
    [InlineData("owner", "not-fileable:owner-gated")]
    [InlineData("fileability", "not-fileable:changed")]
    public void Pending_committed_attempt_keeps_policy_and_fileability_gates(string fault, string reason)
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed(fileable: fault != "fileability");
        h.Drafts.Store.BeginFiling(draft.Id, h.Drafts.Clock.UtcNow);
        h.Intake.Goals["board-fill-" + draft.Id] = "aaaaaaaa1111";
        h.Board = new(100, new HashSet<string> { draft.BacklogItemId });
        if (fault == "mode") h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        if (fault == "owner") h.Drafts.Item = h.Drafts.Item with { Tags = "owner-gated" };
        var result = h.Host.AttemptFiling(draft.Id);
        Assert.Equal("refused", result.Result);
        Assert.Equal(reason, result.Reason);
        Assert.Empty(h.Intake.Requests);
        Assert.Empty(h.Intake.Dependencies);
    }

    [Fact]
    public async Task Finish_fault_preserves_created_receipt_and_host_can_resume()
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed();
        StoreSql(h, """
            CREATE TRIGGER reject_filing_finish BEFORE UPDATE ON board_fill_rounds
            WHEN json_extract(NEW.payload, '$.Filings[0].Result') IS NOT NULL
            BEGIN SELECT RAISE(ABORT, 'injected finish fault'); END;
            """);
        h.Host.ServiceTick(h.Drafts.Kernel);
        var faulted = h.Host.CurrentFiling!;
        var fault = await Assert.ThrowsAsync<SqliteException>(() =>
            PanelTestHarness.Signal(faulted, "filing reached injected finish fault"));
        Assert.Contains("injected finish fault", fault.Message);
        var pending = Assert.Single(h.Drafts.Store.ReadAll()).Filings!.Single();
        Assert.Null(pending.Result);
        Assert.False(pending.Terminal);
        Assert.Equal("filed", pending.Intake!.Kind);
        Assert.Equal(Assert.Single(h.Intake.Goals).Value, pending.GoalId);
        Assert.Equal("intake stdout", pending.Stdout);
        Assert.Equal("intake stderr", pending.Stderr);
        Assert.Empty(h.FilingEvents());
        Assert.Throws<SqliteException>(() => h.Host.ServiceTick(h.Drafts.Kernel));
        Assert.Null(h.Host.CurrentFiling); // A fault is surfaced once; the slot is always released.
        StoreSql(h, "DROP TRIGGER reject_filing_finish;");
        h.Board = new(100, new HashSet<string> { draft.BacklogItemId });
        h.Host.ServiceTick(h.Drafts.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentFiling!, "recovery completed after store repair");
        h.Host.ServiceTick(h.Drafts.Kernel);
        var recovered = Assert.Single(h.Drafts.Store.ReadAll()).Filings!.Single();
        Assert.Equal("replayed", recovered.Result);
        Assert.Equal(pending.GoalId, recovered.GoalId);
        Assert.Single(h.Intake.Goals);
        Assert.Single(h.FilingEvents());
    }

    [Fact]
    public async Task Repeated_refusal_waits_for_injected_clock_before_retry()
    {
        using var h = new BoardFillFilingTestFixture();
        h.Assessed();
        h.Head = null;
        h.Host.ServiceTick(h.Drafts.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentFiling!, "first head refusal completed");
        h.Host.ServiceTick(h.Drafts.Kernel);
        h.Host.ServiceTick(h.Drafts.Kernel);
        Assert.Null(h.Host.CurrentFiling);
        Assert.Single(Assert.Single(h.Drafts.Store.ReadAll()).Filings!);
        Assert.Single(h.FilingEvents());
        Assert.Empty(h.Intake.Requests);
        h.Drafts.Clock.UtcNow = h.Drafts.Clock.UtcNow.AddMinutes(1);
        h.Host.ServiceTick(h.Drafts.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentFiling!, "clock admitted next head refusal");
        h.Host.ServiceTick(h.Drafts.Kernel);
        Assert.Equal(2, Assert.Single(h.Drafts.Store.ReadAll()).Filings!.Count);
        Assert.Equal(2, h.FilingEvents().Length);
        Assert.Null(h.Host.CurrentFiling);
        Assert.Empty(h.Intake.Requests);
    }

    private static void StoreSql(BoardFillFilingTestFixture h, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(h.Drafts.Root, "board-fill.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
