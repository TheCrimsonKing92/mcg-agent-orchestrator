using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBoardFillHost
{
    // This phase is read-only. A fault here supplies no evidence that intake failed or a goal was created.
    private BoardFillFilingAttempt? FilingAdmission(BoardFillDraftRound draft, BoardFillFilingAttempt attempt,
        bool completed, CancellationToken token)
    {
        var phase = "policy";
        try
        {
            token.ThrowIfCancellationRequested();
            var policy = _policy();
            if (policy.BoardFillMode != ConductorBoardFillMode.File) return Refuse("mode-changed");
            if (_filingSeams is null) return Refuse("intake-not-configured", true);
            if (completed) return null;
            if ((draft.Filings ?? []).Any(row => row.Terminal)) return Refuse("draft-terminal", true);
            phase = "request-receipt";
            // A crash can occur before OR after goal commit. Only a created request receipt bypasses
            // new-goal admission; File still validates the payload fingerprint through the original intake.
            // Also check after a retryable refusal: cancellation or a policy change during recovery
            // must not erase the ability to replay a goal that was committed before a crash.
            var created = _filingSeams.Intake.FindCreatedGoal(attempt.RequestKey) is not null;
            BoardFillGoalBoard? board = null;
            if (!created)
            {
                phase = "board";
                board = _filingSeams.Board();
                if (board.NonTerminalCount >= policy.BoardFillTargetActiveGoals) return Refuse("target-reached");
                phase = "daily-cap";
                if (_store.FiledOnUtcDay(_utcNow()) >= policy.BoardFillMaxDraftsPerDay) return Refuse("daily-cap-reached");
            }
            phase = "fileability";
            if (draft.StaleHead) return Refuse("stale-head", true);
            if (draft.Outcome != "draft" || draft.Assessment?.Fileable != true)
                return Refuse("not-fileable:" + string.Join(',', draft.Assessment?.FailingReasons ?? ["unassessed"]), true);
            var item = _backlog().SingleOrDefault(row => row.Id == draft.BacklogItemId);
            if (item is null) return Refuse("not-fileable:backlog-missing", true);
            if (BoardFillReadyItemSelector.IsOwnerGated(item)) return Refuse("not-fileable:owner-gated", true);
            if (!created && (item.Status != BacklogItemStatus.Open || BoardFillReadyItemSelector.ChangeStamp(item) != draft.ChangeStamp))
                return Refuse("not-fileable:backlog-changed", true);
            if (draft.DraftPath is null || !System.IO.File.Exists(draft.DraftPath))
                return Refuse("not-fileable:draft-missing", true);
            if (!created)
            {
                phase = "main-head";
                var head = _filingSeams.MainHead();
                if (string.IsNullOrWhiteSpace(head)) return Refuse("main-head-unavailable");
                if (head != draft.MainHead) return Refuse("stale-head", true);
                if (board!.LinkedBacklogItemIds.Contains(draft.BacklogItemId)) return Refuse("backlog-linked", true);
            }
            // Policy can change while the read seams run. Check again at the mutation boundary.
            token.ThrowIfCancellationRequested();
            phase = "policy";
            return _policy().BoardFillMode != ConductorBoardFillMode.File ? Refuse("mode-changed") : null;
        }
        catch (OperationCanceledException exception)
        {
            return Refuse("filing-cancelled") with { Stderr = exception.ToString() };
        }
        catch (Exception exception)
        {
            return Refuse(phase + "-unavailable") with { Stderr = exception.ToString() };
        }

        BoardFillFilingAttempt Refuse(string reason, bool terminal = false) =>
            attempt with { Result = "refused", Reason = reason, Terminal = terminal };
    }
}
