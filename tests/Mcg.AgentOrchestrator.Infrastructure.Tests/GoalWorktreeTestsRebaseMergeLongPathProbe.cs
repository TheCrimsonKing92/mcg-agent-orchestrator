using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeTestsRebaseMergeLongPathProbe : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void RawRebaseSucceedsFromPaddedFixtureRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paddedTop = Path.Combine(SeedRepositoryProcessRootPath, "lp" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var repo = CreateSeededRepository(paddedTop);
            var goalId = GoalId.New();
            var unpaddedWorktree = Path.Combine(repo, ".orchestrator-worktrees", goalId.Value[..8]);
            var unpaddedLength = Path.GetFullPath(unpaddedWorktree).Length;
            var paddingLength = Math.Max(0, 200 - unpaddedLength - 1);
            if (paddingLength > 0)
            {
                var seedContainer = Path.GetDirectoryName(repo)!;
                var paddedRoot = Path.Combine(paddedTop, new string('x', paddingLength));
                Directory.CreateDirectory(paddedRoot);
                var movedContainer = Path.Combine(paddedRoot, Path.GetFileName(seedContainer));
                Directory.Move(seedContainer, movedContainer);
                repo = Path.Combine(movedContainer, "repo");
            }

            var expectedWorktree = Path.Combine(repo, ".orchestrator-worktrees", goalId.Value[..8]);
            var expectedLength = Path.GetFullPath(expectedWorktree).Length;
            Assert.True(expectedLength >= 190,
                $"Rebase directory length was {expectedLength}; unpadded length was {unpaddedLength}.");
            if (unpaddedLength <= 215)
            {
                Assert.True(expectedLength <= 215,
                    $"Rebase directory length was {expectedLength}; unpadded length was {unpaddedLength}.");
            }
            if (paddingLength > 0)
            {
                Assert.True(expectedLength == 200,
                    $"Expected 200 characters after padding, got {expectedLength}; unpadded length was {unpaddedLength}.");
            }

            RunGit(repo, "config", "core.autocrlf", "true");
            Assert.Equal(0, RunGitExitCode(repo, ["branch", "--show-current"], out var baseBranch, out _));

            var worktree = GoalWorktrees.Ensure(repo, goalId);
            Assert.Equal(expectedLength, Path.GetFullPath(worktree).Length);

            File.WriteAllBytes(Path.Combine(worktree, "fixture.txt"), Encoding.UTF8.GetBytes("alpha\nbeta\n"));
            RunGit(worktree, "add", "fixture.txt");
            RunGit(worktree, "commit", "-m", "Add normalized fixture");
            File.WriteAllText(Path.Combine(worktree, ".gitattributes"), "fixture.txt -text\n");
            RunGit(worktree, "add", ".gitattributes");
            RunGit(worktree, "commit", "-m", "Preserve fixture bytes");

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
            RunGit(repo, "add", "main-advanced.txt");
            RunGit(repo, "commit", "-m", "Advance main");
            Assert.Equal(0, RunGitExitCode(repo, ["rev-parse", "HEAD"], out var advancedHead, out _));

            RunGit(worktree, "rebase", "--merge", "--no-stat", baseBranch.Trim());
            Assert.Equal(0, RunGitExitCode(worktree, "merge-base", "--is-ancestor", advancedHead.Trim(), "HEAD"));
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(paddedTop);
        }
    }
}
