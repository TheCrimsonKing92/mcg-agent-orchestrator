using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    internal sealed record GoalCancelCommand(string GoalSelector, IReadOnlyList<string> Parts);

    internal static GoalCancelCommand PrepareGoalCancelCommand(IReadOnlyList<string> parts)
    {
        CliArgumentParser.RequirePartCount(parts, 3,
            "cancel-goal <goal-id-prefix> <reason> [--confirm-goal-stop] | cancel-goal <goal-id-prefix> --text-file <path> [--confirm-goal-stop]");
        return new GoalCancelCommand(parts[1], parts);
    }

    internal static bool IsStopCancelAlias(IReadOnlyList<string> parts) =>
        parts.Count > 0 &&
        parts[0].Equals("stop", StringComparison.OrdinalIgnoreCase) &&
        GetFlagValue(parts, "--as")?.Equals("cancel", StringComparison.OrdinalIgnoreCase) == true;

    internal static GoalCancelCommand PrepareGoalCancelCommandFromStopAlias(IReadOnlyList<string> parts)
    {
        CliArgumentParser.RequirePartCount(parts, 3, CliCommandHelp.StopUsage["Usage: ".Length..]);
        var reason = ResolveTextArgument(parts, 2, CliCommandHelp.StopUsage["Usage: ".Length..], "--text-file");
        var delegated = BuildStopDelegateParts("cancel-goal", parts[1], reason, parts, "--confirm-goal-stop");
        return PrepareGoalCancelCommand(delegated);
    }

    internal static GoalLifecycleTransitionOutcome ApplyGoalCancelWithoutRendering(
        GoalCancelCommand command,
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        GoalId? currentGoalId)
    {
        var goal = kernel.GetGoal(goalId);
        var currentGoal = currentGoalId == goalId ? goal : null;
        if (RequiresGoalStopConfirmation(currentGoal, goal) && !HasGoalStopConfirmation(command.Parts))
        {
            return new GoalLifecycleTransitionOutcome(GoalLifecycleTransitionDisposition.Rejected,
                goalId, goal.Status, goal,
                RejectionReason: "cancel-goal requires --confirm-goal-stop for active or non-current goals.");
        }

        var textParts = RemoveStandaloneFlag(command.Parts, "--confirm-goal-stop");
        var reason = ResolveTextArgument(textParts, inlineIndex: 2,
            "cancel-goal <goal-id-prefix> <reason> [--confirm-goal-stop] | cancel-goal <goal-id-prefix> --text-file <path> [--confirm-goal-stop]",
            "--text-file");
        var liveDispatches = CollectLiveDispatches(goal);
        _ = kernel.CancelGoal(goalId, reason, allowLiveDispatches: true);
        return new GoalLifecycleTransitionOutcome(
            liveDispatches.Length == 0
                ? GoalLifecycleTransitionDisposition.Applied
                : GoalLifecycleTransitionDisposition.AppliedWithLiveDispatches,
            goalId, goal.Status, goal, goal.Timeline[^1], LiveDispatches: liveDispatches);
    }

    internal static void RenderGoalCancelOutcome(
        GoalLifecycleTransitionOutcome outcome,
        OrchestratorWorkspace workspace,
        Action? deliverCommittedLifecycleEvent = null)
    {
        switch (outcome.Disposition)
        {
            case GoalLifecycleTransitionDisposition.Applied:
            case GoalLifecycleTransitionDisposition.AppliedWithLiveDispatches:
                ConsoleViews.PrintGoal(outcome.Goal!);
                if (outcome.Disposition == GoalLifecycleTransitionDisposition.AppliedWithLiveDispatches)
                {
                    Console.WriteLine($"Live dispatches still running: {outcome.LiveDispatches!.Count}");
                    WriteLiveDispatches(outcome.LiveDispatches);
                }

                if (deliverCommittedLifecycleEvent is null)
                    AppendCommittedLifecycleEvent(outcome, workspace, GoalStatus.Cancelled);
                else
                    deliverCommittedLifecycleEvent();
                return;

            case GoalLifecycleTransitionDisposition.Rejected:
                throw new InvalidOperationException(outcome.RejectionReason);

            case GoalLifecycleTransitionDisposition.ConflictExhausted:
                throw CreateConflictExhaustedException("cancel-goal", outcome.GoalId);

            default:
                throw new InvalidOperationException($"Unsupported cancel transition outcome: {outcome.Disposition}.");
        }
    }
}
