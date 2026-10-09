namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class BoardFillTrackedEditsHold(ConductorBoardFillDraftStore store, ConductEventLogWriter events,
    Func<(bool Clean, AuthorDraftTrackedEdits? Edits)> probe)
{
    internal void Enter(BoardFillDraftRound round)
    {
        if (round.HoldReason != AuthorDraftTrackedEdits.Reason || round.HoldStartReported) return;
        if (Report(round, "started", round.FinishedAt)) store.MarkHoldStartReported(round.Id);
    }

    internal bool Blocks(DateTimeOffset now)
    {
        foreach (var hold in store.ReadAll().Where(round => round.Outcome == "held" &&
                     round.HoldReason == AuthorDraftTrackedEdits.Reason && !round.HoldReleaseReported)
                     .OrderBy(round => round.StartedAt))
        {
            Enter(hold);
            if (!store.ReadAll().Single(round => round.Id == hold.Id).HoldStartReported) return true;
            var releasedAt = hold.HoldReleasedAt;
            if (releasedAt is null)
            {
                try { if (!probe().Clean) return true; }
                catch { return true; }
                store.ReleaseHold(hold.Id, now);
                releasedAt = now;
            }
            // Persist the clean observation before reporting; retry the same event after restart.
            if (!Report(hold, "released", releasedAt)) return true;
            store.MarkHoldReleaseReported(hold.Id);
        }
        return false;
    }

    private bool Report(BoardFillDraftRound round, string state, DateTimeOffset? at) =>
        events.AppendRequired("board-fill-held", null,
            $"BOARD_FILL_HELD state={state} reason={AuthorDraftTrackedEdits.Reason} " +
            $"files={new AuthorDraftTrackedEdits(round.HeldPaths ?? []).Render()}", at,
            $"board-fill-held-{state}-{round.Id}");
}
