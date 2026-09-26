using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierOwnerResultsRootTests
{
    [Fact]
    public async Task AttemptsUseTheCandidateRepository()
    {
        var root = NewRoot();
        var owned = Path.Combine(root, "owned-repo");
        var unrelated = Path.Combine(root, "unrelated-repo");
        Directory.CreateDirectory(Path.Combine(owned, ".git"));
        Directory.CreateDirectory(Path.Combine(unrelated, ".git"));
        var original = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT", unrelated);
            await CreateBothOwners(owned, "feedfacefeedfacefeedfacefeedface");

            Assert.True(Directory.Exists(Path.Combine(owned, ".orchestrator", "acceptance-gate-attempts", "feedfacefeedfacefeedfacefeedface")));
            Assert.True(Directory.Exists(Path.Combine(owned, ".orchestrator", "pre-review-evidence-attempts", "feedfacefeedfacefeedfacefeedface")));
            Assert.False(Directory.Exists(Path.Combine(unrelated, ".orchestrator", "acceptance-gate-attempts")));
            Assert.False(Directory.Exists(Path.Combine(unrelated, ".orchestrator", "pre-review-evidence-attempts")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT", original);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ManifestOnlyFixtureUsesOwnedTempFallback()
    {
        var root = NewRoot();
        var fixture = Path.Combine(root, "manifest-only");
        var unrelated = Path.Combine(root, "unrelated-repo");
        Directory.CreateDirectory(fixture);
        Directory.CreateDirectory(Path.Combine(unrelated, ".git"));
        var original = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT", unrelated);
            await CreateBothOwners(fixture, "deadbeefdeadbeefdeadbeefdeadbeef");

            var fallback = Path.Combine(Path.GetTempPath(), "mcg-acceptance-owner-results", ".orchestrator");
            Assert.True(Directory.Exists(Path.Combine(fallback, "acceptance-gate-attempts", "deadbeefdeadbeefdeadbeefdeadbeef")));
            Assert.True(Directory.Exists(Path.Combine(fallback, "pre-review-evidence-attempts", "deadbeefdeadbeefdeadbeefdeadbeef")));
            Assert.False(Directory.Exists(Path.Combine(unrelated, ".orchestrator", "acceptance-gate-attempts")));
            Assert.False(Directory.Exists(Path.Combine(unrelated, ".orchestrator", "pre-review-evidence-attempts")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT", original);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("worktree")]
    [InlineData("unset")]
    public async Task GoalWorktreeKeepsItsMainRepositoryAttemptRoots(string configuredPath)
    {
        var root = NewRoot();
        var repository = Path.Combine(root, "repo");
        var worktree = Path.Combine(repository, ".orchestrator-worktrees", "abc12345");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(worktree);
        var original = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT", configuredPath switch
            {
                "repository" => repository,
                "worktree" => worktree,
                _ => null
            });
            await CreateBothOwners(worktree, "abc12345abc12345abc12345abc12345");

            Assert.True(Directory.Exists(Path.Combine(repository, ".orchestrator", "acceptance-gate-attempts", "abc12345abc12345abc12345abc12345")));
            Assert.True(Directory.Exists(Path.Combine(repository, ".orchestrator", "pre-review-evidence-attempts", "abc12345abc12345abc12345abc12345")));
            Assert.False(Directory.Exists(Path.Combine(worktree, ".orchestrator")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT", original);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OutOfTreeLinkedWorktreeKeepsConfiguredMainRepository()
    {
        var root = NewRoot();
        var repository = Path.Combine(root, "repo");
        var worktree = Path.Combine(root, "elsewhere", "linked");
        Directory.CreateDirectory(Path.Combine(repository, ".git", "worktrees", "linked"));
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"),
            $"gitdir: {Path.Combine(repository, ".git", "worktrees", "linked")}");
        var original = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT", repository);
            await CreateBothOwners(worktree, "abcdabcdabcdabcdabcdabcdabcdabcd");
            Assert.True(Directory.Exists(Path.Combine(repository, ".orchestrator", "acceptance-gate-attempts", "abcdabcdabcdabcdabcdabcdabcdabcd")));
            Assert.True(Directory.Exists(Path.Combine(repository, ".orchestrator", "pre-review-evidence-attempts", "abcdabcdabcdabcdabcdabcdabcdabcd")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT", original);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CreateBothOwners(string path, string id)
    {
        await using var gate = AcceptanceExecutionOwners.CreateAttempt(path, new GoalId(id));
        await using var preReview = AcceptanceExecutionOwners.CreateFocusedVerification(path, new GoalId(id));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-owner-root-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
