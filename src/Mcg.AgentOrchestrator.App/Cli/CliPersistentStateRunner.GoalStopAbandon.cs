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
        var before = CliCommandHandlers.PlanGoalAbandonStopAlias(command, preparation, goal, workspace, hooks);
        if (!command.Confirmed || !before.CanApply)
        {
            currentGoal = goal;
            CliCommandHandlers.RenderGoalAbandonStopAliasPlan(command, before);
            return false;
        }

        var receipts = GoalWorkerTermination.Terminate(snapshot, "abandon");
        CliCommandHandlers.GoalLifecycleTransitionOutcome outcome;
        AgentOrchestratorKernel? committed;
        try
        {
            outcome = TransactGoalSnapshotTransition(repository, "cli:abandon-goal", goalId, kernel =>
                CliCommandHandlers.ApplyGoalAbandonStopAliasWithoutRendering(
                    command, kernel, goalId, workspace, hooks, before, receipts), out committed);
            if (outcome.Disposition == CliCommandHandlers.GoalLifecycleTransitionDisposition.ConflictExhausted)
                throw CliCommandHandlers.CreateConflictExhaustedException("abandon-goal", goalId);
        }
        catch (Exception ex) when (receipts.Count > 0)
        {
            throw GoalWorkerTermination.DidNotCommit("abandon", receipts, ex);
        }

        currentGoal = outcome.Goal;
        CliCommandHandlers.RenderGoalAbandonStopAliasOutcome(outcome, committed!, workspace, before.Reason, hooks);
        return true;
    }
}
