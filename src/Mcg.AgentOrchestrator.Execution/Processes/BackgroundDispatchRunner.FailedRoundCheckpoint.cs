using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private static FailedRoundCheckpointReceipt? TryBuildFailedRoundReceipt(
        TaskSpec task, string dispatchId, GoalWorktreeDispatchEvidence evidence,
        bool committed, bool checkpointed, bool lowIntegrityRefused, bool wasCancelled)
    {
        // Final failure is checked in ApplyRefreshOutcome, after wrapper reconciliation.
        if (task.RequiredRole != AgentRole.Developer || committed || checkpointed ||
            lowIntegrityRefused || wasCancelled || evidence.CommitsAfterDispatch != 0 ||
            evidence.IsClean || evidence.DirtyPaths.Count == 0)
            return null;
        return new(dispatchId, evidence.DirtyStateHash, evidence.DirtyPaths.ToArray());
    }

    private static string? TryGetDirtyStateHash(string workingDirectory)
    {
        try
        {
            if (!Directory.Exists(workingDirectory))
                return null;

            var result = GitCli.Run(workingDirectory, "status", "--porcelain=v1", "--untracked-files=all");
            if (!result.Succeeded)
                return null;

            return DispatchWorktreeCommitter.ComputeDirtyStateHash(result.Output);
        }
        catch
        {
            return null;
        }
    }
}
