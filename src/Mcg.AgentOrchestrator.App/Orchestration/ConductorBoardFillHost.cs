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
    private readonly CancellationTokenSource _shutdown = new();
    private BoardFillDraftRound? _running;
    private Task<AuthorBriefDraftOutcome>? _round;
    private bool _recovered;
    internal Task? CurrentRound => _round;

    // No state repository, backlog writer, goal creator, transaction or outbox seam.
    internal ConductorBoardFillHost(ConductorBoardFillDraftStore store,
        Func<string, CancellationToken, AuthorBriefDraftOutcome> draft,
        Func<IReadOnlyList<BacklogItem>> backlog,
        Func<AgentOrchestratorKernel, IReadOnlyList<BacklogItem>, Func<BacklogItem, BacklogReadiness>> readiness,
        Func<ConductorAutonomyPolicy> policy, ConductEventLogWriter events, Func<DateTimeOffset>? utcNow = null)
    {
        _store = store;
        _draft = draft;
        _backlog = backlog;
        _readiness = readiness;
        _policy = policy;
        _events = events;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal void ServiceTick(AgentOrchestratorKernel kernel, string? onlyGoalId = null)
    {
        if (onlyGoalId is not null || _shutdown.IsCancellationRequested) return;
        var policy = _policy();
        if (policy.BoardFillMode == ConductorBoardFillMode.Off) return;
        var errors = policy.Validate();
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
        var now = _utcNow();
        if (!_recovered)
        {
            foreach (var interrupted in _store.ReadAll().Where(round => round.Outcome is null))
                _store.Finish(interrupted, Failed("interrupted"), now);
            _recovered = true;
        }
        Harvest(now);
        ReportFinished();
        if (_round is not null || kernel.Goals.Count(goal => !goal.IsTerminal) >= policy.BoardFillTargetActiveGoals ||
            _store.StartedOnUtcDay(now) >= policy.BoardFillMaxDraftsPerDay) return;
        var items = _backlog();
        var item = BoardFillReadyItemSelector.Select(items, kernel.Goals.ToArray(), _store.AlreadyDrafted(items),
            _readiness(kernel, items));
        if (item is null) return;
        _running = _store.Begin(item, now);
        // Only immutable identity crosses the thread boundary; the model never reads the live kernel.
        _round = Task.Run(() => _draft(item.Id, _shutdown.Token));
    }

    private void Harvest(DateTimeOffset now)
    {
        if (_round is not { IsCompleted: true } || _running is null) return;
        AuthorBriefDraftOutcome outcome;
        try { outcome = _round.GetAwaiter().GetResult(); }
        catch (Exception exception) { outcome = Failed($"{exception.GetType().Name}: {exception.Message}"); }
        _store.Finish(_running, outcome, now);
        _running = null;
        _round = null;
    }

    private void ReportFinished()
    {
        foreach (var round in _store.ReadAll().Where(round => round.Outcome is not null && !round.Reported))
        {
            var detail = $"BOARD_FILL_DRAFT backlog={round.BacklogItemId[..Math.Min(8, round.BacklogItemId.Length)]} " +
                $"outcome={round.Outcome} checks={round.Checks.Count(check => check.Passed)}/{round.Checks.Count} " +
                $"draft={round.DraftPath ?? "none"}";
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
    }

    private static AuthorBriefDraftOutcome Failed(string reason) => new("failed", 1, null, null, null, [], reason);
}
