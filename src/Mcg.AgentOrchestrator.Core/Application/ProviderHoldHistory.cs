namespace Mcg.AgentOrchestrator.Core;

/// <summary>Read-only goal history retained only while it changes a provider hold answer.</summary>
public sealed class ProviderHoldHistory
{
    private readonly SortedDictionary<string, Goal> _retained = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Goal> Retained => _retained.Values;

    public IReadOnlyCollection<Goal> Scope(IReadOnlyCollection<Goal> workingSet)
    {
        if (_retained.Count == 0) return workingSet;
        var ids = workingSet.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal);
        return workingSet.Concat(_retained.Values.Where(goal => !ids.Contains(goal.Id.Value))).ToArray();
    }

    public void Retain(Goal leaving, IReadOnlyCollection<Goal> workingSet) => Retain([leaving], workingSet);

    public void Retain(IReadOnlyCollection<Goal> leaving, IReadOnlyCollection<Goal> workingSet)
    {
        var working = workingSet.ToArray();
        var ids = working.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var goal in leaving)
            if (!ids.Contains(goal.Id.Value)) _retained[goal.Id.Value] = goal;

        bool dropped;
        do
        {
            dropped = false;
            foreach (var goal in _retained.Values.ToArray())
            {
                var scope = Scope(working);
                // An ingested working-set copy already shadows the retained copy.
                var without = ids.Contains(goal.Id.Value)
                    ? scope
                    : scope.Where(item => item.Id != goal.Id).ToArray();
                if (!SameAnswers(goal, scope, without)) continue;
                _retained.Remove(goal.Id.Value);
                dropped = true;
            }
        } while (dropped);
    }

    public void CopyFrom(ProviderHoldHistory other)
    {
        var goals = other.Retained.ToArray();
        _retained.Clear();
        foreach (var goal in goals) _retained.Add(goal.Id.Value, goal);
    }

    public static IReadOnlyList<string> SeedCandidates(
        IEnumerable<ModelFitHistoryRow> rows, IReadOnlySet<string> workingGoalIds)
    {
        var history = rows.ToArray();
        var exhaustions = history.Where(row => string.Equals(
            row.OutcomeRule, TaskOutcomeRules.ProviderBudgetExhausted.Token, StringComparison.Ordinal)).ToArray();
        var candidates = exhaustions.Select(row => row.GoalId).ToHashSet(StringComparer.Ordinal);
        foreach (var provider in exhaustions.GroupBy(row => row.ProviderName, StringComparer.OrdinalIgnoreCase))
        {
            var latestExhaustion = provider.Max(row => row.Timestamp);
            var success = history.Where(row =>
                    string.Equals(row.ProviderName, provider.Key, StringComparison.OrdinalIgnoreCase) &&
                    row.OutcomeClass == TaskOutcomeClass.Success && row.Timestamp > latestExhaustion)
                .OrderByDescending(row => row.Timestamp).ThenBy(row => row.GoalId, StringComparer.Ordinal).FirstOrDefault();
            if (success is not null) candidates.Add(success.GoalId);
        }
        return candidates.Where(id => !workingGoalIds.Contains(id)).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool SameAnswers(Goal goal, IReadOnlyCollection<Goal> scope, IReadOnlyCollection<Goal> without)
    {
        var bindings = goal.Tasks.SelectMany(task => task.DispatchHistory)
            .Select(dispatch => (Provider: dispatch.ProviderName ?? dispatch.WorkerName,
                Credential: dispatch.ClaudeCredentialSourceDirectory)).Distinct();
        foreach (var binding in bindings)
            if (Answer(scope, binding.Provider, binding.Credential) != Answer(without, binding.Provider, binding.Credential))
                return false;
        return true;
    }

    private static (string BindingKey, GoalId GoalId, TaskId TaskId, DateTimeOffset PlacedAt)? Answer(
        IReadOnlyCollection<Goal> scope, string provider, string? credential) =>
        DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(scope, provider, credential, out var hold)
            ? (hold.BindingKey, hold.SourceGoalId, hold.SourceTaskId, hold.PlacedAt)
            : null;
}
