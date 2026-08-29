public sealed class GoalWorktreeTestsGitHelperDiagnostics : GoalWorktreeTestBase
{
    public static bool IsWindows => OperatingSystem.IsWindows();

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

    [Xunit.Fact]
    public void GitCommitPostconditionRequiresANewCommittedCleanHead()
    {
        var repo = CreateEmptyRepository();
        try
        {
            var previousHead = TryGetGitHead(repo);
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
            RunGit(repo, "add", "seed.txt");
            RunGit(repo, "commit", "-m", "Seed");

            Assert.True(HasNewCommittedCleanGitHead(repo, previousHead));

            var committedHead = TryGetGitHead(repo);
            Assert.False(HasNewCommittedCleanGitHead(repo, committedHead));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(Skip = "Requires Windows owned-file capture semantics.", SkipUnless = nameof(IsWindows))]
    public void RunGitProbeLinkedWorktreeDoesNotDirtyTheWorktree()
    {
        var repo = CreateEmptyRepository();
        var linkedWorktree = repo + "-linked";
        try
        {
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
            RunGit(repo, "add", "seed.txt");
            RunGit(repo, "commit", "-m", "Seed");
            RunGit(repo, "worktree", "add", "-b", "probe", linkedWorktree, "HEAD");

            var result = InfrastructureTestSupport.RunGitProbe(linkedWorktree, ["status", "--short"]);

            Assert.True(result.Succeeded, result.StandardError);
            Assert.Empty(result.StandardOutput);
            Assert.Empty(Directory.EnumerateFiles(
                linkedWorktree,
                ".mcg-git-probe-*",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteDirectory(linkedWorktree);
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(Skip = "Requires Windows owned-file capture semantics.", SkipUnless = nameof(IsWindows))]
    public void RunGitProbeKeepsLiveCapturesOutsideTheRepositoryTree()
    {
        var repo = CreateEmptyRepository();
        try
        {
            var repositoryPrefix = Path.GetFullPath(repo)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var callbackObserved = false;
            string? observedStandardOutputPath = null;
            string? observedStandardErrorPath = null;
            var standardOutputCaptureExisted = false;
            var standardErrorCaptureExisted = false;
            string[] repositoryCapturePaths = [];
            var result = InfrastructureTestSupport.RunGitProbe(
                repo,
                ["status", "--short"],
                beforeOwnedCaptureRead: (standardOutputPath, standardErrorPath) =>
                {
                    callbackObserved = true;
                    observedStandardOutputPath = standardOutputPath;
                    observedStandardErrorPath = standardErrorPath;
                    standardOutputCaptureExisted = File.Exists(standardOutputPath);
                    standardErrorCaptureExisted = File.Exists(standardErrorPath);
                    repositoryCapturePaths = Directory.GetFiles(
                        repo,
                        ".mcg-git-probe-*",
                        SearchOption.AllDirectories);
                });

            Assert.True(callbackObserved);
            Assert.Equal(
                WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification.Success,
                result.Classification);
            Assert.True(result.Succeeded, result.StandardError);
            Assert.True(standardOutputCaptureExisted);
            Assert.True(standardErrorCaptureExisted);
            Assert.NotNull(observedStandardOutputPath);
            Assert.NotNull(observedStandardErrorPath);
            Assert.False(
                Path.GetFullPath(observedStandardOutputPath).StartsWith(
                    repositoryPrefix,
                    StringComparison.OrdinalIgnoreCase),
                $"stdout capture was created inside the repository: {observedStandardOutputPath}");
            Assert.False(
                Path.GetFullPath(observedStandardErrorPath).StartsWith(
                    repositoryPrefix,
                    StringComparison.OrdinalIgnoreCase),
                $"stderr capture was created inside the repository: {observedStandardErrorPath}");
            Assert.Empty(repositoryCapturePaths);
            Assert.Empty(result.StandardOutput);
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
