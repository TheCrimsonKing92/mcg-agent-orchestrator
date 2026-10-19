using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProviderHoldHistorySeed
{
    internal static void Apply(AgentOrchestratorKernel kernel, IOrchestratorStateRepository repository, Action<string> emit)
    {
        try
        {
            var rows = repository.ListModelFitHistoryAsync().GetAwaiter().GetResult();
            var candidates = ProviderHoldHistory.SeedCandidates(rows,
                kernel.Goals.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal));
            if (candidates.Count == 0) return;
            var loaded = repository.LoadGoalsAsync(candidates.Select(id => new GoalId(id)).ToArray()).GetAwaiter().GetResult();
            var history = new ProviderHoldHistory();
            history.CopyFrom(kernel.ProviderHoldHistory);
            history.Retain(loaded.Goals, kernel.Goals);
            var line = $"PROVIDER_HOLD_HISTORY candidates={candidates.Count} retained={history.Retained.Count} goals={string.Join(",", history.Retained.Select(goal => goal.Id.Value[..8]))}";
            // Emission is part of seeding: a throwing sink must leave the kernel unchanged too.
            emit(line);
            kernel.ProviderHoldHistory.CopyFrom(history);
        }
        catch (Exception exception)
        {
            emit($"PROVIDER_HOLD_HISTORY seed-failed: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
