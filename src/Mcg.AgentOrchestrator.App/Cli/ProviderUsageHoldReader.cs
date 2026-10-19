using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProviderUsageHoldReader
{
    internal static IReadOnlyList<ProviderUsageHoldRow> Read(IOrchestratorStateRepository repository,
        IReadOnlyCollection<Goal> loaded, DateTimeOffset now)
    {
        try
        {
            var candidates = ProviderHoldHistory.SeedCandidates(
                repository.ListModelFitHistoryAsync().GetAwaiter().GetResult(),
                loaded.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal));
            if (candidates.Count == 0) return ProviderUsageHoldView.Read(loaded, now);
            var retained = repository.LoadGoalsAsync(candidates.Select(id => new GoalId(id)).ToArray()).GetAwaiter().GetResult();
            var history = new ProviderHoldHistory();
            history.Retain(retained.Goals, loaded);
            return ProviderUsageHoldView.Read(history.Scope(loaded), now);
        }
        catch (Exception) { return ProviderUsageHoldView.Read(loaded, now); }
    }

    internal static IReadOnlyList<ProviderUsageHoldRow> ReadWorkspace(OrchestratorWorkspace workspace, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(workspace.SqliteStatePath)) return [];
            var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var metadata = repository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            var ids = metadata.Where(item => Enum.TryParse<GoalStatus>(item.Status, true, out var status) &&
                    status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
                .Select(item => new GoalId(item.Id)).ToArray();
            return Read(repository, repository.LoadGoalsAsync(ids).GetAwaiter().GetResult().Goals, now);
        }
        catch (Exception) { return []; }
    }
}
