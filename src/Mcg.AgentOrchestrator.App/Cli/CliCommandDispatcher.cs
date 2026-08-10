using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

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
    Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalKernel = null,
    IGoalAcceptanceVerifier? acceptanceVerifier = null,
    Func<long>? goalMarkLandedElapsedMilliseconds = null,
    CliPhaseTimingRecorder? phaseTimings = null,
    TimeSpan? stableSlotAcquisitionTimeout = null,
    Func<TimeSpan?, Action<DotnetBuildStableSlotWait>?, DotnetBuildEnvironmentLease>? stableSlotSelector = null,
    Action? releaseConductLoopLease = null,
    Action? reacquireConductLoopLease = null,
    Func<AgentOrchestratorKernel>? reloadResolvedParkedHumanWaitKernel = null,
    Func<AgentOrchestratorKernel>? reloadParkedGoalSafetyNetKernel = null,
    Action<OrchestratorStateOutboxMessage>? registerStateOutboxMessage = null,
    Func<IReadOnlyCollection<string>, AgentOrchestratorKernel>? reloadKernelForGoals = null,
    Action<string>? registerPostCommitFailure = null,
    TextReader? standardInput = null,
    bool? isStandardInputRedirected = null,
    Action? registerAcceptanceGuardAbort = null,
    Func<AcceptanceMergeGuardPreflightRequest, AcceptanceMergeGuardPreflightResult>? prepareAcceptanceMergeGuard = null,
    Action<Goal>? finalizeGoalCreation = null,
    IGoalLifecycleEventWriter? eventWriter = null,
    CollaborationItemRaise? refinementCollaborationItemRaise = null)
{
    eventWriter ??= new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, kernel: kernel);
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
        persistGoalKernel,
        phaseTimings: phaseTimings,
        releaseConductLoopLease: releaseConductLoopLease,
        reacquireConductLoopLease: reacquireConductLoopLease,
        reloadResolvedParkedHumanWaitKernel: reloadResolvedParkedHumanWaitKernel,
        reloadParkedGoalSafetyNetKernel: reloadParkedGoalSafetyNetKernel,
        registerStateOutboxMessage: registerStateOutboxMessage,
        reloadKernelForGoals: reloadKernelForGoals,
        registerPostCommitFailure: registerPostCommitFailure,
        standardInput: standardInput,
        isStandardInputRedirected: isStandardInputRedirected,
        registerAcceptanceGuardAbort: registerAcceptanceGuardAbort,
        prepareAcceptanceMergeGuard: prepareAcceptanceMergeGuard,
        finalizeGoalCreation: finalizeGoalCreation,
        refinementCollaborationItemRaise: refinementCollaborationItemRaise)
    {
        EventWriter = eventWriter,
        AcceptanceVerifier = acceptanceVerifier ?? new GoalAcceptanceVerifier(),
        GoalMarkLandedElapsedMilliseconds = goalMarkLandedElapsedMilliseconds,
        StableSlotAcquisitionTimeout = stableSlotAcquisitionTimeout,
        StableSlotSelector = stableSlotSelector
    };
    var changed = CliCommandHandlers.Execute(parts, context);
    agents = context.Agents;
    workerProfiles = context.WorkerProfiles;
    currentGoal = context.CurrentGoal;
    return changed;
}
}
