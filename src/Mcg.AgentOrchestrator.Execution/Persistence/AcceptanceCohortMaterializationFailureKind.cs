namespace Mcg.AgentOrchestrator.Infrastructure;

public enum AcceptanceCohortMaterializationFailureKind
{
    StaleBinding,
    MergeConflict,
    WorkspaceFailure,
    ManifestUnavailable
}
