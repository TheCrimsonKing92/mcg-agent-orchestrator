namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorInvariantObservation(
    DateTimeOffset Now,
    IReadOnlyList<ConductorInvariantReadyGoal> GateReadyGoals,
    DateTimeOffset? LastGateProgressAt,
    IReadOnlyList<ConductorInvariantRefusal> Refusals,
    IReadOnlyList<ConductorFinishedAttempt>? FinishedAttempts = null,
    ConductorRelaunchObservation? Relaunch = null,
    string? MainSha = null);

internal sealed record ConductorInvariantReadyGoal(string GoalId, DateTimeOffset ReadySince);
internal sealed record ConductorInvariantRefusal(string RecordId, string Reason, DateTimeOffset At);
