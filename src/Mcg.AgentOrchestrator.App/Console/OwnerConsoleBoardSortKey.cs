using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal enum OwnerConsoleAttention { NeedsYou, Held, GateRunning, WaitingForGate, WorkerRunning, Other, RecentTerminal }

// The base key describes goal state; live questions override only its attention rank.
internal readonly record struct OwnerConsoleBoardSortKey(
    OwnerConsoleAttention Rank, DateTimeOffset? Since, string GoalId) : IComparable<OwnerConsoleBoardSortKey>
{
    internal static OwnerConsoleBoardSortKey For(Goal goal)
    {
        var rank = goal.Status is GoalStatus.Failed or GoalStatus.Parked ? OwnerConsoleAttention.RecentTerminal :
            goal.CurrentHold is not null || goal.Status == GoalStatus.WaitingForHuman ? OwnerConsoleAttention.Held :
            goal.Status == GoalStatus.Verifying ? OwnerConsoleAttention.GateRunning :
            goal.Status == GoalStatus.Verified ? OwnerConsoleAttention.WaitingForGate :
            RunningTask(goal) is not null ? OwnerConsoleAttention.WorkerRunning : OwnerConsoleAttention.Other;
        return new(rank, LatestPolicy(goal)?.OccurredAt ?? goal.Timeline.MaxBy(item => item.OccurredAt)?.OccurredAt,
            goal.Id.Value);
    }

    internal OwnerConsoleBoardSortKey WithOwnerQuestion(bool hasQuestion) =>
        hasQuestion && Rank != OwnerConsoleAttention.RecentTerminal ? this with { Rank = OwnerConsoleAttention.NeedsYou } : this;

    internal static TaskSpec? RunningTask(Goal goal) => goal.Tasks.FirstOrDefault(task =>
        task.Status == WorkTaskStatus.Running || task.LastProcess is { IsRunning: true });

    internal static ProgressEvent? LatestPolicy(Goal goal) => goal.Timeline
        .Where(item => item.Kind == ProgressKind.GoalPolicyDecision).MaxBy(item => item.OccurredAt);

    public int CompareTo(OwnerConsoleBoardSortKey other)
    {
        var rank = Rank.CompareTo(other.Rank);
        if (rank != 0) return rank;
        if (Rank is OwnerConsoleAttention.WaitingForGate or OwnerConsoleAttention.RecentTerminal)
        {
            var time = Nullable.Compare(Since, other.Since);
            if (time != 0) return Rank == OwnerConsoleAttention.RecentTerminal ? -time : time;
        }
        return StringComparer.OrdinalIgnoreCase.Compare(GoalId, other.GoalId);
    }
}
