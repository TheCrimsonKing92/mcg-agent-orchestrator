using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class CandidateIdentityComputationTests
{
    [Xunit.Fact]
    public void RebaseAcrossUnrelatedMainChangePreservesIdentityButPatchEditChangesIt()
    {
        var root = CreateRepository();
        try
        {
            Git(root, "switch", "-c", "feature");
            Write(root, "src/Example/Program.cs", "class Program { static int Value = 2; }\n");
            Git(root, "add", "-A");
            Git(root, "commit", "-m", "Change value");
            var original = Compute(root);

            Git(root, "switch", "main");
            Write(root, "docs/notes.md", "Unrelated change\n");
            Git(root, "add", "-A");
            Git(root, "commit", "-m", "Change documentation");
            Git(root, "switch", "feature");
            Git(root, "rebase", "main");
            var rebased = Compute(root);
            Xunit.Assert.Equal(original, rebased);

            Write(root, "src/Example/Program.cs", "class Program { static int Value = 3; }\n");
            Git(root, "add", "-A");
            Git(root, "commit", "-m", "Change patch content");
            Xunit.Assert.NotEqual(original, Compute(root));
        }
        finally { Delete(root); }
    }

    [Xunit.Fact]
    public void ChangedDependencyClosureChangesIdentity()
    {
        var root = CreateRepository();
        try
        {
            Git(root, "switch", "-c", "feature");
            Write(root, "src/Example/Program.cs", "class Program { static int Value = 2; }\n");
            Git(root, "add", "-A");
            Git(root, "commit", "-m", "Change value");
            var original = Compute(root);
            Git(root, "switch", "main");
            Write(root, "src/Dependency/Dependency.cs", "class Dependency { const int Value = 2; }\n");
            Git(root, "add", "-A");
            Git(root, "commit", "-m", "Change dependency");
            Git(root, "switch", "feature");
            Git(root, "rebase", "main");
            Xunit.Assert.NotEqual(original, Compute(root));
        }
        finally { Delete(root); }
    }

    private static Mcg.AgentOrchestrator.Core.CandidateIdentity Compute(string root)
    {
        var succeeded = GoalWorktrees.TryComputeCandidateIdentity(root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, out var identity, out var failure,
            (_, _) => "manifest");
        Xunit.Assert.True(succeeded, failure);
        return Xunit.Assert.IsType<Mcg.AgentOrchestrator.Core.CandidateIdentity>(identity);
    }

    private static string CreateRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-candidate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Git(root, "init", "-b", "main");
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
        return root;
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Git(string root, params string[] args)
    {
        var result = GitCli.Run(root, 10_000, args);
        Xunit.Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
    }

    private static void Delete(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
