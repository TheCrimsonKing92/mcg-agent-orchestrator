using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static bool ExecuteGoalAbandonStopAliasTransition(IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository repository, OrchestratorWorkspace workspace, ref Goal? currentGoal)
    {
        var command = CliCommandHandlers.PrepareGoalAbandonCommandFromStopAlias(args);
        var goalId = ResolveSingleGoalCommandGoalId(repository, currentGoal?.Id.Value, command.GoalSelector);
        var snapshot = repository.LoadGoalAsync(goalId).GetAwaiter().GetResult()
            ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
        var preparation = RestoreGoalExactly(snapshot);
        var goal = preparation.GetGoal(goalId);
        var hooks = WorktreeCleanupContext.Load(attentionStoreDirectory: workspace.OrchestratorDirectory).Hooks;
        var before = GoalAbandonPlanner.Build(preparation, goal, workspace, command.Reason, hooks);
        if (!command.Confirmed || !before.CanApply)
        {
            currentGoal = goal;
            ConsoleViews.PrintGoalAbandonPlan(before);
            if (command.Confirmed)
                throw new InvalidOperationException("abandon-goal could not apply because one or more steps are blocked.");
            return false;
        }

        var receipts = GoalWorkerTermination.Terminate(snapshot, "abandon");
        CliCommandHandlers.GoalLifecycleTransitionOutcome outcome;
        AgentOrchestratorKernel? committed;
        try
        {
            outcome = TransactGoalSnapshotTransition(repository, "cli:abandon-goal", goalId, kernel =>
            {
                var updated = kernel.GetGoal(goalId);
                // A concurrent writer may have made the original plan unsafe. Recheck before
                // recording state, while keeping termination and cleanup outside retry bodies.
                if (!GoalAbandonPlanner.Build(kernel, updated, workspace, command.Reason, hooks).CanApply)
                    throw new InvalidOperationException("abandon-goal could not apply because one or more steps are blocked.");
                GoalWorkerTermination.Replay(kernel, goalId, receipts);
                ProgressEvent? committedEvent = null;
                if (updated.Status is not (GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
                {
                    kernel.CancelGoal(goalId, before.Reason);
                    committedEvent = updated.Timeline[^1];
                }
                return new(CliCommandHandlers.GoalLifecycleTransitionDisposition.Applied,
                    goalId, updated.Status, updated, committedEvent);
            }, out committed);
            if (outcome.Disposition == CliCommandHandlers.GoalLifecycleTransitionDisposition.ConflictExhausted)
                throw CliCommandHandlers.CreateConflictExhaustedException("abandon-goal", goalId);
        }
        catch (Exception ex) when (receipts.Count > 0)
        {
            throw GoalWorkerTermination.DidNotCommit("abandon", receipts, ex);
        }

        currentGoal = outcome.Goal;
        if (outcome.CommittedTimelineEvent is not null)
            CliCommandHandlers.AppendCommittedLifecycleEvent(outcome, workspace, GoalStatus.Cancelled);
        var plan = GoalAbandonPlanner.CompleteAfterCommit(committed!, outcome.Goal!, workspace, before.Reason, hooks);
        ConsoleViews.PrintGoalAbandonPlan(plan);
        return true;
    }
}
