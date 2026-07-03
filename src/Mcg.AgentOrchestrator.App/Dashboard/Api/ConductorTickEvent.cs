using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record ConductorTickEvent(
    long Seq,
    DateTimeOffset OccurredAt,
    int Tick,
    int Advanced,
    int Held,
    int Escalated,
    int Retried,
    int Done,
    bool WatchSleeping,
    IReadOnlyList<string>? ProgressLines = null,
    IReadOnlyList<ConductorOperatorDispositionSnapshot>? OperatorDispositions = null);
