using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static bool ExecuteGoalParkStopAliasTransition(IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository repository, OrchestratorWorkspace workspace, ref Goal? currentGoal)
    {
        var command = CliCommandHandlers.PrepareGoalParkCommandFromStopAlias(args);
        var goalId = ResolveSingleGoalCommandGoalId(repository, currentGoal?.Id.Value, command.GoalSelector);
        var snapshot = repository.LoadGoalAsync(goalId).GetAwaiter().GetResult()
            ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
        var preparation = RestoreGoalExactly(snapshot);
        if (!command.Confirmed)
        {
            currentGoal = preparation.GetGoal(goalId);
            CliCommandHandlers.RenderGoalParkStopAliasDryRun(command, currentGoal);
            return false;
        }

        // Validate parkability on an isolated kernel before any process is terminated.
        preparation.ParkGoal(goalId, command.Reason);
        var receipts = GoalWorkerTermination.Terminate(snapshot, "park");
        CliCommandHandlers.GoalLifecycleTransitionOutcome outcome;
        try
        {
            outcome = repository.TransactGoalStateAsync("cli:park-goal", goalId, (state, _) =>
            {
                if (state is null) throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
                var kernel = new AgentOrchestratorKernel();
                kernel.ReplaceGoalStateWithSnapshot(state.Goal, state.HumanInputRequests);
                GoalWorkerTermination.Replay(kernel, goalId, receipts);
                var applied = CliCommandHandlers.ApplyGoalParkWithoutRendering(command, kernel, goalId);
                return Task.FromResult((true, (GoalStateSnapshot?)ExportGoalStateSnapshot(kernel, goalId), applied));
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (receipts.Count > 0)
        {
            throw GoalWorkerTermination.DidNotCommit("park", receipts, ex);
        }
        catch (GoalTransactionConflictException)
        {
            throw CliCommandHandlers.CreateConflictExhaustedException("park-goal", goalId);
        }

        currentGoal = outcome.Goal;
        var resolved = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ResolveOpenForGoalAsync(goalId.Value, $"Goal parked: {command.Reason}").GetAwaiter().GetResult();
        Console.WriteLine($"Goal parked {goalId.Value[..8]}.");
        Console.WriteLine($"Cancelled running dispatches: {receipts.Count}");
        Console.WriteLine($"Resolved human waits: {outcome.ResolvedHumanWaits}");
        Console.WriteLine($"Resolved attention items: {resolved}");
        return true;
    }
}
