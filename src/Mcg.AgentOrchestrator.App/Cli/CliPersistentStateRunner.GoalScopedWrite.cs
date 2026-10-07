using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    internal static bool IsGoalScopedWriteCommand(IReadOnlyList<string> args) =>
        args.Count >= 2 && !args[1].StartsWith('-') &&
        (args[0].Equals("goal-depends", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("revise", StringComparison.OrdinalIgnoreCase) && !HasFlag(args, "--history"));

    private static bool TryExecuteGoalScopedWrite(
        IReadOnlyList<string> args,
        IOrchestratorStateOutboxRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel,
        out bool changed)
    {
        changed = false;
        GoalCreationLoadScope scope;
        var currentGoalId = currentGoal?.Id.Value;
        try
        {
            var target = ResolveSingleGoalCommandGoalId(stateRepository, currentGoalId, args[1]);
            if (args[0].Equals("revise", StringComparison.OrdinalIgnoreCase))
            {
                scope = GoalCreationLoadScope.ForGoals([target], []);
            }
            else
            {
                var on = HasFlag(args, "--on");
                var remove = HasFlag(args, "--remove");
                var clear = HasFlag(args, "--clear");
                if ((on ? 1 : 0) + (remove ? 1 : 0) + (clear ? 1 : 0) != 1)
                    return false;
                if (clear)
                {
                    scope = GoalCreationLoadScope.ForGoals([target], []);
                }
                else
                {
                    var dependencyPrefix = GetFlagValue(args, on ? "--on" : "--remove");
                    if (string.IsNullOrWhiteSpace(dependencyPrefix) || dependencyPrefix.StartsWith('-'))
                        return false;
                    var dependency = ResolveSingleGoalCommandGoalId(stateRepository, null, dependencyPrefix);
                    scope = GoalCreationLoadScope.ForGoals([target, dependency], on ? [dependency] : []);
                }
            }
        }
        catch (KeyNotFoundException) { return false; }
        catch (InvalidOperationException) { return false; }

        var nextAgents = agents;
        var nextWorkerProfiles = workerProfiles;
        var nextCurrentGoal = currentGoal;
        string? postCommitFailure = null;
        IReadOnlyList<GoalId> pendingRefinementGoalIds = [];
        changed = stateRepository.TransactGoalCreationWithOutboxAsync(scope, (kernel, _) =>
        {
            var loadedGoalIds = kernel.Goals.Select(goal => goal.Id).ToArray();
            var existingGoalIds = loadedGoalIds.ToHashSet();
            var outboxMessages = new List<OrchestratorStateOutboxMessage>();
            var commandAgents = nextAgents;
            var commandProfiles = nextWorkerProfiles;
            var commandGoal = kernel.Goals.FirstOrDefault(goal =>
                goal.Id.Value.Equals(currentGoalId, StringComparison.OrdinalIgnoreCase));
            var shouldSave = CliCommandDispatcher.ExecuteCommand(
                args,
                kernel,
                workspace,
                ref commandAgents,
                providers,
                ref commandProfiles,
                ref commandGoal,
                channel,
                () => stateRepository.LoadGoalsAsync(loadedGoalIds).GetAwaiter().GetResult(),
                registerStateOutboxMessage: outboxMessages.Add,
                registerPostCommitFailure: failure => postCommitFailure ??= failure);

            nextAgents = commandAgents;
            nextWorkerProfiles = commandProfiles;
            nextCurrentGoal = commandGoal ?? nextCurrentGoal;
            pendingRefinementGoalIds = kernel.Goals
                .Where(goal => !existingGoalIds.Contains(goal.Id))
                .Where(GoalRefinementWorkCoordinator.HasPendingWork)
                .Select(goal => goal.Id)
                .ToArray();
            foreach (var goalId in pendingRefinementGoalIds)
            {
                var messageId = GoalRefinementWorkCoordinator.MessageId(goalId);
                if (outboxMessages.All(message => !message.Id.Equals(messageId, StringComparison.Ordinal)))
                    outboxMessages.Add(GoalRefinementWorkCoordinator.CreateMessage(goalId));
            }
            var mustCommit = shouldSave || postCommitFailure is not null;
            return Task.FromResult((mustCommit, mustCommit,
                (IReadOnlyList<OrchestratorStateOutboxMessage>)outboxMessages));
        }).GetAwaiter().GetResult();

        agents = nextAgents;
        workerProfiles = nextWorkerProfiles;
        currentGoal = nextCurrentGoal;
        if (postCommitFailure is not null)
            throw new InvalidOperationException(postCommitFailure);
        foreach (var goalId in pendingRefinementGoalIds)
            _ = GoalRefinementWorkCoordinator.TryLaunch(workspace, goalId);
        return true;
    }
}
