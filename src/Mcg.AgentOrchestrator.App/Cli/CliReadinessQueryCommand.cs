using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliReadinessQueryCommand
{
    internal static bool Diagnose(IReadOnlyList<string> args, CliExecutionContext context)
    {
        if (args.Count > 2 || (args.Count == 2 &&
            (string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("-", StringComparison.Ordinal))))
            throw new ArgumentException("Usage: readiness [goal-id]");
        context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, args.Count > 1 ? args[1] : null);
        var readinessDiagnosis = TerminalGoalSweep.Diagnose(
            context.Kernel, context.Workspace.ExecutionDirectory, context.Workspace.IntegrationBranch, context.CurrentGoal.Id);
        ConsoleViews.PrintTerminalGoalSweep(readinessDiagnosis, includeRepairs: false);
        IReadOnlyCollection<Goal> diagnosisHoldScope = context.Kernel.Goals;
        if (!ConductLoopGoalStatus.IsTerminal(context.CurrentGoal.Status.ToString()) &&
            DispatchReadinessRules.HasAssignedDispatchCandidates(context.CurrentGoal))
        {
            var intentPath = Path.Combine(context.Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
            var actionableIds = File.Exists(intentPath)
                ? SqliteOperatorIntentStore.OpenExisting(context.Workspace.OrchestratorDirectory, context.Workspace.LogDirectory)
                    .ListActionableGoalIdsAsync().GetAwaiter().GetResult()
                : Array.Empty<string>();
            diagnosisHoldScope = context.ReloadKernel(actionableIds).Goals
                .Where(goal => goal.Id != context.CurrentGoal.Id)
                .Append(context.CurrentGoal).ToArray();
        }
        ConsoleViews.PrintGoalReadinessPreflight(GoalReadinessPreflight.Build(
            context.CurrentGoal,
            context.Agents,
            context.Workspace.ExecutionDirectory,
            context.WorkerProfiles,
            context.Worktrees.TryResolve,
            diagnosisHoldScope));
        return false;
    }

    // Bare readiness resolves the current goal from metadata. Invalid explicit
    // forms stay here so usage errors cannot drain the outbox.
    internal static bool IsReadinessQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        args[0].Equals("readiness", StringComparison.OrdinalIgnoreCase) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        if (args.Count > 2 || (args.Count == 2 &&
            (string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("-", StringComparison.Ordinal))))
            throw new ArgumentException("Usage: readiness [goal-id]");
        CliCommandHelp.ThrowIfInvalidFlags(args);

        GoalId goalId;
        if (args.Count == 1)
        {
            goalId = CliCurrentGoalSelector.Select(stateRepository, currentGoal?.Id);
        }
        else
        {
            var matches = stateRepository.ListGoalIdStatusesAsync().GetAwaiter().GetResult()
                .Where(goal => goal.Id.StartsWith(args[1], StringComparison.OrdinalIgnoreCase))
                .ToArray();
            goalId = matches.Length switch
            {
                1 => new GoalId(matches[0].Id),
                0 => throw new KeyNotFoundException($"Goal '{args[1]}' was not found."),
                _ => throw new InvalidOperationException($"Goal prefix '{args[1]}' is ambiguous.")
            };
        }
        var kernel = stateRepository.LoadGoalsAsync([goalId]).GetAwaiter().GetResult();
        var goal = kernel.Goals.SingleOrDefault(candidate => candidate.Id == goalId)
            ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
        var context = new CliExecutionContext(kernel, workspace, providers, agents, workerProfiles,
            goal, channel, reloadKernelForGoals: ids => CliPersistentStateRunner.LoadConductLoopKernel(
                stateRepository, ids, workspace.ExecutionDirectory)) { IsReadOnlyQuery = true };
        try
        {
            if (CliCommandHandlers.Execute(args, context))
                throw new InvalidOperationException("readiness attempted to mutate state through its read-only route.");
        }
        finally
        {
            agents = context.Agents;
            workerProfiles = context.WorkerProfiles;
            currentGoal = context.CurrentGoal;
        }
    }
}
