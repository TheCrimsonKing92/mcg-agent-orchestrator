using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: Git only accesses each fixture's unique repository, never shared configuration or state.
public sealed class GitAuthorBriefDraftRepositoryTests
{
    [Fact]
    public void Trailing_separator_root_at_main_returns_main_sha()
    {
        using var fixture = new RepositoryFixture();
        var main = fixture.Git("rev-parse", "main");
        Assert.Equal(main, fixture.Git("rev-parse", "HEAD"));
        var repository = new GitAuthorBriefDraftRepository(fixture.Root + "\\");

        Assert.Equal(main, repository.ResolveMainHead());
    }

    [Fact]
    public void Trailing_separator_root_with_different_head_reports_head_not_main_only()
    {
        using var fixture = new RepositoryFixture();
        var first = fixture.Git("rev-parse", "HEAD");
        fixture.Git("commit", "-q", "--allow-empty", "-m", "Second");
        var main = fixture.Git("rev-parse", "main");
        Assert.NotEqual(first, main);
        fixture.Git("checkout", "-q", "--detach", first);
        Assert.Equal(first, fixture.Git("rev-parse", "HEAD"));
        var configuredRoot = fixture.Root + "\\";
        var repository = new GitAuthorBriefDraftRepository(configuredRoot);

        var exception = Assert.Throws<AuthorDraftRepositoryNotAtMainException>(() => repository.ResolveMainHead());
        Assert.Equal("head-not-main", exception.Condition);
        Assert.Equal(first, exception.Head);
        Assert.Equal(main, exception.Main);
        Assert.Equal(fixture.Root, exception.Toplevel);
        Assert.Equal(configuredRoot, exception.ConfiguredRoot);
        Assert.Contains(configuredRoot, exception.Message);
    }

    [Fact]
    public void Different_head_reports_both_shas_and_matching_main_returns_its_sha()
    {
        using var fixture = new RepositoryFixture();
        var first = fixture.Git("rev-parse", "HEAD");
        fixture.Git("commit", "-q", "--allow-empty", "-m", "Second");
        var main = fixture.Git("rev-parse", "main");
        Assert.NotEqual(first, main);
        fixture.Git("checkout", "-q", "--detach", first);
        Assert.Equal(first, fixture.Git("rev-parse", "HEAD"));
        var repository = new GitAuthorBriefDraftRepository(fixture.Root);

        var exception = Assert.Throws<AuthorDraftRepositoryNotAtMainException>(() => repository.ResolveMainHead());
        Assert.Equal("head-not-main", exception.Condition);
        Assert.Equal(first, exception.Head);
        Assert.Equal(main, exception.Main);
        Assert.Contains("head-not-main", exception.Message);
        Assert.Contains(first, exception.Message);
        Assert.Contains(main, exception.Message);
        Assert.Contains(fixture.Root, exception.Message);

        fixture.Git("checkout", "-q", "main");
        Assert.Equal(main, fixture.Git("rev-parse", "HEAD"));
        Assert.Equal(main, repository.ResolveMainHead());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configured_subdirectory_reports_root_mismatch_and_all_observations(bool differentHead)
    {
        using var fixture = new RepositoryFixture();
        var first = fixture.Git("rev-parse", "HEAD");
        if (differentHead)
        {
            fixture.Git("commit", "-q", "--allow-empty", "-m", "Second");
            fixture.Git("checkout", "-q", "--detach", first);
        }
        var main = fixture.Git("rev-parse", "main");
        var subdirectory = Path.Combine(fixture.Root, "nested");
        Directory.CreateDirectory(subdirectory);
        var repository = new GitAuthorBriefDraftRepository(subdirectory);

        var exception = Assert.Throws<AuthorDraftRepositoryNotAtMainException>(() => repository.ResolveMainHead());
        Assert.Equal(differentHead ? "head-not-main, toplevel-not-root" : "toplevel-not-root", exception.Condition);
        Assert.Equal(first, exception.Head);
        Assert.Equal(main, exception.Main);
        Assert.Equal(fixture.Root, exception.Toplevel);
        Assert.Equal(subdirectory, exception.ConfiguredRoot);
        Assert.Contains(exception.Condition, exception.Message);
        Assert.Contains(first, exception.Message);
        Assert.Contains(main, exception.Message);
        Assert.Contains(fixture.Root, exception.Message);
        Assert.Contains(subdirectory, exception.Message);
    }

    [Fact]
    public void Tracked_edits_keep_the_original_exception_and_message()
    {
        using var fixture = new RepositoryFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "seed.txt"), "changed");
        var repository = new GitAuthorBriefDraftRepository(fixture.Root);

        var exception = Assert.Throws<InvalidOperationException>(() => repository.ResolveMainHead());
        Assert.Equal("Author drafting requires no tracked edits against main HEAD.", exception.Message);
    }

    private sealed class RepositoryFixture : IDisposable
    {
        internal string Root { get; }

        internal RepositoryFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mcg-author-repository-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Git("init", "-q");
            // Canonicalize git's root so Windows temp short names cannot mimic a mismatch.
            Root = Git("rev-parse", "--show-toplevel");
            Git("config", "user.email", "tests@example.com");
            Git("config", "user.name", "Author Repository Tests");
            Git("config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(Root, "seed.txt"), "seed");
            Git("add", "seed.txt");
            Git("commit", "-q", "-m", "Seed");
            Git("checkout", "-q", "-B", "main");
        }

        internal string Git(params string[] arguments)
        {
            var result = GitCli.Run(Root, arguments);
            Assert.True(result.Succeeded && !result.DrainTimedOut,
                $"git {string.Join(' ', arguments)} failed: exit={result.ExitCode}; stderr={result.Error}");
            return result.Output.Trim();
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
