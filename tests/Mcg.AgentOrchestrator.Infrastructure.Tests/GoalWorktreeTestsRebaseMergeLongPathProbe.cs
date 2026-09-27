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
        var paddedRoot = paddedTop;
        while (Path.GetFullPath(paddedRoot).Length < 150)
        {
            paddedRoot = Path.Combine(paddedRoot, new string('x', 40));
        }

        try
        {
            Directory.CreateDirectory(paddedRoot);
            var repo = CreateSeededRepository(paddedRoot);
            RunGit(repo, "config", "core.autocrlf", "true");
            Assert.Equal(0, RunGitExitCode(repo, ["branch", "--show-current"], out var baseBranch, out _));

            var worktree = GoalWorktrees.Ensure(repo, GoalId.New());
            Assert.True(Path.GetFullPath(worktree).Length >= 190, $"Rebase directory length was {Path.GetFullPath(worktree).Length}.");

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
