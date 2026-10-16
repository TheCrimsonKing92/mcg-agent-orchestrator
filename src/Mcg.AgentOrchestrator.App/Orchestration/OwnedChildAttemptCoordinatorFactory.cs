using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Production attempt coordinators carry the workspace's state identity into child metadata.
internal static class OwnedChildAttemptCoordinatorFactory
{
    internal static ConductorParallelAcceptanceAttemptCoordinator CreateGate(
        OrchestratorWorkspace workspace, DotnetBuildStorageRoot? buildStorageRoot,
        ConductorParallelAcceptanceTryRunPreSlot tryRunPreSlot, bool runInline) =>
        new(
            Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
            workspace.IntegrationBranch, workspace.ExecutionDirectory,
            tryRunPreSlot: tryRunPreSlot,
            runInline: runInline,
            buildStorageRoot: buildStorageRoot,
            stateDirectory: workspace.OrchestratorDirectory,
            projectName: OwnedChildWorkspaceResolver.RecordedProjectName(workspace));

    internal static ConductorParallelAcceptanceAttemptCoordinator CreateFocusedEvidence(
        OrchestratorWorkspace workspace, DotnetBuildStorageRoot? buildStorageRoot) =>
        new(
            Path.Combine(workspace.OrchestratorDirectory, "pre-review-evidence-attempts"),
            workspace.IntegrationBranch, workspace.ExecutionDirectory,
            conductEventLogWriter: new ConductEventLogWriter(workspace.ConductEventsLogPath),
            buildStorageRoot: buildStorageRoot,
            stateDirectory: workspace.OrchestratorDirectory,
            projectName: OwnedChildWorkspaceResolver.RecordedProjectName(workspace));
}
