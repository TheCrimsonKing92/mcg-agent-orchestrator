using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static string? GetDependencyHoldReason(
        Goal goal,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        AgentOrchestratorKernel kernel) =>
        GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel, out _);

    private static string? GetDependencyHoldReason(
        Goal goal,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        AgentOrchestratorKernel kernel,
        out bool requiresPerson,
        IReadOnlySet<string>? dependencyEscalatedGoals = null) =>
        ConductorDependencyHoldEvaluator.Evaluate(
            goal, completedGoals, escalatedGoals, kernel, out requiresPerson, dependencyEscalatedGoals);
}
