using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class CandidateIdentityConfiguredTrunkTests
{
    [Xunit.Fact]
    public void MasterOnlyGoalWorktreeUsesMasterCommitAsMergeBase()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var worktree = Path.Combine(root, "wt");
        try
        {
            Git(root, "init", "-b", "master");
            Git(root, "config", "user.email", "candidate-tests@example.com");
            Git(root, "config", "user.name", "Candidate Tests");
            Write(root, "src/Example/Example.csproj",
                "<Project><ItemGroup><ProjectReference Include=\"../Dependency/Dependency.csproj\" /></ItemGroup></Project>\n");
            Write(root, "src/Example/Program.cs", "class Program { static int Value = 1; }\n");
            Write(root, "src/Dependency/Dependency.csproj", "<Project />\n");
            Write(root, "src/Dependency/Dependency.cs", "class Dependency { const int Value = 1; }\n");
            Write(root, "Directory.Build.props", "<Project />\n");
            Git(root, "add", "-A");
            Git(root, "commit", "-m", "Seed");
            var masterCommit = Git(root, "rev-parse", "refs/heads/master").Output.Trim();
            Xunit.Assert.NotEqual(0, GitCli.Run(root, "rev-parse", "--verify", "--quiet", "refs/heads/main").ExitCode);
            Git(root, "worktree", "add", worktree, "-b", $"goal/{Guid.NewGuid():N}", "master");
            Write(worktree, "src/Example/Program.cs", "class Program { static int Value = 2; }\n");
            Git(worktree, "add", "-A");
            Git(worktree, "commit", "-m", "Change value");

            Xunit.Assert.True(GoalWorktrees.TryResolveCandidateMergeBase(worktree, "master", out var mergeBase));
            Xunit.Assert.Equal(masterCommit, mergeBase);
            var succeeded = GoalWorktrees.TryComputeCandidateIdentity(worktree, "master",
                out var identity, out var failure, (_, _) => "manifest");
            Xunit.Assert.True(succeeded, failure);
            Xunit.Assert.IsType<CandidateIdentity>(identity);
            Xunit.Assert.False(GoalWorktrees.TryComputeCandidateIdentity(worktree, "main",
                out _, out var missingMain, (_, _) => "manifest"));
            Xunit.Assert.Equal("missing-merge-base", missingMain);
        }
        finally
        {
            if (Directory.Exists(worktree)) GitCli.Run(root, "worktree", "remove", "--force", worktree);
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static GitCli.GitResult Git(string root, params string[] arguments)
    {
        var result = GitCli.Run(root, 10_000, arguments);
        Xunit.Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        return result;
    }
}
