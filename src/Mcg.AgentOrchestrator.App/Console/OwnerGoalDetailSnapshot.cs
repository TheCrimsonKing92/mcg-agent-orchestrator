using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerGoalDetailSnapshot(
    Goal Goal, int? LandingQueuePosition, IReadOnlyList<OwnerConductEvent> RecentEvents,
    OwnerConsoleGateProgress? Gate = null, bool GateObserved = false, OwnerConsoleGateEnd? LastGateEnd = null,
    string? PriorityWarning = null, OwnerGoalChangeSummary? Changes = null)
{
    internal OwnerGoalDetailSnapshot WithGate(OwnerConsoleGateProgress? observed) => this with
    {
        Gate = observed?.ForMember(Goal.Id.Value),
        GateObserved = observed is not null,
        LastGateEnd = OwnerConsoleGateEnd.LatestFor(observed?.Ended, Goal.Id.Value)
    };
}
