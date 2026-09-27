using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeTestsRebaseMergeMaterializationStatClean : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void RewritesStatCleanCrlfBytesAfterSplitAttributeCommit()
    {
        var repo = CreateSeededRepository();
        try
        {
            RunGit(repo, "config", "core.autocrlf", "true");
            var baseBranch = RunGitOutput(repo, "branch", "--show-current");
            var goalId = GoalId.New();
            var worktree = GoalWorktrees.Ensure(repo, goalId);
            var fixturePath = Path.Combine(worktree, "fixture.txt");

            File.WriteAllBytes(fixturePath, Encoding.UTF8.GetBytes("alpha\r\nbeta\r\n"));
            RunGit(worktree, "add", "fixture.txt");
            RunGit(worktree, "commit", "-m", "Add normalized fixture");

            File.SetLastWriteTimeUtc(fixturePath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            RunGit(worktree, "update-index", "--refresh");
            File.WriteAllText(Path.Combine(worktree, ".gitattributes"), "fixture.txt -text\n");
            RunGit(worktree, "add", ".gitattributes");
            RunGit(worktree, "commit", "-m", "Preserve fixture bytes");

            var committedBlob = RunGitOutput(worktree, "rev-parse", "HEAD:fixture.txt");
            var workingBlob = RunGitOutput(worktree, "hash-object", "--no-filters", "--", "fixture.txt");
            Assert.Equal(string.Empty, RunGitOutput(worktree, "status", "--porcelain=v1", "--untracked-files=all"));
            Assert.NotEqual(committedBlob, workingBlob);

            var result = GoalWorktrees.ValidatePostRebaseMaterialization(
                worktree,
                GoalWorktrees.BranchName(goalId),
                baseBranch,
                goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
            Assert.Equal(["fixture.txt"], result.RematerializedFiles);
            Assert.Equal(committedBlob, RunGitOutput(worktree, "hash-object", "--no-filters", "--", "fixture.txt"));
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(fixturePath));
            Assert.Equal(string.Empty, RunGitOutput(worktree, "status", "--porcelain=v1", "--untracked-files=all"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
