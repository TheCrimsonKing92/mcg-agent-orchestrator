using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed class CliExecutionContext(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    IModelProviderRegistry providers,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog workerProfiles,
    Goal? currentGoal,
    IOperatorChannel? channel = null,
    Func<AgentOrchestratorKernel>? reloadKernel = null,
    Action<AgentOrchestratorKernel>? persistKernel = null,
    Func<AcceptanceMergeCommitRequest, AcceptanceMergeCommitResult>? finalizeAcceptanceMerge = null,
    Action<AgentOrchestratorKernel, GoalId>? persistGoalKernel = null)
{
public AgentOrchestratorKernel Kernel { get; } = kernel;

public AgentOrchestratorKernel ReloadKernel() => reloadKernel?.Invoke() ?? Kernel;

/// <summary>
/// Durably commits the current kernel state mid-command. Long-running loops (conduct --loop/--watch)
/// run outside the single wrapping state transaction and call this per tick so each tick's progress
/// survives a reload or a killed process. No-op for ordinary commands (which commit on return).
/// </summary>
public void PersistCheckpoint(AgentOrchestratorKernel checkpointKernel) => persistKernel?.Invoke(checkpointKernel);

public void PersistGoalCheckpoint(AgentOrchestratorKernel checkpointKernel, GoalId changedGoalId) =>
    persistGoalKernel?.Invoke(checkpointKernel, changedGoalId);

public OrchestratorWorkspace Workspace { get; } = workspace;

public string AgentCatalogPath => Workspace.AgentCatalogPath;

public IModelProviderRegistry Providers { get; } = providers;

public string WorkerProfilePath => Workspace.WorkerProfilePath;

public IReadOnlyList<AgentDefinition> Agents { get; set; } = agents;

public WorkerProfileCatalog WorkerProfiles { get; set; } = workerProfiles;

public Goal? CurrentGoal { get; set; } = currentGoal;

public IGoalAcceptanceVerifier AcceptanceVerifier { get; init; } = new GoalAcceptanceVerifier();

public IGoalLifecycleEventWriter EventWriter { get; init; } = NullGoalLifecycleEventWriter.Instance;

public AcceptanceMergeCommitResult FinalizeAcceptanceMerge(AcceptanceMergeCommitRequest request) =>
    finalizeAcceptanceMerge?.Invoke(request) ?? request.Merge();

public IOperatorChannel Channel { get; } = channel ?? NullOperatorChannel.Instance;
}

internal sealed record AcceptanceMergeCommitRequest(
    GoalId GoalId,
    string ExpectedGoalFingerprint,
    string? TestedWorktreeHead,
    Func<AcceptanceMergeCommitResult> Merge);

internal sealed record AcceptanceMergeCommitResult(
    bool FastForwarded,
    string? Message);
