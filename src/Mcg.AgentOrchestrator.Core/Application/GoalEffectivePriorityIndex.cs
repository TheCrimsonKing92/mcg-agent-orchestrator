using System.Runtime.CompilerServices;

namespace Mcg.AgentOrchestrator.Core;

// Owns the materialized dependency-priority index. CLI mutations record seeds, never effective values.
public static class GoalEffectivePriorityIndex
{
    private static readonly StrongBox<DateTimeOffset?> ProcessDriftState = new();

    public static void ResetProcessState() => ProcessDriftState.Value = null;

    public static GoalPriority StoredLevel(Goal goal) => goal.PriorityScheduling is { } state
        ? GoalPriorities.ParseOrDefault(state.EffectiveLevel) : goal.Priority;

    public static GoalPriority EffectiveLevel(Goal goal, bool dispatch = false) =>
        (dispatch ? goal.PriorityScheduling?.DispatchOvertakes : goal.PriorityScheduling?.GateOvertakes) >= 3
            ? GoalPriority.Critical : StoredLevel(goal);

    public static void SetOwnPriority(IEnumerable<Goal> goals, Goal goal, GoalPriority level)
    {
        goal.SetPriority(level);
        var lookup = goals.ToDictionary(item => item.Id.Value, StringComparer.Ordinal);
        Recompute(lookup, DependencyClosure(lookup, [goal.Id.Value]), null);
        goal.PrioritySchedulingPendingPersistence = true;
    }

    internal static void RecordSeeds(Goal goal, IEnumerable<GoalId> targets)
    {
        if (StoredLevel(goal) <= GoalPriority.Normal) return;
        var state = State(goal);
        goal.PriorityScheduling = state with
        {
            PendingSeedGoalIds = (state.PendingSeedGoalIds ?? []).Concat(targets.Select(id => id.Value))
                .Append(goal.Id.Value).Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    public static IReadOnlySet<GoalId> ApplyPending(IEnumerable<Goal> goals, Action<string> emit,
        Func<DateTimeOffset> clock, StrongBox<DateTimeOffset?>? driftState = null)
    {
        var lookup = goals.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);
        var changed = lookup.Values.Where(goal => goal.PrioritySchedulingPendingPersistence)
            .Select(goal => goal.Id).ToHashSet();
        var seeds = lookup.Values.SelectMany(goal => goal.PriorityScheduling?.PendingSeedGoalIds ?? [])
            .Distinct(StringComparer.Ordinal).ToArray();
        if (seeds.Length > 0)
        {
            foreach (var goal in lookup.Values.Where(goal => goal.PriorityScheduling?.PendingSeedGoalIds?.Count > 0))
            {
                Write(goal, State(goal) with { PendingSeedGoalIds = null });
                changed.Add(goal.Id);
            }
            changed.UnionWith(Recompute(lookup, DependencyClosure(lookup, seeds), null));
        }
        var state = driftState ?? ProcessDriftState;
        var now = clock();
        if (state.Value is null || now - state.Value.Value >= TimeSpan.FromMinutes(60))
        {
            changed.UnionWith(Recompute(lookup, lookup.Keys.ToHashSet(StringComparer.Ordinal), emit));
            state.Value = now;
        }
        foreach (var goal in lookup.Values.Where(goal => goal.PrioritySchedulingPendingPersistence))
        {
            changed.Add(goal.Id);
            goal.PrioritySchedulingPendingPersistence = false;
        }
        return changed;
    }

    public static void IncrementGateOvertakes(Goal goal) => ChangeCounter(goal, false, false);
    public static void IncrementDispatchOvertakes(Goal goal) => ChangeCounter(goal, true, false);
    public static void ResetGateOvertakes(Goal goal) => ChangeCounter(goal, false, true);
    public static void ResetDispatchOvertakes(Goal goal) => ChangeCounter(goal, true, true);

    private static void ChangeCounter(Goal goal, bool dispatch, bool reset)
    {
        var state = State(goal);
        var old = dispatch ? state.DispatchOvertakes : state.GateOvertakes;
        var value = reset ? 0 : checked(old + 1);
        if (old == value) return;
        Write(goal, dispatch ? state with { DispatchOvertakes = value } : state with { GateOvertakes = value });
    }

    private static GoalPrioritySchedulingSnapshot State(Goal goal) => goal.PriorityScheduling ??
        new(GoalPriorities.Format(goal.Priority));

    private static void Write(Goal goal, GoalPrioritySchedulingSnapshot state)
    {
        goal.PriorityScheduling = state;
        goal.PrioritySchedulingPendingPersistence = true;
    }

    private static HashSet<string> DependencyClosure(IReadOnlyDictionary<string, Goal> lookup, IEnumerable<string> seeds)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(seeds);
        while (pending.TryPop(out var id))
            if (lookup.TryGetValue(id, out var goal) && closure.Add(id))
                foreach (var dependency in goal.DependsOn) pending.Push(dependency.Value);
        return closure;
    }

    private static IReadOnlySet<GoalId> Recompute(IReadOnlyDictionary<string, Goal> lookup,
        HashSet<string> closure, Action<string>? emitDrift)
    {
        var dependents = lookup.Values.Where(goal => !goal.IsTerminal)
            .SelectMany(goal => goal.DependsOn.Select(id => (id.Value, Goal: goal)))
            .ToLookup(pair => pair.Value, pair => pair.Goal, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var changed = new HashSet<GoalId>();
        var revision = lookup.Values.Select(goal => goal.PriorityScheduling?.GraphRevision ?? 0).DefaultIfEmpty().Max() + 1;
        foreach (var id in closure) Visit(id);
        return changed;

        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException("Priority dependency graph contains a cycle.");
            var goal = lookup[id];
            var level = goal.Priority;
            string? inheritedFrom = null;
            foreach (var dependent in dependents[id].OrderBy(item => item.Id.Value, StringComparer.Ordinal))
            {
                if (closure.Contains(dependent.Id.Value)) Visit(dependent.Id.Value);
                var candidate = StoredLevel(dependent);
                if (candidate <= level) continue;
                level = candidate;
                inheritedFrom = dependent.PriorityScheduling?.InheritedFromGoalId ?? dependent.Id.Value;
            }
            var old = State(goal);
            if (StoredLevel(goal) != level || old.InheritedFromGoalId != inheritedFrom)
            {
                if (StoredLevel(goal) != level)
                    emitDrift?.Invoke($"PRIORITY_DRIFT goal={id[..8]} stored={GoalPriorities.Format(StoredLevel(goal))} computed={GoalPriorities.Format(level)} revision={revision}");
                Write(goal, old with { EffectiveLevel = GoalPriorities.Format(level), InheritedFromGoalId = inheritedFrom, GraphRevision = revision });
                changed.Add(goal.Id);
            }
            else if (emitDrift is null && goal.PriorityScheduling is not null)
            {
                Write(goal, old with { GraphRevision = revision });
                changed.Add(goal.Id);
            }
            visiting.Remove(id);
            visited.Add(id);
        }
    }
}
