using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleStageDescriber
{
    internal const string OwnerQuestion = "question for you";

    internal static string Describe(Goal goal, OwnerConsoleBoardSortKey key, int? queuePosition, DateTimeOffset now)
    {
        switch (key.Rank)
        {
            case OwnerConsoleAttention.NeedsYou: return OwnerQuestion;
            case OwnerConsoleAttention.Held:
                var blocker = goal.CurrentHold?.Blocker;
                var dependency = Regex.Match(blocker ?? "",
                    @"^(?:waiting on dependency\s+|dependency escalated:\s*|dependency-terminal-without-landing:\s*)([a-fA-F0-9]{8})");
                return dependency.Success ? "held: depends on " + dependency.Groups[1].Value :
                    "held: " + OwnerActivityNarrator.WaitingOn(blocker);
            case OwnerConsoleAttention.GateRunning:
                var started = OwnerConsoleBoardSortKey.LatestPolicy(goal)?.OccurredAt;
                return "gate running" + (started is null ? "" : $" {Math.Max(0, (int)(now - started.Value).TotalMinutes)}m");
            case OwnerConsoleAttention.WaitingForGate:
                return "waiting for gate" + (queuePosition is null ? "" : $" ({Ordinal(queuePosition.Value)} in queue)");
            case OwnerConsoleAttention.WorkerRunning:
                return OwnerConsoleBoardSortKey.RunningTask(goal)!.RequiredRole.ToString();
            case OwnerConsoleAttention.RecentTerminal:
                var reason = goal.Status == GoalStatus.Failed
                    ? goal.Timeline.Where(item => item.Kind == ProgressKind.TaskFailed).MaxBy(item => item.OccurredAt)?.Message
                    : null;
                reason ??= OwnerConsoleBoardSortKey.LatestPolicy(goal)?.Message;
                return goal.Status.ToString().ToLowerInvariant() + ": " +
                    (OwnerHoldReason.FirstLine(reason) ?? "The reason was not recorded.");
            case OwnerConsoleAttention.Other: return OwnerConsoleGoalDetail.Stage(goal);
            default: throw new ArgumentOutOfRangeException(nameof(key), key.Rank, "Unknown board attention rank.");
        }
    }

    private static string Ordinal(int position) => position + (position % 100 is 11 or 12 or 13 ? "th" :
        (position % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}
