using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Target reloads refresh their baselines; ordinary loads preserve baselines for held changes.
internal static class ConductLoopTargetLoader
{
    internal static AgentOrchestratorKernel LoadOperatorIntentTargets(
        IOrchestratorStateQueries repository, IReadOnlyCollection<GoalId> goalIds,
        IDictionary<string, GoalSnapshot> tickBaselines)
    {
        var loaded = repository.LoadGoalsAsync(goalIds).GetAwaiter().GetResult();
        foreach (var snapshot in loaded.ExportSnapshot().Goals)
            tickBaselines[snapshot.Id] = snapshot;
        return loaded;
    }

    internal static void CaptureMissingTickBaselines(
        IDictionary<string, GoalSnapshot> tickBaselines,
        AgentOrchestratorKernel loadedKernel)
    {
        ArgumentNullException.ThrowIfNull(tickBaselines);
        ArgumentNullException.ThrowIfNull(loadedKernel);
        foreach (var goal in loadedKernel.ExportSnapshot().Goals)
            tickBaselines.TryAdd(goal.Id, goal);
    }
}
