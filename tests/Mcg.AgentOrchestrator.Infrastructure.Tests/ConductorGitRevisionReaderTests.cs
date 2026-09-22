using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorGitRevisionReaderTests
{
    [Xunit.Fact(DisplayName = "ConductorGitRevisionReader_ReadRequiredCommit_failure_carries_git_exit_code_and_error_text")]
    public void ReadRequiredCommitFailureCarriesGitExitCodeAndErrorText()
    {
        var repo = CreateSeededRepository();
        try
        {
            const string missingReference = "refs/heads/reference-that-does-not-exist";

            // Capture what THIS git binary and locale actually print for the identical command, so the
            // assertion pins the forwarding contract rather than a wording that varies by git version.
            var expected = GitCli.Run(repo, "rev-parse", "--verify", missingReference);
            Assert.False(expected.Succeeded);
            Assert.False(
                string.IsNullOrWhiteSpace(expected.Error),
                "git printed no stderr for a missing reference, so the message check would pass vacuously.");

            var thrown = Assert.Throws<InvalidOperationException>(
                () => ConductorGitRevisionReader.ReadRequiredCommit(repo, missingReference));

            Assert.Contains(missingReference, thrown.Message, StringComparison.Ordinal);
            Assert.Contains($"{expected.ExitCode}", thrown.Message, StringComparison.Ordinal);
            Assert.Contains(expected.Error.Trim(), thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static string CreateSeededRepository()
    {
        var repo = Path.Combine(Path.GetTempPath(), "mcg-revision-reader-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(repo);
        RunGit(repo, "init", "-q");
        RunGit(repo, "config", "user.email", "tests@example.com");
        RunGit(repo, "config", "user.name", "Conductor Git Revision Reader Tests");
        RunGit(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
        RunGit(repo, "add", "seed.txt");
        RunGit(repo, "commit", "-q", "-m", "Seed");
        return repo;
    }

    private static void RunGit(string repo, params string[] arguments)
    {
        var result = GitCli.Run(repo, arguments);
        Assert.True(
            result.Succeeded,
            $"git {string.Join(' ', arguments)} failed: exit={result.ExitCode}; stderr={result.Error}");
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort: a leaked temp repository must not fail the fact.
        }
    }
}
