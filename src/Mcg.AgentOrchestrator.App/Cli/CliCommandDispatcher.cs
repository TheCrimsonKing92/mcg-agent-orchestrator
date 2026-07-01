using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliCommandDispatcher
{
public static bool ExecuteCommand(
    IReadOnlyList<string> parts,
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    ref IReadOnlyList<AgentDefinition> agents,
    IModelProviderRegistry providers,
    ref WorkerProfileCatalog workerProfiles,
    ref Goal? currentGoal,
    IOperatorChannel? channel = null,
    Func<AgentOrchestratorKernel>? reloadKernel = null,
    Action<AgentOrchestratorKernel>? persistKernel = null,
    Func<AcceptanceMergeCommitRequest, AcceptanceMergeCommitResult>? finalizeAcceptanceMerge = null,
    Action<AgentOrchestratorKernel, GoalId>? persistGoalKernel = null,
    IGoalAcceptanceVerifier? acceptanceVerifier = null,
    Func<long>? goalMarkLandedElapsedMilliseconds = null)
{
    var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
    kernel.SetEventWriter(eventWriter);
    var context = new CliExecutionContext(
        kernel,
        workspace,
        providers,
        agents,
        workerProfiles,
        currentGoal,
        channel,
        reloadKernel,
        persistKernel,
        finalizeAcceptanceMerge,
        persistGoalKernel)
    {
        EventWriter = eventWriter,
        AcceptanceVerifier = acceptanceVerifier ?? new GoalAcceptanceVerifier(),
        GoalMarkLandedElapsedMilliseconds = goalMarkLandedElapsedMilliseconds
    };
    var changed = CliCommandHandlers.Execute(parts, context);
    agents = context.Agents;
    workerProfiles = context.WorkerProfiles;
    currentGoal = context.CurrentGoal;
    return changed;
}
}
