using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBoardFillHost
{
    private readonly ConductorBoardFillDraftStore _store;
    private readonly Func<string, CancellationToken, AuthorBriefDraftOutcome> _draft;
    private readonly Func<IReadOnlyList<BacklogItem>> _backlog;
    private readonly Func<AgentOrchestratorKernel, IReadOnlyList<BacklogItem>, Func<BacklogItem, BacklogReadiness>> _readiness;
    private readonly Func<ConductorAutonomyPolicy> _policy;
    private readonly ConductEventLogWriter _events;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string?> _mainHead;
    private readonly Func<string?> _staleMainHead;
    private readonly Func<IReadOnlyList<string>>? _preferredOrder;
    private readonly BoardFillTrackedEditsHold _trackedEditsHold;
    private readonly IBoardFillPremiseVerifier? _verifier;
    private readonly CancellationTokenSource _shutdown = new();
    private BoardFillDraftRound? _running;
    private Task<BoardFillRoundResult>? _round;
    private AgentOrchestratorKernel? _kernel;
    private bool _recovered;
    // In memory only; a process restart clears the hold.
    private string? _heldMainHead;
    private string? _lastPlanReadFailure;
    internal Task? CurrentRound => _round;

    // Drafting is read-only; optional filing delegates all goal mutations to the CLI intake seam.
    internal ConductorBoardFillHost(ConductorBoardFillDraftStore store,
        Func<string, CancellationToken, AuthorBriefDraftOutcome> draft,
        Func<IReadOnlyList<BacklogItem>> backlog,
        Func<AgentOrchestratorKernel, IReadOnlyList<BacklogItem>, Func<BacklogItem, BacklogReadiness>> readiness,
        Func<ConductorAutonomyPolicy> policy, ConductEventLogWriter events, Func<DateTimeOffset>? utcNow = null,
        IBoardFillPremiseVerifier? verifier = null, BoardFillFilingSeams? filing = null,
        Func<string?>? mainHead = null, Func<(bool Clean, AuthorDraftTrackedEdits? Edits)>? trackedEdits = null,
        Func<string?>? staleMainHead = null, Func<IReadOnlyList<string>>? preferredOrder = null)
    {
        _store = store;
        _draft = draft;
        _backlog = backlog;
        _readiness = readiness;
        _policy = policy;
        _events = events;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _verifier = verifier;
        _filingSeams = filing;
        _mainHead = mainHead ?? (() => null);
        _staleMainHead = staleMainHead ?? _mainHead;
        _preferredOrder = preferredOrder;
        _trackedEditsHold = new(store, events, trackedEdits ?? (() => (false, null)));
    }

    internal void ServiceTick(AgentOrchestratorKernel kernel, string? onlyGoalId = null)
    {
        if (onlyGoalId is not null || _shutdown.IsCancellationRequested) return;
        var policy = _policy();
        if (policy.BoardFillMode == ConductorBoardFillMode.Off) return;
        var errors = policy.Validate();
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
        var now = _utcNow();
        _kernel = kernel;
        if (!_recovered)
        {
            foreach (var interrupted in _store.ReadAll().Where(round => round.Outcome is null))
                _store.Finish(interrupted, Failed("interrupted"), now);
            foreach (var unfinished in _store.ReadAll().Where(round => round.Outcome is not null && round.Assessment is null))
                Assess(unfinished, null, new("unavailable", 0, [], "assessment-interrupted"), kernel);
            _recovered = true;
        }
        var held = Harvest(now);
        ReportFinished();
        ServiceFiling();
        if (held || _round is not null || kernel.Goals.Count(goal => !goal.IsTerminal) >= policy.BoardFillTargetActiveGoals ||
            _store.StartedOnUtcDay(now) >= policy.BoardFillMaxDraftsPerDay) return;
        if (_heldMainHead is not null)
        {
            string? mainHead;
            try { mainHead = _mainHead(); }
            catch { return; } // An unresolved head cannot release a repository hold.
            if (string.IsNullOrWhiteSpace(mainHead) ||
                string.Equals(mainHead, _heldMainHead, StringComparison.OrdinalIgnoreCase)) return;
            _heldMainHead = null;
        }
        if (_trackedEditsHold.Blocks(now)) return;
        string? observedMain;
        try { observedMain = _staleMainHead(); }
        catch { observedMain = null; }
        var items = ConductorTickStepLedger.CountBacklogRows(_backlog());
        var preferredOrder = ReadPreferredOrder(now);
        var item = BoardFillReadyItemSelector.Select(items, kernel.Goals.ToArray(), _store.AlreadyDrafted(items, observedMain),
            ConductorTickStepLedger.CountReadinessEvaluations(_readiness(kernel, items)), _store.ReadAll(), preferredOrder);
        if (item is null) return;
        _running = _store.Begin(item, now);
        // Only immutable identity crosses the thread boundary; the model never reads the live kernel.
        _round = Task.Run(() => RunRoundAsync(item.Id));
    }

    private IReadOnlyList<string> ReadPreferredOrder(DateTimeOffset now)
    {
        try
        {
            var order = _preferredOrder?.Invoke() ?? [];
            _lastPlanReadFailure = null;
            return order;
        }
        catch (Exception exception)
        {
            var detail = $"BOARD_FILL_PLAN_READ_FAILED reason={exception.GetType().Name}: {exception.Message}"
                .Replace('\r', ' ').Replace('\n', ' ');
            if (detail != _lastPlanReadFailure && _events.AppendRequired("board-fill-draft", null, detail, now))
                _lastPlanReadFailure = detail;
            return [];
        }
    }

    private bool Harvest(DateTimeOffset now)
    {
        if (_round is not { IsCompleted: true } || _running is null) return false;
        BoardFillRoundResult result;
        try { result = _round.GetAwaiter().GetResult(); }
        catch (Exception exception) { result = new(Failed($"{exception.GetType().Name}: {exception.Message}"), null,
            new("failed", 0, [], exception.GetType().Name)); }
        _store.Finish(_running, result.Outcome, now);
        var finished = _store.ReadAll().Single(round => round.Id == _running.Id);
        _trackedEditsHold.Enter(finished);
        Assess(finished, result.Markdown, result.Verification, _kernel!);
        _running = null;
        _round = null;
        if (result.Outcome.Kind == "held" && !string.IsNullOrWhiteSpace(result.Outcome.HeldMainHead))
            _heldMainHead = result.Outcome.HeldMainHead;
        return result.Outcome.Kind == "held";
    }

    private void ReportFinished()
    {
        foreach (var round in _store.ReadAll().Where(round => round.Assessment is not null && !round.Reported))
        {
            var detail = $"BOARD_FILL_DRAFT backlog={round.BacklogItemId[..Math.Min(8, round.BacklogItemId.Length)]} " +
                $"outcome={round.Outcome} checks={round.Checks.Count(check => check.Passed)}/{round.Checks.Count} " +
                $"draft={round.DraftPath ?? "none"}";
            var assessment = round.Assessment!;
            detail += $" verified={assessment.Verification.VerifiedCount}/{assessment.Verification.BulletCount}" +
                $" fileable={assessment.Fileable.ToString().ToLowerInvariant()} depends=" +
                (assessment.Scope.Depends.Count == 0 ? "none" : string.Join(',', assessment.Scope.Depends.Select(dependency =>
                    dependency.GoalId[..Math.Min(8, dependency.GoalId.Length)])));
            if (_events.AppendRequired("board-fill-draft", null, detail, round.FinishedAt, "board-fill-" + round.Id))
                _store.MarkReported(round.Id);
        }
    }

    internal void Stop()
    {
        _shutdown.Cancel();
        // Only shutdown may drain. ServiceTick never waits on a drafting task.
        try { _round?.Wait(TimeSpan.FromSeconds(30)); }
        catch (AggregateException) { }
        Harvest(_utcNow());
        if (_running is { } running) _store.Finish(running, Failed("conductor-stop"), _utcNow());
        _running = null;
        _round = null;
        StopFiling();
    }

    private static AuthorBriefDraftOutcome Failed(string reason) => new("failed", 1, null, null, null, [], reason);
}
