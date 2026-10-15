namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private static (string? Commit, string? Provenance) CaptureResultCommit(
        string workingDirectory, bool buildEvidenceAllowsResultCommit, bool isCheckpoint,
        bool hasCommittedChanges, bool orchestratorCommitted)
    {
        // A result SHA is completion credit, so require certified build evidence first.
        var commit = !buildEvidenceAllowsResultCommit || isCheckpoint ? null : TryGetWorktreeHead(workingDirectory);
        var provenance = commit is not null && hasCommittedChanges
            ? orchestratorCommitted ? "orchestrator" : "worker"
            : null;
        return (commit, provenance);
    }
}
