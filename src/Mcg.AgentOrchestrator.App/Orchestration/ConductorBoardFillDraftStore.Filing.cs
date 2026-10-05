namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBoardFillDraftStore
{
    internal BoardFillFilingAttempt BeginFiling(string draftId, DateTimeOffset now)
    {
        var attempt = new BoardFillFilingAttempt(Guid.NewGuid().ToString("N"), "board-fill-" + draftId, now);
        Update(draftId, current => (current.Filings ?? []).Any(row => row.Result is null)
            ? throw new InvalidOperationException("Board-fill filing is already in progress.")
            : current with { Filings = [.. current.Filings ?? [], attempt] });
        return attempt;
    }

    internal void FinishFiling(string draftId, BoardFillFilingAttempt attempt)
    {
        if (attempt.Result is not ("filed" or "replayed" or "refused" or "failed") ||
            attempt.FinishedAt is null || string.IsNullOrWhiteSpace(attempt.Reason))
            throw new InvalidOperationException("Invalid board-fill filing outcome.");
        Update(draftId, current =>
        {
            var rows = current.Filings ?? [];
            var pending = rows.Single(row => row.Id == attempt.Id);
            if (pending.Result is not null) throw new InvalidOperationException("Board-fill filing is already finished.");
            return current with { Filings = rows.Select(row => row.Id == attempt.Id ? attempt : row).ToArray(),
                StaleHead = current.StaleHead || attempt.Reason == "stale-head" };
        });
    }

    internal void MarkFilingReported(string draftId, string attemptId) => Update(draftId, current =>
    {
        var rows = current.Filings ?? [];
        if (rows.Single(row => row.Id == attemptId).Result is null)
            throw new InvalidOperationException("Cannot report an unfinished board-fill filing.");
        return current with { Filings = rows.Select(row => row.Id == attemptId ? row with { Reported = true } : row).ToArray() };
    });

    internal int FiledOnUtcDay(DateTimeOffset now) => ReadAll().SelectMany(round => round.Filings ?? [])
        .Count(attempt => attempt.Result == "filed" && attempt.FinishedAt?.UtcDateTime.Date == now.UtcDateTime.Date);
}
