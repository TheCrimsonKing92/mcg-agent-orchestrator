using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its repository and local git configuration.
public sealed class GitTrackedEditsProbeTests
{
    [Fact]
    public void Clean_tree_and_untracked_files_do_not_hold()
    {
        using var repo = new RepositoryFixture();
        Assert.True(GitTrackedEditsProbe.Probe(repo.Root).Clean);
        File.WriteAllText(Path.Combine(repo.Root, "untracked.txt"), "ignored");
        var probe = GitTrackedEditsProbe.Probe(repo.Root);
        Assert.True(probe.Clean);
        Assert.Null(probe.Edits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tracked_staged_or_unstaged_edits_name_the_file(bool staged)
    {
        using var repo = new RepositoryFixture();
        File.WriteAllText(Path.Combine(repo.Root, "seed.txt"), "changed");
        if (staged) repo.Git("add", "seed.txt");
        var probe = GitTrackedEditsProbe.Probe(repo.Root);
        Assert.False(probe.Clean);
        Assert.Equal(new[] { "seed.txt" }, probe.Edits!.Paths);
        var repository = new GitAuthorBriefDraftRepository(repo.Root, "main");
        Assert.Equal(probe.Edits.Paths, repository.ResolveMainHeadOrTrackedEdits().TrackedEdits!.Paths);
        repo.Git("restore", "--source=HEAD", "--staged", "--worktree", "--", "seed.txt");
        Assert.True(GitTrackedEditsProbe.Probe(repo.Root).Clean);
        Assert.Null(repository.ResolveMainHeadOrTrackedEdits().TrackedEdits);
    }

    [Fact]
    public void Paths_are_sorted_and_render_at_most_five_with_a_remainder_count()
    {
        using var repo = new RepositoryFixture();
        foreach (var name in new[] { "f.txt", "c.txt", "b.txt", "e.txt", "a.txt", "d.txt" })
        {
            File.WriteAllText(Path.Combine(repo.Root, name), "new staged file");
            repo.Git("add", name);
        }
        var edits = GitTrackedEditsProbe.Probe(repo.Root).Edits!;
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt", "d.txt", "e.txt", "f.txt" }, edits.Paths);
        Assert.Equal("a.txt, b.txt, c.txt, d.txt, e.txt (+1 more)", edits.Render());
    }

    [Fact]
    public void Invalid_repository_is_unresolved_and_does_not_report_clean()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var probe = GitTrackedEditsProbe.Probe(root);
            Assert.False(probe.Clean);
            Assert.Null(probe.Edits);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Not_at_main_wins_over_tracked_edits_in_the_typed_resolution()
    {
        using var repo = new RepositoryFixture();
        var previous = repo.Git("rev-parse", "HEAD");
        repo.Git("commit", "-q", "--allow-empty", "-m", "Next main");
        repo.Git("checkout", "-q", "--detach", previous);
        File.WriteAllText(Path.Combine(repo.Root, "seed.txt"), "dirty detached tree");
        var repository = new GitAuthorBriefDraftRepository(repo.Root, "main");
        var mismatch = Assert.Throws<AuthorDraftRepositoryNotAtMainException>(() => repository.ResolveMainHeadOrTrackedEdits());
        Assert.Equal("head-not-main", mismatch.Condition);
        Assert.False(GitTrackedEditsProbe.Probe(repo.Root).Clean);
    }

    internal sealed class RepositoryFixture : IDisposable
    {
        internal string Root { get; }
        internal RepositoryFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mcg-board-git-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Git("init", "-q", "-b", "main");
            Root = Git("rev-parse", "--show-toplevel");
            Git("config", "user.email", "tests@example.com");
            Git("config", "user.name", "Board Fill Tests");
            Git("config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(Root, "seed.txt"), "seed");
            Git("add", "seed.txt");
            Git("commit", "-q", "-m", "Seed");
        }

        internal string Git(params string[] args)
        {
            var result = GitCli.Run(Root, args);
            Assert.True(result.Succeeded && !result.DrainTimedOut,
                $"git {string.Join(' ', args)} failed: exit={result.ExitCode}; stderr={result.Error}");
            return result.Output.Trim();
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
