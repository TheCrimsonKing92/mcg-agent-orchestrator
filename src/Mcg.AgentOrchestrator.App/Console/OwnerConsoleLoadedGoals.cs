using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleLoadedGoals(
    IReadOnlyList<Goal> Goals, IReadOnlyList<HumanInputRequest> Requests);
