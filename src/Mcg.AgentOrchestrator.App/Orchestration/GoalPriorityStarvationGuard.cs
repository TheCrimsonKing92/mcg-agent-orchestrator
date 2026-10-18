using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class GoalPriorityStarvationGuard
{
    internal StrongBox<DateTimeOffset?> DriftState { get; } = new();
    private HashSet<(string Goal, string Task, int Pid, DateTimeOffset StartedAt)>? _running;
    private readonly Dictionary<GoalId, int> _pendingDispatchOvertakes = [];
    private int _tick;
    private bool _tickObserved;

    internal StrongBox<DateTimeOffset?> BeginTick(AgentOrchestratorKernel kernel, int tick)
    {
        _tick = tick;
        _tickObserved = true;
        _running ??= Running(kernel);
        // The sweep replaces goal instances before this call; replay post-checkpoint changes by id.
        foreach (var goal in kernel.Goals)
            if (_pendingDispatchOvertakes.TryGetValue(goal.Id, out var count))
                GoalEffectivePriorityIndex.SetDispatchOvertakes(goal, count);
        _pendingDispatchOvertakes.Clear();
        return DriftState;
    }

    internal static HashSet<string> GateOccupants(IEnumerable<string> attemptGoalIds, ConductorDriver driver) =>
        attemptGoalIds.Concat(driver.GetActiveAcceptanceCohortCapacity().ActiveRoots.SelectMany(root => root.MemberGoalIds))
            .ToHashSet(StringComparer.Ordinal);

    internal void ObserveGateTick(IReadOnlyList<Goal> orderedEligible, IReadOnlyList<Goal> baseOrder, IReadOnlySet<string> before,
        IReadOnlySet<string> after, int tick, HashSet<GoalId> changedGoalIds, Action<string> emit)
    {
        _tick = tick;
        _tickObserved = true;
        var served = after.Except(before, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        // Inline reconciliation changes Verified history; retain the order captured before admission.
        var waiting = baseOrder.Where(goal => !before.Contains(goal.Id.Value)).ToArray();
        Observe("gate", waiting, orderedEligible, served, null, changedGoalIds, emit);
    }

    internal void ObserveDispatchTick(IReadOnlySet<GoalId> capacityHeld, AgentOrchestratorKernel kernel, Action<string> emit)
    {
        if (!_tickObserved) _tick++;
        var running = Running(kernel);
        var served = running.Except(_running ?? []).Select(item => item.Goal).ToHashSet(StringComparer.Ordinal);
        _running = running;
        _tickObserved = false;
        var goals = kernel.Goals.ToArray();
        var changed = new HashSet<GoalId>();
        Observe("dispatch", goals, GoalSchedulingOrder.ForWalk(goals), served,
            capacityHeld.Select(id => id.Value).ToHashSet(StringComparer.Ordinal), changed, emit);
        foreach (var goal in goals.Where(goal => changed.Contains(goal.Id)))
            _pendingDispatchOvertakes[goal.Id] = goal.PriorityScheduling?.DispatchOvertakes ?? 0;
    }

    private static HashSet<(string Goal, string Task, int Pid, DateTimeOffset StartedAt)> Running(AgentOrchestratorKernel kernel) =>
        kernel.Goals.SelectMany(goal => goal.Tasks.Where(task => task.LastProcess is { IsRunning: true })
            .Select(task => (goal.Id.Value, task.Id.Value, task.LastProcess!.ProcessId, task.LastProcess.StartedAt))).ToHashSet();

    private void Observe(string stage, IReadOnlyList<Goal> baseOrder, IReadOnlyList<Goal> priorityOrder,
        IReadOnlySet<string> served, IReadOnlySet<string>? held, HashSet<GoalId> changed, Action<string> emit)
    {
        var dispatch = stage == "dispatch";
        var ranks = baseOrder.Select((goal, rank) => (goal.Id.Value, rank)).ToDictionary(item => item.Value, item => item.rank);
        // Capture the ordering-point levels before any counter is reset or raised in this observation.
        var levels = priorityOrder.ToDictionary(goal => goal.Id.Value, goal => GoalEffectivePriorityIndex.EffectiveLevel(goal, dispatch));
        var ordered = priorityOrder.Select((goal, rank) => (Goal: goal, Rank: rank)).ToArray();
        foreach (var (goal, rank) in ordered.Where(item => served.Contains(item.Goal.Id.Value)))
        {
            var overtaken = ordered.Where(item => item.Rank > rank && ranks.ContainsKey(item.Goal.Id.Value) &&
                    ranks.TryGetValue(goal.Id.Value, out var servedRank) && ranks[item.Goal.Id.Value] < servedRank &&
                    (held is null || held.Contains(item.Goal.Id.Value)) && levels[goal.Id.Value] > levels[item.Goal.Id.Value])
                .Select(item => item.Goal).ToArray();
            if (overtaken.Length == 0) continue;
            emit($"PRIORITY_ADMISSION stage={stage} tick={_tick} goal={goal.Id.Value[..8]} level={GoalPriorities.Format(goal.Priority)} effective={GoalPriorities.Format(levels[goal.Id.Value])} inheritedFrom={Prefix(goal.PriorityScheduling?.InheritedFromGoalId)} overtook={string.Join(',', overtaken.Select(waiter => $"{waiter.Id.Value[..8]}:{GoalPriorities.Format(levels[waiter.Id.Value])}"))}");
            foreach (var waiter in overtaken)
            {
                if (dispatch) GoalEffectivePriorityIndex.IncrementDispatchOvertakes(waiter);
                else GoalEffectivePriorityIndex.IncrementGateOvertakes(waiter);
                changed.Add(waiter.Id);
                var count = dispatch ? waiter.PriorityScheduling!.DispatchOvertakes : waiter.PriorityScheduling!.GateOvertakes;
                if (count == 3)
                    emit($"PRIORITY_STARVATION_GUARD stage={stage} tick={_tick} goal={waiter.Id.Value[..8]} overtakes=3");
            }
        }
        foreach (var goal in priorityOrder.Where(goal => served.Contains(goal.Id.Value)))
        {
            var count = dispatch ? goal.PriorityScheduling?.DispatchOvertakes : goal.PriorityScheduling?.GateOvertakes;
            if (count is null or 0) continue;
            if (dispatch) GoalEffectivePriorityIndex.ResetDispatchOvertakes(goal);
            else GoalEffectivePriorityIndex.ResetGateOvertakes(goal);
            changed.Add(goal.Id);
        }
    }

    private static string Prefix(string? id) => id is null ? "none" : id[..8];
}
