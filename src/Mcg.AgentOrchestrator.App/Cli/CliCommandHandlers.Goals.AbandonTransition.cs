using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    internal const string GoalAbandonUsage =
        "abandon-goal <goal-id-prefix> <reason> [--confirm-goal-abandon] | abandon-goal <goal-id-prefix> --text-file <path> [--confirm-goal-abandon]";

    internal sealed record GoalAbandonCommand(string GoalSelector, string Reason, bool Confirmed);

    internal static GoalAbandonCommand PrepareGoalAbandonCommand(IReadOnlyList<string> parts)
    {
        CliArgumentParser.RequirePartCount(parts, 3, GoalAbandonUsage);
        var textParts = RemoveStandaloneFlag(parts, "--confirm-goal-abandon");
        var reason = ResolveTextArgument(textParts, inlineIndex: 2, GoalAbandonUsage, "--text-file");
        return new GoalAbandonCommand(
            parts[1], reason,
            HasCliConfirmation(parts, "--confirm-goal-abandon") ||
                parts[2].Contains("--confirm-goal-abandon", StringComparison.OrdinalIgnoreCase));
    }

    internal static GoalLifecycleTransitionOutcome ApplyGoalAbandonWithoutRendering(
        GoalAbandonCommand command,
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        OrchestratorWorkspace workspace,
        GoalWorktreeCleanupHooks hooks)
    {
        var goal = kernel.GetGoal(goalId);
        var liveDispatches = CollectLiveDispatches(goal);
        var leaveRunning = liveDispatches.Length > 0;
        var plan = GoalAbandonPlanner.Build(kernel, goal, workspace, command.Reason, hooks,
            leaveLiveDispatchesRunning: leaveRunning);
        if (!command.Confirmed)
        {
            return new GoalLifecycleTransitionOutcome(GoalLifecycleTransitionDisposition.DryRun,
                goalId, goal.Status, goal, LiveDispatches: liveDispatches);
        }

        if (!plan.CanApply)
        {
            return new GoalLifecycleTransitionOutcome(GoalLifecycleTransitionDisposition.Rejected,
                goalId, goal.Status, goal, LiveDispatches: liveDispatches);
        }

        ProgressEvent? committedEvent = null;
        if (goal.Status is not (GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
        {
            _ = kernel.CancelGoal(goalId, plan.Reason, allowLiveDispatches: true);
            committedEvent = goal.Timeline[^1];
        }

        return new GoalLifecycleTransitionOutcome(
            leaveRunning ? GoalLifecycleTransitionDisposition.AppliedWithLiveDispatches : GoalLifecycleTransitionDisposition.Applied,
            goalId, goal.Status, goal, committedEvent, LiveDispatches: liveDispatches);
    }

    internal static void RenderGoalAbandonOutcome(
        GoalAbandonCommand command,
        GoalLifecycleTransitionOutcome outcome,
        AgentOrchestratorKernel? kernel,
        OrchestratorWorkspace workspace,
        GoalWorktreeCleanupHooks hooks,
        Action? deliverCommittedLifecycleEvent = null,
        Func<GoalAbandonPlan?>? deliverAfterCommitEffect = null)
    {
        if (outcome.Disposition == GoalLifecycleTransitionDisposition.ConflictExhausted)
        {
            throw CreateConflictExhaustedException("abandon-goal", outcome.GoalId);
        }

        if (outcome.Goal is null || kernel is null)
        {
            throw new InvalidOperationException("Abandon outcome is missing its committed goal state.");
        }

        var leaveRunning = outcome.LiveDispatches is { Count: > 0 };
        if (outcome.Disposition is GoalLifecycleTransitionDisposition.DryRun or GoalLifecycleTransitionDisposition.Rejected)
        {
            ConsoleViews.PrintGoalAbandonPlan(GoalAbandonPlanner.Build(kernel, outcome.Goal, workspace,
                command.Reason, hooks, leaveLiveDispatchesRunning: leaveRunning));
            if (outcome.Disposition == GoalLifecycleTransitionDisposition.Rejected)
            {
                throw new InvalidOperationException("abandon-goal could not apply because one or more steps are blocked.");
            }

            return;
        }

        if (outcome.CommittedTimelineEvent is not null)
        {
            if (deliverCommittedLifecycleEvent is null)
                AppendCommittedLifecycleEvent(outcome, workspace, GoalStatus.Cancelled);
            else
                deliverCommittedLifecycleEvent();
        }

        GoalAbandonPlan plan;
        if (leaveRunning)
        {
            if (deliverAfterCommitEffect is null)
                GoalAbandonPlanner.RecordAbandonedTerminalDisposition(outcome.Goal, workspace, command.Reason.Trim());
            else
                _ = deliverAfterCommitEffect();
            plan = GoalAbandonPlanner.Build(kernel, outcome.Goal, workspace, command.Reason, hooks,
                dryRun: false, leaveLiveDispatchesRunning: true);
        }
        else
        {
            plan = deliverAfterCommitEffect is null
                ? GoalAbandonPlanner.CompleteAfterCommit(kernel, outcome.Goal, workspace, command.Reason.Trim(), hooks)
                : deliverAfterCommitEffect() ?? GoalAbandonPlanner.Build(kernel, outcome.Goal, workspace, command.Reason, hooks, dryRun: false);
        }

        ConsoleViews.PrintGoalAbandonPlan(plan);
        if (leaveRunning)
        {
            Console.WriteLine($"Live dispatches still running: {outcome.LiveDispatches!.Count}");
            WriteLiveDispatches(outcome.LiveDispatches);
        }
    }
}
