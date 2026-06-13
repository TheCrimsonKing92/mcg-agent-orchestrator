using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliPersistentStateRunner
{
    public static bool ExecuteCommand(
        IReadOnlyList<string> args,
        string statePath,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        if (args.Count > 0 && !ShouldRunInStateTransaction(args[0]))
        {
            return ExecuteCommandWithoutTransaction(args, statePath, workspace, ref agents, providers, ref workerProfiles, ref currentGoal);
        }

        var nextAgents = agents;
        var nextWorkerProfiles = workerProfiles;
        var currentGoalId = currentGoal?.Id.Value;
        Goal? nextCurrentGoal = currentGoal;

        var changed = OrchestratorStateStore.TransactAsync(
                statePath,
                (kernel, _) =>
                {
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
                        ref commandGoal);

                    nextAgents = commandAgents;
                    nextWorkerProfiles = commandProfiles;
                    nextCurrentGoal = commandGoal;
                    return Task.FromResult((shouldSave, shouldSave));
                })
            .GetAwaiter()
            .GetResult();

        agents = nextAgents;
        workerProfiles = nextWorkerProfiles;
        currentGoal = nextCurrentGoal;
        return changed;
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
            "state-rollback" => false,
            _ => true
        };
    }

    private static bool ExecuteCommandWithoutTransaction(
        IReadOnlyList<string> args,
        string statePath,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        var kernel = OrchestratorStateStore.Load(statePath);
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);
        var shouldSave = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal);

        if (shouldSave)
        {
            OrchestratorStateStore.Save(statePath, kernel);
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
