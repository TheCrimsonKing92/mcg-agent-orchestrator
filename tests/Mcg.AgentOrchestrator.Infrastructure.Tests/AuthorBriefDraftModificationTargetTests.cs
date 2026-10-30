using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: immutable markdown and a per-test in-memory repository.
public sealed class AuthorBriefDraftModificationTargetTests
{
    [Fact]
    public void Build_targets_Tracked_missing_and_new_Only_missing_fails()
    {
        var checks = Run("""
            ## What to build
            - Update `src/Feature/Existing.cs`.
            - Update `src/Feature/Missing.cs`.
            - Add a new `src/Feature/Added.cs` file.
            """);

        var failed = Assert.Single(checks.Where(check => !check.Passed));
        Assert.Equal("modification-targets", failed.Name);
        Assert.Equal("src/Feature/Missing.cs: not tracked at fixed-main", failed.Detail);
        Assert.True(Assert.Single(checks, check => check.Name == "premise-citations").Passed);
    }

    [Fact]
    public void Scope_targets_Tracked_missing_and_new_Only_missing_directory_fails()
    {
        var checks = Run("""
            ## What to build
            Implement the slice.
            ## Scope
            In scope:
            - `src/Feature/`
            - `src/Missing/`
            - `src/Feature/Extra.cs` (new file)
            Out of scope:
            - `src/Excluded/`
            """);

        var failed = Assert.Single(checks.Where(check => !check.Passed));
        Assert.Equal("modification-targets", failed.Name);
        Assert.Equal("src/Missing: not tracked at fixed-main", failed.Detail);
    }

    [Fact]
    public void Decorated_sections_and_nested_build_content_Check_only_owned_paths()
    {
        var check = Target("""
            ## **wHaT tO bUiLd** ##
            ### Details
            Update `./src/Feature/Existing.cs:1-2` and `README.md`.
            ```md
            ## Scope
            `src/Fenced.cs`
            ```
            Use `Class.Method`, `src/.../Example.cs`, `https://example.com/a.cs`.
            ## Acceptance criteria
            Ignore `src/Criteria.cs`.
            ## Scope
            A prose reference `src/NotInList.cs`.
            ### **In scope:**
            - directory `docs`
            - `src/Missing/`
            ### Out of scope
            - `src/Excluded/`
            """);
        Assert.False(check.Passed);
        Assert.Equal("src/Missing: not tracked at fixed-main", check.Detail);
    }

    [Theory]
    [InlineData("Create `new/Directory/`.", true)]
    [InlineData("A NEW `new/File.cs` file.", true)]
    [InlineData("Do not create `new/File.cs`.", false)]
    [InlineData("No new `new/File.cs` file.", false)]
    [InlineData("`src/Feature/New.cs` is a new file.", true)]
    [InlineData("`new/File.cs` (new file).", false)]
    [InlineData("Add a new row for `src/Feature/New.cs` to the ratchet.", false)]
    [InlineData("Create a ratchet row for `src/Feature/New.cs`.", false)]
    public void New_target_declarations_Require_local_affirmative_intent(string line, bool passed)
    {
        Assert.Equal(passed, Target("## What to build\n- " + line).Passed);
    }

    [Fact]
    public void New_target_in_one_item_Does_not_exempt_other_targets()
    {
        var check = Target("""
            ## What to build
            - Create `src/Feature/New.cs` and update `src/Feature/Missing.cs`.
            - Update `src/Feature/Missing.cs`.
            - Add a new file
              `src/Feature/Continued.cs`.
            - Update `src/Other.cs`.
            """);
        Assert.False(check.Passed);
        Assert.Equal("src/Feature/Missing.cs: not tracked at fixed-main | src/Other.cs: not tracked at fixed-main", check.Detail);
    }

    [Fact]
    public void Invalid_paths_and_case_mismatches_Cannot_resolve_as_tracked()
    {
        var check = Target("""
            ## What to build
            Create `../Escape.cs`.
            Update `src/feature/Existing.cs`.
            """);
        Assert.False(check.Passed);
        Assert.Contains("../Escape.cs: not tracked at fixed-main", check.Detail);
        Assert.Contains("src/feature/Existing.cs: not tracked at fixed-main", check.Detail);
    }

    [Fact]
    public void Repository_failure_Returns_failed_check()
    {
        var checks = AuthorBriefDraftTargetChecks.Run("## What to build\n`src/Missing.cs`",
            "fixed-main", new UnreadableRepository());
        var failed = Assert.Single(checks.Where(check => !check.Passed));
        Assert.Equal("modification-targets", failed.Name);
        Assert.Contains("Validation could not be completed", failed.Detail);
        Assert.Contains("repository unavailable", failed.Detail);
    }

    [Fact]
    public void Subordinate_heading_paths_Are_modification_targets()
    {
        var check = Target("## What to build\n### Update `src/Missing.cs`\nImplement the slice.");
        Assert.False(check.Passed);
        Assert.Equal("src/Missing.cs: not tracked at fixed-main", check.Detail);
    }

    private static AuthorBriefDraftCheck Target(string body) =>
        Assert.Single(Run(body), check => check.Name == "modification-targets");

    private static IReadOnlyList<AuthorBriefDraftCheck> Run(string body)
    {
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.ReplaceLineEndings("\n")
            .Replace("Observed in `docs/role-capability-matrix.md:1`.", "Observed the gap.")
            .Replace("## What to build\nImplement the requested slice.", body);
        return AuthorBriefDraftChecks.Run(markdown, "fixed-main", new Repository());
    }

    private sealed class Repository : IAuthorBriefDraftRepository
    {
        public string ResolveMainHead() => throw new InvalidOperationException("Snapshot must be supplied.");
        public int? TrackedLineCount(string sha, string path)
        {
            Assert.Equal("fixed-main", sha);
            return path is "src/Feature/Existing.cs" or "README.md" ? 10 : null;
        }
        public bool IsTrackedDirectory(string sha, string path)
        {
            Assert.Equal("fixed-main", sha);
            return path is "src/Feature" or "docs";
        }
    }

    private sealed class UnreadableRepository : IAuthorBriefDraftRepository
    {
        public string ResolveMainHead() => "fixed-main";
        public int? TrackedLineCount(string sha, string path) =>
            throw new InvalidOperationException("repository unavailable");
    }
}
