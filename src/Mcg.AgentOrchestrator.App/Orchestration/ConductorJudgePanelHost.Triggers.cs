namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorJudgePanelHost
{
    internal ConductorJudgePanelTriggerDetector? Triggers { get; init; }

    private void DetectTriggers(string? onlyGoalId)
    {
        try { Triggers?.Detect(onlyGoalId); }
        catch (Exception exception)
        {
            // Source-wide read failures also leave harvesting, reporting and claims live.
            _store.RecordTriggerFailure("source-scan", onlyGoalId ?? "all-goals", exception);
        }
    }
}
