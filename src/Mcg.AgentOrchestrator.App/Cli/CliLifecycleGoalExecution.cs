using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Lifecycle commands span creation and external work. No state write transaction may
// enclose that work: native admission reads the committed graph on another connection.
internal static class CliLifecycleGoalExecution
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        (args[0].Equals("lifecycle-goal", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("lifecycle-simple-goal", StringComparison.OrdinalIgnoreCase));

    internal static bool Execute(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog profiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null,
        Func<AgentOrchestratorKernel, Action<AgentOrchestratorKernel>, CliExecutionContext>? createContext = null)
    {
        var kernel = repository.LoadAsync().GetAwaiter().GetResult();
        var baselines = kernel.ExportSnapshot().Goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
        // Reuse the existing scoped creation and baseline-aware progress owners. In
        // particular, do not use the generic outside-transaction raw SaveAsync path.
        var persist = CliCommandCheckpoint.Create(repository, workspace, kernel, baselines);
        var selectedId = currentGoal?.Id;
        var selected = kernel.Goals.FirstOrDefault(goal => goal.Id == selectedId);
        var cleanup = WorktreeCleanupContext.Load(attentionStoreDirectory: workspace.OrchestratorDirectory);
        var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory,
            kernel: kernel, integrationBranch: workspace.IntegrationBranch);
        kernel.SetEventWriter(writer);
        var context = createContext?.Invoke(kernel, persist) ?? new CliExecutionContext(
            kernel, workspace, providers, agents, profiles, selected, channel,
            reloadKernel: () => repository.LoadAsync().GetAwaiter().GetResult(), persistKernel: persist)
        {
            EventWriter = writer,
            CleanupContext = cleanup,
            AcceptanceVerifier = new GoalAcceptanceVerifier(
                cleanup.Hooks.BuildStorageRoot ?? DotnetBuildEnvironmentManager.CaptureStorageRoot(),
                workspace.OrchestratorDirectory) { CrosswalkExecutable = CrosswalkExecutableComposition.ForProcess }
        };
        try
        {
            var changed = CliCommandHandlers.Execute(args, context);
            if (changed) persist(context.Kernel);
            return changed;
        }
        finally
        {
            agents = context.Agents;
            profiles = context.WorkerProfiles;
            currentGoal = context.CurrentGoal is { } target ? context.Kernel.GetGoal(target.Id) : null;
        }
    }
}
