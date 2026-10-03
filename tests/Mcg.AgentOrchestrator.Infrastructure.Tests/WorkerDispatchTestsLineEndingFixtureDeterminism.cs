// Parallel-safe: each test owns its seeded templates, repositories and linked worktrees.
public sealed class WorkerDispatchTestsLineEndingFixtureDeterminism : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void WriteSkill_SourceLfAndCrlf_ProduceIdenticalCleanSeedAndWorktreeStatus()
    {
        var owningRoot = Directory.CreateTempSubdirectory("mcg-dispatch-line-endings-").FullName;
        const string skillText = """
            ---
            name: aspnet-core
            description: Test skill fixture.
            ---

            # aspnet-core
            """;
        try
        {
            var lf = SeedAndInspect(skillText.ReplaceLineEndings("\n"));
            var crlf = SeedAndInspect(skillText.ReplaceLineEndings("\r\n"));
            Assert.Equal(lf.SeedStatus, crlf.SeedStatus);
            Assert.Equal(lf.WorktreeStatus, crlf.WorktreeStatus);
            Assert.Empty(lf.SeedStatus);
            Assert.Empty(lf.WorktreeStatus);
            Assert.DoesNotContain(" M .agents/skills/", lf.WorktreeStatus);
            Assert.DoesNotContain(" M .agents/skills/", crlf.WorktreeStatus);
        }
        finally
        {
            WorkerDispatchTestsSeededRepositoryFactory.DeleteOwnedDirectory(owningRoot);
        }

        (string SeedStatus, string WorktreeStatus) SeedAndInspect(string text)
        {
            var factory = new WorkerDispatchTestsSeededRepositoryFactory(
                () => Directory.CreateDirectory(Path.Combine(owningRoot, Guid.NewGuid().ToString("N"))).FullName,
                root => WriteSkill(root, "aspnet-core", text),
                owningRoot);
            var repository = factory.Create().PublishedIdentity.RepositoryPath;
            var seedStatus = Git(repository, "status", "--porcelain");
            var checkout = Path.Combine(owningRoot, Guid.NewGuid().ToString("N"));
            // Mirror Ensure's inherited hermetic autocrlf=true, then the fixture's
            // autocrlf-unset status probe. read-tree forces rehash without a timer.
            Git(repository, "-c", "core.autocrlf=true", "worktree", "add", "--detach", checkout, "HEAD");
            Git(checkout, "read-tree", "HEAD");
            return (seedStatus, Git(checkout, "status", "--porcelain"));
        }
    }

    private static string Git(string root, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(root, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} at '{root}': {result}");
        Assert.False(result.StandardOutputTruncated, $"Truncated git output at '{root}': {result}");
        return result.StandardOutput;
    }
}
