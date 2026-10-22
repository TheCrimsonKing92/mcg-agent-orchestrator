using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Each test owns a unique repository and uses repository-local git configuration only.
public sealed class DispatchWorktreeCommitterMergeTests
{
    [Xunit.Fact]
    public void ResolvedMergeWithStagedDeletionAndRenameCommitsWithOrderedParents()
    {
        var repository = Path.Combine(Path.GetTempPath(), "mcg-committer-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        try
        {
            Git(repository, "init", "--initial-branch=main");
            Git(repository, "config", "--local", "core.autocrlf", "false");
            Git(repository, "config", "--local", "commit.gpgSign", "false");
            Git(repository, "config", "--local", "user.name", "Committer Merge Test");
            Git(repository, "config", "--local", "user.email", "committer-merge@test.invalid");
            Write(repository, "c.txt", "base\n");
            Write(repository, "d.txt", "removed on main\n");
            Write(repository, "e.txt", "renamed on main\n");
            Git(repository, "add", "-A");
            Git(repository, "commit", "-m", "Base");
            Git(repository, "branch", "goal");

            Write(repository, "c.txt", "main\n");
            Git(repository, "rm", "d.txt");
            Git(repository, "mv", "e.txt", "f.txt");
            Write(repository, "n.txt", "added on main\n");
            Git(repository, "add", "-A");
            Git(repository, "commit", "-m", "Main changes");
            var mainHead = Git(repository, "rev-parse", "HEAD").Trim();

            Git(repository, "checkout", "goal");
            Write(repository, "c.txt", "goal\n");
            Git(repository, "add", "c.txt");
            Git(repository, "commit", "-m", "Goal changes");
            var goalHead = Git(repository, "rev-parse", "HEAD").Trim();
            var merge = GitCli.Run(repository, "merge", "--no-ff", "--no-commit", "main");
            Xunit.Assert.True(merge.ProcessStarted && !merge.DrainTimedOut, merge.Error);
            Xunit.Assert.Equal(1, merge.ExitCode);
            Xunit.Assert.Equal(mainHead, Git(repository, "rev-parse", "MERGE_HEAD").Trim());

            const string resolvedText = "resolved main and goal\n";
            Write(repository, "c.txt", resolvedText);
            // Prove the resolution is still unmerged and the problematic paths are fully staged.
            var status = Git(repository, "status", "--short", "--untracked-files=all");
            var statusLines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Xunit.Assert.Contains("UU c.txt", statusLines);
            Xunit.Assert.Contains("D  d.txt", statusLines);
            Xunit.Assert.Contains("R  e.txt -> f.txt", statusLines);
            Xunit.Assert.Contains("A  n.txt", statusLines);
            var dirtyPaths = GitCli.ParseCommitWorthyStatusPaths(status);
            Xunit.Assert.Equal(new[] { "c.txt", "d.txt", "e.txt", "f.txt", "n.txt" },
                dirtyPaths.OrderBy(path => path, StringComparer.Ordinal));

            var result = new DispatchWorktreeCommitter().TryCommitWorktreeEdits(
                repository, "Resolved integration merge", dirtyPaths);

            Xunit.Assert.True(result.Succeeded, result.Diagnostic);
            Xunit.Assert.Equal(new[] { goalHead, mainHead },
                Git(repository, "show", "-s", "--format=%P", "HEAD").Trim().Split(' '));
            var mergeHead = GitCli.Run(repository, "rev-parse", "--verify", "--quiet", "MERGE_HEAD");
            Xunit.Assert.True(mergeHead.ProcessStarted && !mergeHead.DrainTimedOut, mergeHead.Error);
            Xunit.Assert.Equal(1, mergeHead.ExitCode);
            Xunit.Assert.False(File.Exists(Path.Combine(repository, ".git", "MERGE_HEAD")));
            Xunit.Assert.Equal("", Git(repository, "status", "--porcelain=v1", "--untracked-files=all"));
            Xunit.Assert.Equal(new[] { "c.txt", "f.txt", "n.txt" },
                Git(repository, "ls-tree", "-r", "--name-only", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Xunit.Assert.Equal(resolvedText, Git(repository, "show", "HEAD:c.txt"));
            Xunit.Assert.Equal("renamed on main\n", Git(repository, "show", "HEAD:f.txt"));
            Xunit.Assert.Equal("added on main\n", Git(repository, "show", "HEAD:n.txt"));
        }
        finally
        {
            // Git object files can be read-only on Windows; all files belong to this fixture.
            foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(repository, recursive: true);
        }
    }

    private static void Write(string repository, string path, string text) =>
        File.WriteAllText(Path.Combine(repository, path), text, new UTF8Encoding(false));

    private static string Git(string repository, params string[] arguments)
    {
        var result = GitCli.Run(repository, arguments);
        Xunit.Assert.True(result.Succeeded && result.ProcessStarted && !result.DrainTimedOut,
            $"git {string.Join(' ', arguments)}: {result.ExitCode}: {result.Error}");
        return result.Output;
    }
}
