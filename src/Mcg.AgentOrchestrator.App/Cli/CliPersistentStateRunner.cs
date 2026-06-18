using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliPersistentStateRunner
{
    public static bool ExecuteCommand(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        if (IsModelOutcomesScorecard(args))
        {
            var records = stateRepository.BuildModelOutcomeScorecardAsync().GetAwaiter().GetResult();
            ConsoleViews.PrintModelOutcomeScorecard(records);
            return false;
        }

        if (IsMetadataOnlyListing(args))
        {
            var summaries = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            ConsoleViews.PrintGoals(summaries);
            return false;
        }

        if (args.Count > 0 && !ShouldRunInStateTransaction(args[0]))
        {
            return ExecuteCommandWithoutTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        var nextAgents = agents;
        var nextWorkerProfiles = workerProfiles;
        var currentGoalId = currentGoal?.Id.Value;
        Goal? nextCurrentGoal = currentGoal;

        var changed = stateRepository.TransactAsync(
                (kernel, _) =>
                {
                    var sweptCount = new BackgroundDispatchRunner().SweepExitedProcesses(kernel);

                    var commandAgents = nextAgents;
                    var commandProfiles = nextWorkerProfiles;
                    var commandGoal = ResolveCurrentGoal(kernel, currentGoalId);
                    var shouldSave = CliCommandDispatcher.ExecuteCommand(
                        args,
                        kernel,
                        workspace,
                        ref commandAgents,
                        providers,
                        ref commandProfiles,
                        ref commandGoal,
                        channel);

                    nextAgents = commandAgents;
                    nextWorkerProfiles = commandProfiles;
                    nextCurrentGoal = commandGoal;
                    var anySave = shouldSave || sweptCount > 0;
                    return Task.FromResult((anySave, anySave));
                })
            .GetAwaiter()
            .GetResult();

        agents = nextAgents;
        workerProfiles = nextWorkerProfiles;
        currentGoal = nextCurrentGoal;
        return changed;
    }

    // Read-only goal listing: served from indexed metadata (no whole-store hydration, no save, no
    // auto-reconcile sweep). Only the bare `goals` command qualifies — any extra args fall through
    // to the normal transactional path so future `goals <filter>` forms keep working.
    internal static bool IsMetadataOnlyListing(IReadOnlyList<string> args)
    {
        return args.Count == 1 && args[0].Equals("goals", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsModelOutcomesScorecard(IReadOnlyList<string> args)
    {
        return args.Count == 1 && args[0].Equals("model-outcomes", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SkipsKernelState(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return false;

        return args[0].Equals("operator-listen", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("operator-channel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRunInStateTransaction(string command)
    {
        return command.ToLowerInvariant() switch
        {
            "dashboard" or
            "serve-dashboard" or
            "hosted-dashboard" or
            "simple-hosted-dashboard" or
            "open-dashboard" or
            "monitor-goal" or
            "operator-channel" or
            "state-rollback" => false,
            _ => true
        };
    }

    private static bool ExecuteCommandWithoutTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);
        var shouldSave = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            channel);

        if (shouldSave)
        {
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();
        }

        return shouldSave;
    }

    private static Goal? ResolveCurrentGoal(AgentOrchestratorKernel kernel, string? currentGoalId)
    {
        if (!string.IsNullOrWhiteSpace(currentGoalId))
        {
            var match = kernel.Goals.FirstOrDefault(goal => goal.Id.Value.Equals(currentGoalId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return OrchestratorEntityResolver.GetLatestGoal(kernel);
    }
}
