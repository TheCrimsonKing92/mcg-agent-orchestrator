using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    internal sealed record GoalSupersedeCommand(string GoalSelector, IReadOnlyList<string> Parts);

    internal static GoalSupersedeCommand PrepareGoalSupersedeCommand(IReadOnlyList<string> parts)
    {
        CliArgumentParser.RequirePartCount(parts, 3, CliCommandHelp.SupersedeGoalUsage["Usage: ".Length..]);
        return new GoalSupersedeCommand(parts[1], parts);
    }

    internal static bool IsStopSupersedeAlias(IReadOnlyList<string> parts) =>
        parts.Count > 0 &&
        parts[0].Equals("stop", StringComparison.OrdinalIgnoreCase) &&
        GetFlagValue(parts, "--as")?.Equals("supersede", StringComparison.OrdinalIgnoreCase) == true;

    internal static GoalSupersedeCommand PrepareGoalSupersedeCommandFromStopAlias(IReadOnlyList<string> parts)
    {
        CliArgumentParser.RequirePartCount(parts, 3, CliCommandHelp.StopUsage["Usage: ".Length..]);
        var reason = ResolveTextArgument(parts, 2, CliCommandHelp.StopUsage["Usage: ".Length..], "--text-file");
        var delegated = BuildStopDelegateParts("supersede-goal", parts[1], reason, parts, "--confirm-goal-stop");
        return PrepareGoalSupersedeCommand(delegated);
    }

    internal static GoalLifecycleTransitionOutcome ApplyGoalSupersedeWithoutRendering(
        GoalSupersedeCommand command,
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
                RejectionReason: "supersede-goal requires --confirm-goal-stop for active or non-current goals.");
        }

        var textParts = RemoveStandaloneFlag(command.Parts, "--confirm-goal-stop");
        var reason = ResolveTextArgument(textParts, inlineIndex: 2,
            CliCommandHelp.SupersedeGoalUsage["Usage: ".Length..], "--text-file");
        var liveDispatches = CollectLiveDispatches(goal);
        _ = kernel.SupersedeGoal(goalId, reason, allowLiveDispatches: true);
        return new GoalLifecycleTransitionOutcome(
            liveDispatches.Length == 0
                ? GoalLifecycleTransitionDisposition.Applied
                : GoalLifecycleTransitionDisposition.AppliedWithLiveDispatches,
            goalId, goal.Status, goal, goal.Timeline[^1], LiveDispatches: liveDispatches);
    }

    internal static void RenderGoalSupersedeOutcome(
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
                    AppendCommittedLifecycleEvent(outcome, workspace, GoalStatus.Superseded);
                else
                    deliverCommittedLifecycleEvent();
                return;

            case GoalLifecycleTransitionDisposition.Rejected:
                throw new InvalidOperationException(outcome.RejectionReason);

            case GoalLifecycleTransitionDisposition.ConflictExhausted:
                throw CreateConflictExhaustedException("supersede-goal", outcome.GoalId);

            default:
                throw new InvalidOperationException($"Unsupported supersede transition outcome: {outcome.Disposition}.");
        }
    }
}
