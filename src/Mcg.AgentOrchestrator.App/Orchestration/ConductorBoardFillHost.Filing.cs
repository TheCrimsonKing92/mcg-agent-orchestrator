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
            try { _filing.GetAwaiter().GetResult(); }
            finally { _filing = null; }
        }
        ReportFilings();
        var policy = _policy();
        if (_filingSeams is null || _filing is not null || policy.BoardFillMode != ConductorBoardFillMode.File) return;
        var now = _utcNow();
        var admissionOpen = _kernel!.Goals.Count(goal => !goal.IsTerminal) < policy.BoardFillTargetActiveGoals &&
            _store.FiledOnUtcDay(now) < policy.BoardFillMaxDraftsPerDay;
        var items = _backlog().ToDictionary(item => item.Id, StringComparer.Ordinal);
        var candidate = _store.ReadAll().Where(round => round.Assessment?.Fileable == true && !round.StaleHead &&
            !(round.Filings ?? []).Any(attempt => attempt.Terminal) &&
            ((round.Filings ?? []).Any(attempt => attempt.Result is null) ||
                (admissionOpen || (round.Filings ?? []).Count > 0) && RetryReady(round, now)) &&
            items.TryGetValue(round.BacklogItemId, out var item) && !BoardFillReadyItemSelector.IsOwnerGated(item))
            .OrderBy(round => round.StartedAt).ThenBy(round => round.Id, StringComparer.Ordinal).FirstOrDefault();
        if (candidate is not null) _filing = Task.Run(() => AttemptFiling(candidate.Id, _shutdown.Token));
    }

    // Refusals are retryable, but a persistent read failure must not append a row on every tick.
    private static bool RetryReady(BoardFillDraftRound draft, DateTimeOffset now) =>
        (draft.Filings ?? []).LastOrDefault()?.FinishedAt is not { } finished || now >= finished.AddMinutes(1);

    // Also used directly by deterministic tests. No live kernel crosses this boundary.
    internal BoardFillFilingAttempt AttemptFiling(string draftId, CancellationToken token = default)
    {
        lock (_filingGate)
        {
            var draft = _store.ReadAll().Single(round => round.Id == draftId);
            var previous = (draft.Filings ?? []).LastOrDefault(attempt => attempt.Result is "filed" or "replayed");
            // A crash before FinishFiling resumes the same durable attempt and request key.
            var pending = (draft.Filings ?? []).SingleOrDefault(row => row.Result is null);
            var attempt = pending ?? _store.BeginFiling(draft.Id, _utcNow());
            var refusal = FilingAdmission(draft, attempt, previous is not null, token);
            if (refusal is not null) return Finish(refusal);
            // Re-reading a completed receipt creates no goal and bypasses new-goal admission limits.
            if (previous is not null)
                return Finish(previous with { Id = attempt.Id, StartedAt = attempt.StartedAt,
                    Result = "replayed", Reported = false });
            BoardFillIntakeResult intake;
            try { intake = _filingSeams!.Intake.File(new(draft.Id, Path.GetFullPath(draft.DraftPath!), draft.BacklogItemId), token); }
            catch (Exception exception)
            {
                return Finish(attempt with { Result = "failed", Reason = "intake-exception",
                    Stderr = exception.ToString(), ExitCode = 1, Terminal = true });
            }
            if (intake.Kind is not ("filed" or "replayed") || string.IsNullOrWhiteSpace(intake.GoalId) || intake.ExitCode != 0)
                return Finish(attempt with { Result = "failed", Reason = intake.Reason == "ok" ? "intake-failed" : intake.Reason,
                    GoalId = intake.GoalId, Stdout = intake.Stdout, Stderr = intake.Stderr, ExitCode = intake.ExitCode, Terminal = true });
            // Persistence failures are not intake failures. Leave the attempt pending for keyed recovery;
            // checkpoint the positive receipt before dependencies and final persistence can fail.
            _store.RecordFilingIntake(draft.Id, attempt.Id, intake);
            var dependencies = new List<BoardFillDependencyResult>();
            foreach (var dependency in draft.Assessment!.Scope.Depends)
            {
                try
                {
                    var result = _filingSeams!.Intake.Depend(intake.GoalId, dependency.GoalId, token);
                    dependencies.Add(new(dependency.GoalId, result.Kind == "applied" && result.ExitCode == 0,
                        result.Stdout, result.Stderr, result.ExitCode));
                }
                catch (Exception exception) { dependencies.Add(new(dependency.GoalId, false, "", exception.ToString(), 1)); }
            }
            var failed = dependencies.Where(dependency => !dependency.Applied).Select(dependency => BoardFillFiledEvent.Id8(dependency.GoalId)).ToArray();
            return Finish(attempt with { Result = intake.Kind, Reason = failed.Length == 0 ? intake.Reason :
                "dependency-failed:" + string.Join(',', failed), GoalId = intake.GoalId, Stdout = intake.Stdout,
                Stderr = intake.Stderr, ExitCode = intake.ExitCode, Dependencies = dependencies, Terminal = true, Intake = intake });

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
