using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Uses real git processes in an isolated repository per case.
[Xunit.Collection("IsolatedProcessSpawningRemainderB")]
public sealed class CandidateIdentityProjectHomeTests
{
    private const string ChangedPath = "src/Example/Program.cs";
    private const string Manifest = """
        {
          "version": 1,
          "engine": { "enforceStructuralCoverage": false },
          "checks": [
            { "name": "project check", "type": "command", "command": "dotnet", "arguments": ["--info"] }
          ],
          "forbiddenChangedPathGlobs": []
        }
        """;

    [Xunit.Fact]
    public void HomeOnlyManifest_DefaultComputation_ProducesCandidateIdentity()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var worktree = Path.Combine(root, "worktree");
            var projectHome = Path.Combine(root, "project-home");
            CreateRepository(worktree, includeManifest: false);
            Write(projectHome, "acceptance-manifest.json", Manifest);
            Xunit.Assert.True(File.Exists(Path.Combine(projectHome, "acceptance-manifest.json")));
            Xunit.Assert.False(File.Exists(Path.Combine(worktree, "config", "acceptance-manifest.json")));
            Xunit.Assert.False(File.Exists(Path.Combine(worktree, ".orchestrator", "acceptance-manifest.json")));

            Xunit.Assert.False(GoalWorktrees.TryComputeCandidateIdentity(
                worktree, "main", out var withoutHome, out var missingHomeFailure));
            Xunit.Assert.Null(withoutHome);
            Xunit.Assert.StartsWith("candidate-computation-failed:MissingAcceptanceManifestException:",
                missingHomeFailure);

            var succeeded = GoalWorktrees.TryComputeCandidateIdentity(
                worktree, "main", out var identity, out var failure, projectHomeDirectory: projectHome);

            Xunit.Assert.True(succeeded, failure);
            Xunit.Assert.Empty(failure);
            var candidate = Xunit.Assert.IsType<CandidateIdentity>(identity);
            Xunit.Assert.False(string.IsNullOrWhiteSpace(candidate.ManifestIdentity));
            Xunit.Assert.Equal(GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                worktree, [ChangedPath], projectHome), candidate.ManifestIdentity);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact]
    public void WorktreeManifest_OmittedAndNullHome_ProduceEqualIdentities()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var worktree = Path.Combine(root, "worktree");
            CreateRepository(worktree, includeManifest: true);
            Xunit.Assert.True(File.Exists(Path.Combine(worktree, "config", "acceptance-manifest.json")));

            var omittedSucceeded = GoalWorktrees.TryComputeCandidateIdentity(
                worktree, "main", out var omitted, out var omittedFailure);
            var nullSucceeded = GoalWorktrees.TryComputeCandidateIdentity(
                worktree, "main", out var nullHome, out var nullFailure, projectHomeDirectory: null);

            Xunit.Assert.True(omittedSucceeded, omittedFailure);
            Xunit.Assert.True(nullSucceeded, nullFailure);
            var candidate = Xunit.Assert.IsType<CandidateIdentity>(omitted);
            Xunit.Assert.IsType<CandidateIdentity>(nullHome);
            Xunit.Assert.Equal(candidate, nullHome);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static void CreateRepository(string worktree, bool includeManifest)
    {
        Directory.CreateDirectory(worktree);
        Git(worktree, "init", "-b", "main");
        Git(worktree, "config", "user.email", "candidate-tests@example.com");
        Git(worktree, "config", "user.name", "Candidate Tests");
        Write(worktree, "src/Example/Example.csproj", "<Project />\n");
        Write(worktree, ChangedPath, "class Program { static int Value = 1; }\n");
        if (includeManifest)
            Write(worktree, "config/acceptance-manifest.json", Manifest);
        Git(worktree, "add", "-A");
        Git(worktree, "commit", "-m", "Seed");
        Git(worktree, "switch", "-c", "feature");
        Write(worktree, ChangedPath, "class Program { static int Value = 2; }\n");
        Git(worktree, "add", "-A");
        Git(worktree, "commit", "-m", "Change value");
    }

    private static void Write(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static void Git(string root, params string[] arguments)
    {
        var result = GitCli.Run(root, 10_000, arguments);
        Xunit.Assert.True(result.Succeeded && !result.DrainTimedOut && result.ProcessStarted, result.Error);
    }
}
