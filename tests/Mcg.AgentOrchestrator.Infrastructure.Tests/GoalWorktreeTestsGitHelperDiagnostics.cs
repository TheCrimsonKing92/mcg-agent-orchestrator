public sealed class GoalWorktreeTestsGitHelperDiagnostics : GoalWorktreeTestBase
{
    // Parallel-safe: every fact owns a GUID-named repository and cleans it up independently.
    [Xunit.Fact]
    public void RunGitFailureCarriesStdoutAndExitCode()
    {
        var repo = CreateEmptyRepository();
        try
        {
            var probeExit = RunGitExitCode(
                repo,
                ["commit", "-m", "Seed"],
                out var probeStdout,
                out var probeStderr);

            Assert.NotEqual(0, probeExit);
            Assert.Contains("nothing to commit", probeStdout, StringComparison.Ordinal);
            Assert.Empty(probeStderr);
            Assert.DoesNotContain("nothing to commit", probeStderr, StringComparison.Ordinal);

            var exception = Assert.Throws<InvalidOperationException>(
                () => RunGit(repo, "commit", "-m", "Seed"));

            Assert.Contains(probeStdout.Trim(), exception.Message, StringComparison.Ordinal);
            Assert.Contains($"exit={probeExit}", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void RunGitFailureCarriesStderrText()
    {
        var repo = CreateEmptyRepository();
        try
        {
            var probeExit = RunGitExitCode(
                repo,
                ["checkout", "no-such-branch"],
                out var probeStdout,
                out var probeStderr);

            Assert.NotEqual(0, probeExit);
            Assert.Empty(probeStdout);
            Assert.NotEmpty(probeStderr);

            var exception = Assert.Throws<InvalidOperationException>(
                () => RunGit(repo, "checkout", "no-such-branch"));

            Assert.Contains(probeStderr.Trim(), exception.Message, StringComparison.Ordinal);
            Assert.Contains($"exit={probeExit}", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static string CreateEmptyRepository()
    {
        var root = OperatingSystem.IsWindows()
            ? Path.Combine(FindCurrentSourceRoot(), ".scratch", "mcg-git-helper-diag")
            : Path.Combine(Path.GetTempPath(), "mcg-git-helper-diag");
        var repo = Path.Combine(root, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(repo);
        RunGit(repo, "init");
        RunGit(repo, "config", "user.email", "tests@example.com");
        RunGit(repo, "config", "user.name", "Worktree Tests");
        RunGit(repo, "config", "commit.gpgsign", "false");
        return repo;
    }
}
