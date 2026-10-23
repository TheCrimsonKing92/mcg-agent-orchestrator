using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

// Native admission needs committed state. The loop owns progress for its selected goal,
// using the same baseline-aware checkpoint as the other external-work callers.
internal static class CliRunGoalExecution
{
    internal static bool Execute(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog profiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel)
    {
        // Keep the full read scope for provider holds and cross-goal readiness checks;
        // persistence still owns only the selected goal.
        var kernel = repository.LoadAsync().GetAwaiter().GetResult();
        var selectedId = currentGoal?.Id;
        var selected = kernel.Goals.FirstOrDefault(goal => goal.Id == selectedId);
        currentGoal = OrchestratorEntityResolver.ResolveGoal(kernel, selected,
            CliGoalPrefixArguments.GetOptionalArgument(args, "--confirm-batch-start",
                "--confirm-large-paid-subscription-start", "--confirm-readiness-risk"));
        var checkpoint = new CliOwnedGoalCheckpoint(repository, kernel, currentGoal.Id);
        string? failure = null;
        var changed = CliCommandDispatcher.ExecuteCommand(
            args, kernel, workspace, ref agents, providers, ref profiles, ref currentGoal, channel,
            reloadKernel: () => repository.LoadAsync().GetAwaiter().GetResult(),
            persistKernel: loaded => checkpoint.Persist(loaded),
            registerPostCommitFailure: reason => failure = reason);

        if (changed) checkpoint.Persist(kernel);
        if (currentGoal is { } target) currentGoal = kernel.GetGoal(target.Id);
        // Registration/start failures carry durable progress. Surface the nonzero result
        // only after saving that evidence, just as the transactional caller did.
        if (failure is not null) throw new InvalidOperationException(failure);
        return changed;
    }
}
