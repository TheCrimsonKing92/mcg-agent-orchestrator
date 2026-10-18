using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GroupedAdmissionCandidateFilter
{
    internal static ConductorSpeculativeAcceptanceCandidate[] Filter(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates, IEnumerable<Goal> goals,
        IReadOnlySet<string> liveGroupedIds)
    {
        var kept = FilterGoals(candidates.Select(candidate => candidate.GoalId.Value).ToArray(), goals, liveGroupedIds);
        return candidates.Where(candidate => kept.Contains(candidate.GoalId.Value)).ToArray();
    }

    internal static IReadOnlySet<string> FilterGoals(IReadOnlyList<string> candidateIds, IEnumerable<Goal> goals,
        IReadOnlySet<string> liveGroupedIds)
    {
        var lookup = goals.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);
        var targets = candidateIds.Concat(liveGroupedIds).ToHashSet(StringComparer.Ordinal);
        return candidateIds.Where(id => !HasGroupedDependency(id)).ToHashSet(StringComparer.Ordinal);

        bool HasGroupedDependency(string id)
        {
            if (!lookup.TryGetValue(id, out var goal)) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            var pending = new Stack<string>(goal.DependsOn.Select(dependency => dependency.Value));
            while (pending.TryPop(out var dependencyId))
            {
                if (!seen.Add(dependencyId) || !lookup.TryGetValue(dependencyId, out var dependency) || dependency.IsTerminal)
                    continue;
                if (targets.Contains(dependencyId)) return true;
                foreach (var next in dependency.DependsOn) pending.Push(next.Value);
            }
            return false;
        }
    }
}
