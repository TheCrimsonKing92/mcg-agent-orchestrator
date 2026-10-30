namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record BatchLoopSummary(
    int Ticks,
    int Advanced,
    int Held,
    int Escalated,
    int Retried,
    int Done,
    bool StopRequested,
    ConductorLoopHandoffResult? Handoff = null,
    string? StopReason = null,
    int Rechecks = 0,
    int LandedGoals = 0)
{
    internal HomeBuildAdoptionRecord? HomeBuildTarget { get; init; }
}
