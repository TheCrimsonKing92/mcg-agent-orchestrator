namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record InvalidatedAcceptanceAttemptStopResult(
    string Outcome,
    IReadOnlyList<int> DescendantPids,
    string? Detail = null);
