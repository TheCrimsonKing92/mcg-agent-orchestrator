using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleStageDescriber
{
    internal const string OwnerQuestion = "question for you";

    internal static string Describe(Goal goal, OwnerConsoleBoardSortKey key, int? queuePosition, DateTimeOffset now,
        OwnerConsoleGateProgress? gate = null)
    {
        if (key.Rank is OwnerConsoleAttention.GateRunning or OwnerConsoleAttention.WaitingForGate &&
            gate?.ForMember(goal.Id.Value) is { } live)
            return live.TotalCount > 0 ? $"in gate: {live.DoneCount}/{live.TotalCount}, {OwnerConsoleGateFormatter.Remaining(live.RemainingSeconds)} left" :
                $"gate running {(int)live.Elapsed.TotalMinutes}m";
        if (gate?.ForMember(goal.Id.Value) is null && !(goal.Status == GoalStatus.Verifying && gate is null) &&
            OwnerConsoleLandingBlockerText.Board(goal, now, queuePosition) is { } held) return held;
        switch (key.Rank)
        {
            case OwnerConsoleAttention.NeedsYou: return OwnerQuestion;
            case OwnerConsoleAttention.Held:
                var blocker = goal.CurrentHold?.Blocker;
                var dependency = Regex.Match(blocker ?? "",
                    @"^(?:waiting on dependency\s+|dependency escalated:\s*|dependency-terminal-without-landing:\s*)([a-fA-F0-9]{8})");
                return dependency.Success ? "held: depends on " + dependency.Groups[1].Value :
                    "held: " + OwnerActivityText.WaitingOn(blocker);
            case OwnerConsoleAttention.GateRunning:
                if (gate is not null) return OwnerConsoleVerifyingGate.Stage(goal, gate, now);
                var started = OwnerConsoleBoardSortKey.LatestPolicy(goal)?.OccurredAt;
                return "gate running" + (started is null ? "" : $" {Math.Max(0, (int)(now - started.Value).TotalMinutes)}m");
            case OwnerConsoleAttention.WaitingForGate:
                if (gate is { Live: true, Members.Count: > 0 })
                    return $"queued for gate #{queuePosition}, behind {gate.Members[0].Id[..Math.Min(8, gate.Members[0].Id.Length)]}";
                return "waiting for gate" + (queuePosition is null ? "" : $" ({OwnerConsoleHoldPhrase.Ordinal(queuePosition.Value)} in queue)");
            case OwnerConsoleAttention.WorkerRunning:
                return Labelled(goal, OwnerConsoleBoardSortKey.RunningTask(goal)!.RequiredRole.ToString());
            case OwnerConsoleAttention.RecentTerminal:
                var reason = goal.Status == GoalStatus.Failed
                    ? goal.Timeline.Where(item => item.Kind == ProgressKind.TaskFailed).MaxBy(item => item.OccurredAt)?.Message
                    : null;
                reason ??= OwnerConsoleBoardSortKey.LatestPolicy(goal)?.Message;
                return OwnerConsoleStatusLabel.For(goal.Status) + ": " +
                    (OwnerHoldReason.FirstLine(reason) ?? "The reason was not recorded.");
            case OwnerConsoleAttention.Other: return Labelled(goal, OwnerConsoleGoalDetail.Stage(goal));
            default: throw new ArgumentOutOfRangeException(nameof(key), key.Rank, "Unknown board attention rank.");
        }
    }

    private static string Labelled(Goal goal, string role) => OwnerConsoleStatusLabel.For(goal.Status) +
        (role == "-" ? "" : ": " + role);
}
