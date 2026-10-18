using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

// Detached observations shared by the human and agent renderers; lifecycle is projected against ownership.
internal sealed record ConductorStatusSnapshot(
    int? OwnerPid,
    ConductorStatusSnapshot.SinceObservation Since,
    string? SupervisorBuild,
    ConductorStatusSnapshot.GenerationObservation Generation,
    DateTimeOffset? LatestTick,
    ConductorStatusSnapshot.GoalsObservation Goals,
    ConductorStatusSnapshot.LandingObservation? MostRecentLanding,
    ConductorStatusSnapshot.AdoptionObservation? WorkerAdoption,
    ConductorStatusSnapshot.IntentsObservation PendingIntents,
    IReadOnlyList<ProviderUsageHoldRow>? ProviderHolds = null)
{
    internal string ConductorState => OwnerPid is null ? "stopped" : Since.Kind == "starting" ? "starting" : "running";

    internal sealed record SinceObservation(string Kind, DateTimeOffset? At, string? Reason,
        SinceObservation? Previous = null);
    internal sealed record GenerationObservation(string State, string? Build, string? Main);
    internal sealed record GoalObservation(string Id, string Status, string Role);
    internal sealed record GoalsObservation(bool Available, IReadOnlyList<GoalObservation>? NonTerminal,
        int? PendingClarifications, int? HumanWaits);
    internal sealed record LandingObservation(string Goal, DateTimeOffset At);
    internal sealed record AdoptionObservation(string Summary, int DeferredIdentityUnproven);
    internal sealed record IntentGoalObservation(string Goal, int Intents, DateTimeOffset? Latest);
    internal sealed record IntentsObservation(string State, string? Reason, IReadOnlyList<IntentGoalObservation> Goals);
}
