using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Evidence from a completed reload, not an inference from absence in the live working set.
// Only Missing and Terminal justify rejecting an otherwise in-scope unloaded goal's intent.
internal abstract record ConductorGoalReloadObservation
{
    public sealed record NotObserved : ConductorGoalReloadObservation;
    public sealed record Missing : ConductorGoalReloadObservation;
    public sealed record Terminal(GoalStatus Status) : ConductorGoalReloadObservation;
}
