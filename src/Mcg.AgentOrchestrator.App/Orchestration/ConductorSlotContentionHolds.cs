using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Only the serial goal walk mutates these process-local streaks.
internal sealed class ConductorSlotContentionHolds
{
    private readonly Dictionary<GoalId, int> _holds = new();

    internal int RecordHold(GoalId goalId)
    {
        _holds.TryGetValue(goalId, out var count);
        // Holding is unbounded; the diagnostic counter must never overflow and re-arm.
        return _holds[goalId] = count < int.MaxValue ? count + 1 : count;
    }

    internal void Clear(GoalId goalId) => _holds.Remove(goalId);

    internal void Clear() => _holds.Clear();

    internal void Retain(IEnumerable<GoalId> workingGoalIds)
    {
        if (_holds.Count == 0) return;
        var retained = workingGoalIds.ToHashSet();
        foreach (var goalId in _holds.Keys.Where(id => !retained.Contains(id)).ToArray())
            _holds.Remove(goalId);
    }
}
