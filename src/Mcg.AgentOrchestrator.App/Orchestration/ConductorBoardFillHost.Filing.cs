using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBoardFillHost
{
    private readonly BoardFillFilingSeams? _filingSeams;
    private readonly object _filingGate = new();
    private Task<BoardFillFilingAttempt>? _filing;
    internal Task? CurrentFiling => _filing;

    private void ServiceFiling()
    {
        if (_filing is { IsCompleted: true })
        {
            _filing.GetAwaiter().GetResult();
            _filing = null;
        }
        ReportFilings();
        var policy = _policy();
        if (_filingSeams is null || _filing is not null || policy.BoardFillMode != ConductorBoardFillMode.File ||
            _kernel!.Goals.Count(goal => !goal.IsTerminal) >= policy.BoardFillTargetActiveGoals ||
            _store.FiledOnUtcDay(_utcNow()) >= policy.BoardFillMaxDraftsPerDay) return;
        var items = _backlog().ToDictionary(item => item.Id, StringComparer.Ordinal);
        var candidate = _store.ReadAll().Where(round => round.Assessment?.Fileable == true && !round.StaleHead &&
            !(round.Filings ?? []).Any(attempt => attempt.Terminal) &&
            items.TryGetValue(round.BacklogItemId, out var item) && !BoardFillReadyItemSelector.IsOwnerGated(item))
            .OrderBy(round => round.StartedAt).ThenBy(round => round.Id, StringComparer.Ordinal).FirstOrDefault();
        if (candidate is not null) _filing = Task.Run(() => AttemptFiling(candidate.Id, _shutdown.Token));
    }

    // Also used directly by deterministic tests. No live kernel crosses this boundary.
    internal BoardFillFilingAttempt AttemptFiling(string draftId, CancellationToken token = default)
    {
        lock (_filingGate)
        {
            var draft = _store.ReadAll().Single(round => round.Id == draftId);
            var previous = (draft.Filings ?? []).LastOrDefault(attempt => attempt.Result is "filed" or "replayed");
            // A crash before FinishFiling resumes the same durable attempt and request key.
            var attempt = (draft.Filings ?? []).SingleOrDefault(row => row.Result is null) ??
                _store.BeginFiling(draft.Id, _utcNow());
            try
            {
                token.ThrowIfCancellationRequested();
                var policy = _policy();
                if (policy.BoardFillMode != ConductorBoardFillMode.File) return Refuse("mode-changed");
                if (_filingSeams is null) return Refuse("intake-not-configured", true);
                // Re-reading a completed receipt creates no goal and bypasses new-goal admission limits.
                if (previous is not null)
                    return Finish(previous with { Id = attempt.Id, StartedAt = attempt.StartedAt,
                        Result = "replayed", Reported = false });
                if ((draft.Filings ?? []).Any(row => row.Terminal)) return Refuse("draft-terminal", true);
                var board = _filingSeams.Board();
                if (board.NonTerminalCount >= policy.BoardFillTargetActiveGoals) return Refuse("target-reached");
                if (_store.FiledOnUtcDay(_utcNow()) >= policy.BoardFillMaxDraftsPerDay) return Refuse("daily-cap-reached");
                if (draft.StaleHead) return Refuse("stale-head", true);
                if (draft.Outcome != "draft" || draft.Assessment?.Fileable != true)
                    return Refuse("not-fileable:" + string.Join(',', draft.Assessment?.FailingReasons ?? ["unassessed"]), true);
                var item = _backlog().SingleOrDefault(row => row.Id == draft.BacklogItemId);
                if (item is null) return Refuse("not-fileable:backlog-missing", true);
                if (BoardFillReadyItemSelector.IsOwnerGated(item)) return Refuse("not-fileable:owner-gated", true);
                if (item.Status != BacklogItemStatus.Open || BoardFillReadyItemSelector.ChangeStamp(item) != draft.ChangeStamp)
                    return Refuse("not-fileable:backlog-changed", true);
                if (draft.DraftPath is null || !System.IO.File.Exists(draft.DraftPath))
                    return Refuse("not-fileable:draft-missing", true);
                string? head;
                try { head = _filingSeams.MainHead(); }
                catch { return Refuse("main-head-unavailable"); }
                if (string.IsNullOrWhiteSpace(head)) return Refuse("main-head-unavailable");
                if (head != draft.MainHead) return Refuse("stale-head", true);
                if (board.LinkedBacklogItemIds.Contains(draft.BacklogItemId)) return Refuse("backlog-linked", true);
                // Policy can change while the read seams run. Check again at the mutation boundary.
                token.ThrowIfCancellationRequested();
                if (_policy().BoardFillMode != ConductorBoardFillMode.File) return Refuse("mode-changed");
                var intake = _filingSeams.Intake.File(new(draft.Id, Path.GetFullPath(draft.DraftPath), draft.BacklogItemId), token);
                if (intake.Kind is not ("filed" or "replayed") || string.IsNullOrWhiteSpace(intake.GoalId) || intake.ExitCode != 0)
                    return Finish(attempt with { Result = "failed", Reason = intake.Reason == "ok" ? "intake-failed" : intake.Reason,
                        Stdout = intake.Stdout, Stderr = intake.Stderr, ExitCode = intake.ExitCode, Terminal = true });
                var dependencies = new List<BoardFillDependencyResult>();
                foreach (var dependency in draft.Assessment.Scope.Depends)
                {
                    try
                    {
                        var result = _filingSeams.Intake.Depend(intake.GoalId, dependency.GoalId, token);
                        dependencies.Add(new(dependency.GoalId, result.Kind == "applied" && result.ExitCode == 0,
                            result.Stdout, result.Stderr, result.ExitCode));
                    }
                    catch (Exception exception) { dependencies.Add(new(dependency.GoalId, false, "", exception.ToString(), 1)); }
                }
                var failed = dependencies.Where(dependency => !dependency.Applied).Select(dependency => BoardFillFiledEvent.Id8(dependency.GoalId)).ToArray();
                return Finish(attempt with { Result = intake.Kind, Reason = failed.Length == 0 ? intake.Reason :
                    "dependency-failed:" + string.Join(',', failed), GoalId = intake.GoalId, Stdout = intake.Stdout,
                    Stderr = intake.Stderr, ExitCode = intake.ExitCode, Dependencies = dependencies, Terminal = true });
            }
            catch (Exception exception)
            {
                return Finish(attempt with { Result = "failed", Reason = "intake-exception",
                    Stderr = exception.ToString(), ExitCode = 1, Terminal = true });
            }

            BoardFillFilingAttempt Refuse(string reason, bool terminal = false) =>
                Finish(attempt with { Result = "refused", Reason = reason, Terminal = terminal });
            BoardFillFilingAttempt Finish(BoardFillFilingAttempt result)
            {
                result = result with { FinishedAt = _utcNow() };
                _store.FinishFiling(draft.Id, result);
                return result;
            }
        }
    }

    private void ReportFilings()
    {
        foreach (var draft in _store.ReadAll())
        foreach (var attempt in (draft.Filings ?? []).Where(row => row.Result is not null && !row.Reported))
            if (_events.AppendRequired("board-fill-filed", attempt.GoalId, BoardFillFiledEvent.Format(draft, attempt),
                    attempt.FinishedAt, "board-fill-filed-" + attempt.Id))
                _store.MarkFilingReported(draft.Id, attempt.Id);
    }

    private void StopFiling()
    {
        try { _filing?.Wait(TimeSpan.FromSeconds(30)); }
        catch (AggregateException) { }
        ReportFilings();
    }
}
