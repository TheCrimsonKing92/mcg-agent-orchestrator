using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    internal static bool IsStopParkAlias(IReadOnlyList<string> parts) => IsStopDisposition(parts, "park");
    internal static bool IsStopAbandonAlias(IReadOnlyList<string> parts) => IsStopDisposition(parts, "abandon");

    private static bool IsStopDisposition(IReadOnlyList<string> parts, string disposition) =>
        parts.Count > 0 && parts[0].Equals("stop", StringComparison.OrdinalIgnoreCase) &&
        GetFlagValue(parts, "--as")?.Equals(disposition, StringComparison.OrdinalIgnoreCase) == true;

    private static IReadOnlyList<string> PrepareStopDispositionParts(IReadOnlyList<string> parts, string disposition)
    {
        var usage = CliCommandHelp.StopUsage["Usage: ".Length..];
        CliArgumentParser.RequirePartCount(parts, 3, usage);
        var reason = ResolveTextArgument(parts, 2, usage, "--text-file");
        return BuildStopDelegateParts($"{disposition}-goal", parts[1], reason, parts, $"--confirm-goal-{disposition}");
    }

    internal static GoalParkCommand PrepareGoalParkCommandFromStopAlias(IReadOnlyList<string> parts) =>
        PrepareGoalParkCommand(PrepareStopDispositionParts(parts, "park"));

    internal static GoalAbandonCommand PrepareGoalAbandonCommandFromStopAlias(IReadOnlyList<string> parts) =>
        PrepareGoalAbandonCommand(PrepareStopDispositionParts(parts, "abandon"));

    // Validate on an isolated kernel before performing process I/O.
    internal static void ValidateGoalParkStopAlias(GoalParkCommand command,
        AgentOrchestratorKernel preparation, GoalId goalId) => preparation.ParkGoal(goalId, command.Reason);

    internal static GoalLifecycleTransitionOutcome ApplyGoalParkStopAliasWithoutRendering(
        GoalParkCommand command, AgentOrchestratorKernel kernel, GoalId goalId,
        IReadOnlyList<GoalWorkerTerminationReceipt> receipts)
    {
        GoalWorkerTermination.Replay(kernel, goalId, receipts);
        return ApplyGoalParkWithoutRendering(command, kernel, goalId);
    }

    internal static void RenderGoalParkStopAliasOutcome(GoalParkCommand command,
        GoalLifecycleTransitionOutcome outcome, int cancelledCount, OrchestratorWorkspace workspace)
    {
        var resolved = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ResolveOpenForGoalAsync(outcome.GoalId.Value, $"Goal parked: {command.Reason}").GetAwaiter().GetResult();
        Console.WriteLine($"Goal parked {outcome.GoalId.Value[..8]}.");
        Console.WriteLine($"Cancelled running dispatches: {cancelledCount}");
        Console.WriteLine($"Resolved human waits: {outcome.ResolvedHumanWaits}");
        Console.WriteLine($"Resolved attention items: {resolved}");
    }

    internal static GoalAbandonPlan PlanGoalAbandonStopAlias(GoalAbandonCommand command,
        AgentOrchestratorKernel preparation, Goal goal, OrchestratorWorkspace workspace,
        GoalWorktreeCleanupHooks hooks) =>
        GoalAbandonPlanner.Build(preparation, goal, workspace, command.Reason, hooks);

    internal static void RenderGoalAbandonStopAliasPlan(GoalAbandonCommand command, GoalAbandonPlan plan)
    {
        ConsoleViews.PrintGoalAbandonPlan(plan);
        if (command.Confirmed)
            throw new InvalidOperationException("abandon-goal could not apply because one or more steps are blocked.");
    }

    internal static GoalLifecycleTransitionOutcome ApplyGoalAbandonStopAliasWithoutRendering(
        GoalAbandonCommand command, AgentOrchestratorKernel kernel, GoalId goalId,
        OrchestratorWorkspace workspace, GoalWorktreeCleanupHooks hooks, GoalAbandonPlan before,
        IReadOnlyList<GoalWorkerTerminationReceipt> receipts)
    {
        var updated = kernel.GetGoal(goalId);
        // Recheck against the latest state; replay is state-only and safe inside transaction retries.
        if (!PlanGoalAbandonStopAlias(command, kernel, updated, workspace, hooks).CanApply)
            throw new InvalidOperationException("abandon-goal could not apply because one or more steps are blocked.");
        GoalWorkerTermination.Replay(kernel, goalId, receipts);
        ProgressEvent? committedEvent = null;
        if (updated.Status is not (GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
        {
            kernel.CancelGoal(goalId, before.Reason);
            committedEvent = updated.Timeline[^1];
        }
        return new(GoalLifecycleTransitionDisposition.Applied, goalId, updated.Status, updated, committedEvent);
    }

    internal static void RenderGoalAbandonStopAliasOutcome(GoalLifecycleTransitionOutcome outcome,
        AgentOrchestratorKernel committed, OrchestratorWorkspace workspace, string reason,
        GoalWorktreeCleanupHooks hooks)
    {
        if (outcome.CommittedTimelineEvent is not null)
            AppendCommittedLifecycleEvent(outcome, workspace, GoalStatus.Cancelled);
        var plan = GoalAbandonPlanner.CompleteAfterCommit(committed, outcome.Goal!, workspace, reason, hooks);
        ConsoleViews.PrintGoalAbandonPlan(plan);
    }

    private static GoalSnapshot CaptureGoalSnapshot(AgentOrchestratorKernel kernel, GoalId goalId) =>
        kernel.ExportSnapshot().Goals.Single(goal => goal.Id == goalId.Value);

    private static bool ExecuteGoalParkStopAliasInMemory(CliExecutionContext context, GoalParkCommand command)
    {
        var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, command.GoalSelector);
        if (!command.Confirmed)
        {
            context.CurrentGoal = goal;
            RenderGoalParkStopAliasDryRun(command, goal);
            return false;
        }

        var snapshot = CaptureGoalSnapshot(context.Kernel, goal.Id);
        var preparation = new AgentOrchestratorKernel();
        preparation.ReplaceGoalWithSnapshot(snapshot);
        ValidateGoalParkStopAlias(command, preparation, goal.Id);
        var receipts = GoalWorkerTermination.Terminate(snapshot, "park");
        GoalLifecycleTransitionOutcome outcome;
        try
        {
            outcome = ApplyGoalParkStopAliasWithoutRendering(command, context.Kernel, goal.Id, receipts);
        }
        catch (Exception ex) when (receipts.Count > 0)
        {
            throw GoalWorkerTermination.DidNotCommit("park", receipts, ex);
        }
        context.CurrentGoal = outcome.Goal;
        RenderGoalParkStopAliasOutcome(command, outcome, receipts.Count, context.Workspace);
        return outcome.ShouldSave;
    }

    private static bool ExecuteGoalAbandonStopAliasInMemory(CliExecutionContext context, GoalAbandonCommand command)
    {
        var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, command.GoalSelector);
        var hooks = context.CleanupContext.Hooks;
        var before = PlanGoalAbandonStopAlias(command, context.Kernel, goal, context.Workspace, hooks);
        if (!command.Confirmed || !before.CanApply)
        {
            context.CurrentGoal = goal;
            RenderGoalAbandonStopAliasPlan(command, before);
            return false;
        }

        var receipts = GoalWorkerTermination.Terminate(CaptureGoalSnapshot(context.Kernel, goal.Id), "abandon");
        GoalLifecycleTransitionOutcome outcome;
        try
        {
            outcome = ApplyGoalAbandonStopAliasWithoutRendering(command, context.Kernel, goal.Id,
                context.Workspace, hooks, before, receipts);
        }
        catch (Exception ex) when (receipts.Count > 0)
        {
            throw GoalWorkerTermination.DidNotCommit("abandon", receipts, ex);
        }
        context.CurrentGoal = outcome.Goal;
        RenderGoalAbandonStopAliasOutcome(outcome, context.Kernel, context.Workspace, before.Reason, hooks);
        return outcome.ShouldSave;
    }

    internal static void RenderGoalParkStopAliasDryRun(GoalParkCommand command, Goal goal)
    {
        Console.WriteLine($"Goal park dry run {goal.Id.Value[..8]}:");
        Console.WriteLine($"  reason: {command.Reason}");
        Console.WriteLine($"  running dispatches to cancel: {CollectLiveDispatches(goal).Length}");
        Console.WriteLine("  attention waits: resolve with park reason");
        Console.WriteLine($"  command: park-goal {goal.Id.Value[..8]} <reason> --confirm-goal-park");
    }
}
