namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    private static string PeelRetightenCommits(string executionDirectory, string revision)
    {
        if (string.IsNullOrWhiteSpace(executionDirectory) || !Directory.Exists(executionDirectory) ||
            !TryResolveCommit(executionDirectory, revision, out var head)) return revision;
        for (var count = 0; count < PatchEquivalenceCommitLimit; count++)
        {
            if (!TryReadGit(executionDirectory, ["log", "-1", "--format=%s", head], out var subject) ||
                subject != SourceSizeRatchetRetightener.CommitSubject ||
                !TryReadGit(executionDirectory, ["rev-list", "--parents", "-n", "1", head], out var parentLine)) break;
            var parents = parentLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length != 2 || !CommitShaPattern.IsMatch(parents[1]) ||
                !TryReadGit(executionDirectory, ["diff", "--name-only", "--no-renames", parents[1], head], out var paths) ||
                paths != SourceSizeRatchet.SourcePath) break;
            var before = GitCli.Run(executionDirectory, "show", $"{parents[1]}:{SourceSizeRatchet.SourcePath}");
            var after = GitCli.Run(executionDirectory, "show", $"{head}:{SourceSizeRatchet.SourcePath}");
            // A subject is not proof: reject unrelated text, comments, lowered values and other files.
            if (!before.Succeeded || before.DrainTimedOut || !after.Succeeded || after.DrainTimedOut ||
                !SourceSizeRatchetRetightener.IsNumericRetightenOnly(before.Output, after.Output)) break;
            head = parents[1];
        }
        return head;
    }
}
