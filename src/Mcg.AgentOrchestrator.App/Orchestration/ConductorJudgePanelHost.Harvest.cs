using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorJudgePanelHost
{
    private void Harvest(AgentOrchestratorKernel kernel)
    {
        if (_round is not { IsCompleted: true } || _running is null) return;
        var claim = _running;
        if (_round.IsFaulted || _round.IsCanceled)
            _store.Preserve(claim, "case-runner-failed");
        else
        {
            var goal = kernel.Goals.FirstOrDefault(goal => goal.Id.Value == claim.Key.GoalId);
            var superseded = goal is null ||
                !string.Equals(_candidate(goal), claim.Key.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
                EffectiveAcceptanceCriteriaVersion.ComputeForGoal(goal) != claim.Key.CriteriaVersion;
            var results = _store.Results(claim.Id);
            var suspended = results.Count == ConductorJudgePanelBudgets.MaxJudgeCallsPerCase &&
                            results.All(result => result.Outcome == PanelJudgeOutcome.Skipped && result.Reason == "judge-suspended");
            _store.Complete(claim, superseded ? PanelCaseTerminal.Superseded : suspended ?
                PanelCaseTerminal.SuspendedSkip : PanelCaseTerminal.Completed, superseded ? "candidate-or-criteria-changed" : null);
        }
        _running = null;
        _round = null;
    }

    private void ReportTerminals()
    {
        foreach (var item in _store.Unreported())
        {
            var results = _store.Results(item.Id);
            var detail = $"PANEL_CASE case={item.Id} trigger={item.Key.TriggerId} result={PanelV0Contract.Token(item.Terminal!.Value)} " +
                         string.Join(' ', results.Select(result => $"{result.Judge}={PanelV0Contract.Token(result.Outcome)}"));
            if (_conduct.AppendRequired("judge-panel", item.Key.GoalId, detail, _utcNow(), "panel-case-" + item.Id))
                _store.MarkReported(item.Id);
        }
    }
}
