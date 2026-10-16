using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerGoalDetailSnapshot(
    Goal Goal, int? LandingQueuePosition, IReadOnlyList<OwnerConductEvent> RecentEvents);
