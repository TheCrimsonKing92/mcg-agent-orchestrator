using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class MainIntegrationSquash
{
    internal static string? TryCreate(
        string root, string baseRevision, string mergeBase, string candidateRevision,
        GoalId goalId, Func<string[], GitCli.GitResult> runGit)
    {
        if (!GoalWorktrees.HasMergeCommitsOutsideBase(root, mergeBase, candidateRevision)) return null;

        var resolvedTree = GitCli.Run(root, "rev-parse", "--verify", $"{candidateRevision}^{{tree}}");
        if (!resolvedTree.Succeeded || resolvedTree.DrainTimedOut ||
            string.IsNullOrWhiteSpace(resolvedTree.Output)) return null;
        var tree = resolvedTree.Output.Trim();
        var message = GoalWorktrees.ComposeSquashCommitMessage(
            root, baseRevision, mergeBase, candidateRevision, tree, goalId);
        var commit = runGit([
            "-c", "user.name=mcg-orchestrator", "-c", "user.email=mcg-orchestrator@localhost",
            "commit-tree", tree, "-p", mergeBase, "-m", message]);
        var revision = commit.Output.Trim();
        return commit.Succeeded && !commit.DrainTimedOut && revision.Length is 40 or 64 &&
            revision.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? revision : null;
    }
}
