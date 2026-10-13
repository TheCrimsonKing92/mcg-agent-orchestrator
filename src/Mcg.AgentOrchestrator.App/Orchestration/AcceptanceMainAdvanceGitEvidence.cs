using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Git evidence for deterministic carry is usable only after a successful, fully drained command.
internal static class AcceptanceMainAdvanceGitEvidence
{
    internal static bool IsAncestor(
        string workingDirectory,
        string oldRevision,
        string newRevision) =>
        GitSucceeded(GitCli.Run(
            workingDirectory,
            "merge-base",
            "--is-ancestor",
            oldRevision,
            newRevision));

    internal static IReadOnlyList<string>? ReadChangedPaths(
        string workingDirectory,
        string oldRevision,
        string newRevision)
    {
        var result = GitCli.Run(
            workingDirectory,
            "diff",
            "--name-only",
            "-z",
            "--no-renames",
            oldRevision,
            newRevision);
        return GitSucceeded(result)
            ? result.Output
                .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(RepositoryPathOverlap.Normalize)
                .Where(path => path.Length > 0)
                .ToArray()
            : null;
    }

    private static bool GitSucceeded(GitCli.GitResult result) =>
        result.Succeeded && !result.DrainTimedOut;
}
