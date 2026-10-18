namespace Mcg.AgentOrchestrator.Core;

// A scope (epic, later program or project) is a future second component; it is not built here.
public readonly record struct GoalSchedulingKey(GoalPriority Level, long WaitingRank) : IComparable<GoalSchedulingKey>
{
    public int CompareTo(GoalSchedulingKey other)
    {
        var level = other.Level.CompareTo(Level);
        return level != 0 ? level : WaitingRank.CompareTo(other.WaitingRank);
    }
}
