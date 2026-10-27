using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliAskGoalCommand
{
    internal static void ValidateStartupArguments(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        if (!CliArgumentParser.IsAskGoal(args)) return;
        var invocation = CliArgumentParser.ParseAskGoalArgs(args);
        var kernel = File.Exists(workspace.SqliteStatePath)
            ? SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath).LoadAsync().GetAwaiter().GetResult()
            : new AgentOrchestratorKernel();
        _ = CliArgumentParser.ResolveAskGoalTarget(invocation, kernel, OrchestratorEntityResolver.GetLatestGoal(kernel));
    }

    internal static bool Execute(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel)
    {
        if (CliCommandHelp.TryPrintStartupHelp(args)) return false;
        var invocation = CliArgumentParser.ParseAskGoalArgs(args);
        var snapshot = stateRepository.LoadAsync().GetAwaiter().GetResult();
        var currentGoalId = currentGoal?.Id;
        var selectedGoal = currentGoalId is null ? null : snapshot.Goals.SingleOrDefault(goal => goal.Id == currentGoalId);
        _ = CliArgumentParser.ResolveAskGoalTarget(invocation, snapshot, selectedGoal);

        var nextAgents = agents;
        var nextProfiles = workerProfiles;
        Goal? nextCurrentGoal = currentGoal;
        // Ask only after validation, without draining unrelated outboxes. The transaction
        // resolves the selector again so the earlier read cannot authorize a stale target.
        var changed = stateRepository.TransactAsync((kernel, _) =>
        {
            var commandGoal = currentGoalId is null ? null : kernel.Goals.SingleOrDefault(goal => goal.Id == currentGoalId);
            var shouldSave = CliCommandDispatcher.ExecuteCommand(args, kernel, workspace,
                ref nextAgents, providers, ref nextProfiles, ref commandGoal, channel);
            nextCurrentGoal = commandGoal;
            return Task.FromResult((shouldSave, shouldSave));
        }).GetAwaiter().GetResult();
        agents = nextAgents;
        workerProfiles = nextProfiles;
        currentGoal = nextCurrentGoal;
        return changed;
    }
}
