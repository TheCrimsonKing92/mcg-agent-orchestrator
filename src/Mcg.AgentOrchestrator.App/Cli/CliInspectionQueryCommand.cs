using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliInspectionQueryCommand
{
    internal static bool IsInspectionQueryCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || CliCommandHelp.IsCommandSpecificHelp(args))
            return false;

        return args[0].ToLowerInvariant() switch
        {
            "goals" or "model-outcomes" or "architecture" or "backlog-view" => args.Count == 1,
            "config" => args.Count == 2 && args[1].ToLowerInvariant() is "agents" or "profiles" or "policy",
            _ => false
        };
    }

    internal static void Execute(
        IReadOnlyList<string> args,
        IOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles)
    {
        switch (args[0].ToLowerInvariant())
        {
            case "goals":
                ConsoleViews.PrintGoals(stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult());
                var debtHooks = WorktreeCleanupContext.Load(
                    attentionStoreDirectory: workspace.OrchestratorDirectory).Hooks;
                ConsoleViews.PrintCleanupDebtWarning(
                    GoalWorktrees.ListCleanupDebt(workspace.ExecutionDirectory, debtHooks));
                break;
            case "model-outcomes":
                ModelOutcomeBoundModels.Print(
                    stateRepository.BuildModelOutcomeScorecardAsync().GetAwaiter().GetResult(), workspace);
                break;
            default:
                Goal? commandGoal = null;
                CliReadOnlyQueryExecutor.Execute(args, new AgentOrchestratorKernel(), workspace,
                    providers, channel, ref agents, ref workerProfiles, ref commandGoal);
                break;
        }
    }
}
