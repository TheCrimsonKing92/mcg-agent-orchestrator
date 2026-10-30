using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory repositories, plus one uniquely owned temporary Git repository.
public sealed class AuthorBriefDraftRatchetRowTests
{
    private const string Ratchet = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/SourceSizeRatchet.cs";
    private const string Rows = """"
        new SourceSizeCeiling("src/Existing.cs", 100), // existing row
        // new SourceSizeCeiling("src/Commented.cs", 100),
        /* new SourceSizeCeiling("src/BlockComment.cs", 100), */
        const string example = "new SourceSizeCeiling(\"src/String.cs\", 100)";
        const string verbatim = @"new SourceSizeCeiling(""src/Verbatim.cs"", 100)";
        new SourceSizeCeiling(
            "src/Multiline.cs", 200),
        """";

    [Theory]
    [InlineData("Update the ratchet row for `src/Existing.cs`.", true)]
    [InlineData("Update the SourceSizeRatchet row for `src/Missing.cs`.", false)]
    [InlineData("Add a ratchet row for `src/Missing.cs`.", true)]
    [InlineData("The ratchet row for `src/Missing.cs` is added.", true)]
    [InlineData("Adding the SourceSizeRatchet row for `src/Missing.cs`.", true)]
    [InlineData("A new ratchet row for `src/Missing.cs`.", true)]
    [InlineData("Do not add a ratchet row for `src/Missing.cs`.", false)]
    [InlineData("The ratchet row for `src/Missing.cs` is not added.", false)]
    [InlineData("Add tests for the ratchet row for `src/Missing.cs`.", false)]
    [InlineData("The ratchet row for `src/Missing.cs` has tests added.", false)]
    [InlineData("Update ratchet row `src/Commented.cs`.", false)]
    [InlineData("Update ratchet row `src/BlockComment.cs`.", false)]
    [InlineData("Update ratchet row `src/String.cs`.", false)]
    [InlineData("Update ratchet row `src/Verbatim.cs`.", false)]
    [InlineData("Update ratchet row `src/Multiline.cs`.", true)]
    public void Row_references_Require_live_entry_or_affirmative_addition(string line, bool passed)
    {
        var check = Check(line, new Repository());
        Assert.Equal(passed, check.Passed);
        if (!passed)
        {
            var path = line.Split('`')[1];
            Assert.Equal($"{path}: no SourceSizeCeiling entry in {Ratchet} at fixed-main", check.Detail);
        }
    }

    [Fact]
    public void Multiple_references_Report_distinct_missing_paths_in_order()
    {
        var check = Check("""
            Update ratchet rows for `src/Missing.cs`, `src/Existing.cs`, `src/Other.cs`.
            Update ratchet row for `src/Missing.cs`.
            """, new Repository());
        Assert.False(check.Passed);
        Assert.Equal($"src/Missing.cs: no SourceSizeCeiling entry in {Ratchet} at fixed-main | " +
            $"src/Other.cs: no SourceSizeCeiling entry in {Ratchet} at fixed-main", check.Detail);
    }

    [Fact]
    public void Fences_and_incidental_ratchet_mentions_Do_not_read_ratchet()
    {
        var check = Check("""
            Mention the ratchet and `src/Missing.cs`.
            ~~~markdown
            Update ratchet row `src/Missing.cs`.
            ~~~
            """, new Repository { ThrowOnRead = true });
        Assert.True(check.Passed, check.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unreadable_ratchet_Returns_failed_check(bool throws)
    {
        var check = Check("Update ratchet row `src/Existing.cs`.",
            new Repository { Content = null, ThrowOnRead = throws });
        Assert.False(check.Passed);
        Assert.Contains(throws ? "Validation could not be completed" : "not readable", check.Detail);
    }

    [Fact]
    public void Adding_row_Does_not_exempt_missing_modification_target()
    {
        var checks = AuthorBriefDraftTargetChecks.Run(
            "## What to build\nAdd a new ratchet row for `src/Missing.cs`.", "fixed-main", new Repository());
        Assert.True(Assert.Single(checks, check => check.Name == "ratchet-rows").Passed);
        var failed = Assert.Single(checks.Where(check => !check.Passed));
        Assert.Equal("modification-targets", failed.Name);
        Assert.Contains("src/Missing.cs: not tracked at fixed-main", failed.Detail);
    }

    [Fact]
    public void Git_content_Uses_supplied_main_snapshot_despite_branch_and_worktree_edits()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-author-ratchet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Git("init", "-q");
            Git("config", "user.email", "tests@example.com");
            Git("config", "user.name", "Author Ratchet Tests");
            Git("config", "commit.gpgsign", "false");
            var file = Path.Combine(root, Ratchet);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, Rows);
            Git("add", ".");
            Git("commit", "-q", "-m", "Ratchet entries");
            Git("checkout", "-q", "-B", "main");
            var repository = new GitAuthorBriefDraftRepository(root, "main");
            var head = repository.ResolveMainHead();
            Assert.Equal(Git("rev-parse", "main"), head);
            Git("checkout", "-q", "-b", "draft");
            File.WriteAllText(file, "// branch content");
            Git("add", ".");
            Git("commit", "-q", "-m", "Draft edits");
            Assert.NotEqual(head, Git("rev-parse", "HEAD"));
            File.WriteAllText(file, "// uncommitted content");

            Assert.Equal(Rows, repository.TrackedFileText(head, Ratchet));
            Assert.Null(repository.TrackedFileText(head, "src/Missing.cs"));
            var check = Assert.Single(AuthorBriefDraftTargetChecks.Run(
                "Update ratchet row `src/Existing.cs`.", head, repository), check => check.Name == "ratchet-rows");
            Assert.True(check.Passed, check.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        string Git(params string[] arguments)
        {
            var result = GitCli.Run(root, arguments);
            Assert.True(result.Succeeded && !result.DrainTimedOut,
                $"git {string.Join(' ', arguments)} failed: exit={result.ExitCode}; stderr={result.Error}");
            return result.Output.Trim();
        }
    }

    private static AuthorBriefDraftCheck Check(string line, Repository repository) => Assert.Single(
        AuthorBriefDraftChecks.Run(CliAuthorDraftCommandTests.ValidMarkdown + "\n" + line,
            "fixed-main", repository), check => check.Name == "ratchet-rows");

    private sealed class Repository : IAuthorBriefDraftRepository
    {
        internal string? Content { get; init; } = Rows;
        internal bool ThrowOnRead { get; init; }
        public string ResolveMainHead() => throw new InvalidOperationException("Snapshot must be supplied.");
        public int? TrackedLineCount(string sha, string path)
        {
            Assert.Equal("fixed-main", sha);
            return path == "docs/role-capability-matrix.md" ? 100 : null;
        }
        public string? TrackedFileText(string sha, string path)
        {
            Assert.Equal("fixed-main", sha);
            Assert.Equal(Ratchet, path);
            if (ThrowOnRead) throw new InvalidOperationException("repository unavailable");
            return Content;
        }
    }
}
