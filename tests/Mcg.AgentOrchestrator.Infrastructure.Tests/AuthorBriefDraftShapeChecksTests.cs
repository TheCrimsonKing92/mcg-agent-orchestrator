using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: text analysis and an instance-owned repository fake; no real Git or shared state.
public sealed class AuthorBriefDraftShapeChecksTests
{
    private const string PartialPath = "src/Mcg.AgentOrchestrator.App/Orchestration/BriefLint.Extra.cs";
    private const string BasePath = "src/Mcg.AgentOrchestrator.App/Orchestration/BriefLint.cs";

    [Theory]
    [InlineData("new")]
    [InlineData("new file")]
    [InlineData("newly added")]
    [InlineData("added")]
    [InlineData("create")]
    [InlineData("creates")]
    [InlineData("CREATED")]
    public void Untracked_partial_of_tracked_class_Fails_with_path_and_remedy(string marker)
    {
        var repository = new CliAuthorDraftCommandTests.FakeRepository();
        repository.TrackedFiles.Add(BasePath, 100);
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown + $"\n`{PartialPath}` ({marker})";
        var check = Check(markdown, repository, "new-partial-file");
        Assert.False(check.Passed);
        Assert.Contains(PartialPath, check.Detail, StringComparison.Ordinal);
        Assert.Contains("Extract a separately named type", check.Detail, StringComparison.Ordinal);
        Assert.All(BoardFillPreflightChecks.Run(markdown), item => Assert.True(item.Passed, item.Detail));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Absent_base_or_tracked_partial_Passes(bool baseTracked, bool partialTracked)
    {
        var repository = new CliAuthorDraftCommandTests.FakeRepository();
        if (baseTracked) repository.TrackedFiles.Add(BasePath, 100);
        if (partialTracked) repository.TrackedFiles.Add(PartialPath, 20);
        Assert.True(Check($"{PartialPath} (new file)", repository, "new-partial-file").Passed);
    }

    [Fact]
    public void New_marker_at_forty_character_boundary_Fails_for_single_segment_path()
    {
        var repository = new CliAuthorDraftCommandTests.FakeRepository();
        repository.TrackedFiles.Add("src/Feature/Widget.cs", 20);
        var check = Check("src/Feature/Widget.Part.cs" + new string(' ', 40) + "new",
            repository, "new-partial-file");
        Assert.False(check.Passed);
        Assert.Contains("src/Feature/Widget.Part.cs", check.Detail);
    }

    [Fact]
    public void Nonfollowing_or_distant_marker_and_rule_prose_Pass()
    {
        var repository = new CliAuthorDraftCommandTests.FakeRepository();
        repository.TrackedFiles.Add(BasePath, 100);
        string[] drafts =
        [
            $"new file `{PartialPath}`", $"{PartialPath}{new string(' ', 41)}new",
            $"{PartialPath}\nnew file", $"{PartialPath} renewed",
            "src/<dir>/<Class>.<Part>.cs (new file)",
            "A new class goes in a separately named type in its own file, never in a new partial file of an existing class."
        ];
        foreach (var draft in drafts)
            Assert.True(Check(draft, repository, "new-partial-file").Passed, draft);
    }

    [Fact]
    public void Multiple_partial_paths_Produce_one_check_with_distinct_paths()
    {
        var repository = new CliAuthorDraftCommandTests.FakeRepository();
        repository.TrackedFiles.Add(BasePath, 100);
        const string second = "src/Mcg.AgentOrchestrator.App/Orchestration/BriefLint.Other.cs";
        var check = Check($"{PartialPath} new\n{second} added\n{PartialPath} new",
            repository, "new-partial-file");
        Assert.False(check.Passed);
        Assert.StartsWith($"{PartialPath}, {second}. Extract", check.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BriefLintTests.PreChangeFailureCriteria), MemberType = typeof(BriefLintTests))]
    public void Pre_change_failure_Fails_with_criterion_number(string criterion, string anchor)
    {
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace("The receipt records the result. Developer owns; Acceptance executes. TEST-VERIFIABLE.", criterion);
        var check = Check(markdown, new CliAuthorDraftCommandTests.FakeRepository(), "pre-change-failure-criterion");
        Assert.False(check.Passed);
        Assert.Contains($"criterion 1 matched \"{anchor}\"", check.Detail, StringComparison.Ordinal);
        Assert.Contains("Reviewer reading", check.Detail);
        Assert.Contains("committed negative-control test that runs on the candidate", check.Detail);
    }

    [Theory]
    [MemberData(nameof(BriefLintTests.AllowedPreChangeBriefs), MemberType = typeof(BriefLintTests))]
    public void Allowed_pre_change_wording_Passes_draft_check(string brief) =>
        Assert.True(Check(brief, new CliAuthorDraftCommandTests.FakeRepository(),
            "pre-change-failure-criterion").Passed);

    [Fact]
    public void Pre_change_check_uses_numbered_lines_and_their_declared_numbers()
    {
        const string demand = "The test fails against prior code. Acceptance executes.";
        var markdown = $"## Acceptance criteria\n- {demand}\n3. The receipt exists.\n7) {demand}\n" +
            $"8. The test passes against\n{demand}\n## Scope\n9. {demand}";
        var check = Check(markdown, new CliAuthorDraftCommandTests.FakeRepository(),
            "pre-change-failure-criterion");

        Assert.False(check.Passed);
        Assert.Equal($"criterion 7 matched \"prior code\". {BriefLint.PreChangeFailureRemedy}", check.Detail);
    }

    [Fact]
    public void Multiple_pre_change_criteria_Report_numbers_in_one_check()
    {
        const string demand = "The test fails against prior code. Acceptance executes.";
        var check = Check($"## Acceptance criteria\n1. {demand}\n2. {demand}",
            new CliAuthorDraftCommandTests.FakeRepository(), "pre-change-failure-criterion");
        Assert.False(check.Passed);
        Assert.Contains("criterion 1 matched \"prior code\" | criterion 2 matched \"prior code\"", check.Detail);
    }

    [Fact]
    public void Board_fill_pre_change_demand_Fails_blocking_lint_preflight()
    {
        var markdown = BoardFillAssessmentTestFixture.Brief.Replace("The feature works.",
            "The test fails against pre-change code.");
        var failed = Assert.Single(BoardFillPreflightChecks.Run(markdown).Where(check => !check.Passed));
        Assert.Equal("preflight:brief-lint-blocking", failed.Name);
        Assert.Contains("pre-change-failure-criterion", failed.Detail);
    }

    private static AuthorBriefDraftCheck Check(string markdown,
        CliAuthorDraftCommandTests.FakeRepository repository, string name) =>
        Assert.Single(AuthorBriefDraftChecks.Run(markdown, CliAuthorDraftCommandTests.Fixture.MainSha, repository)
            .Where(check => check.Name == name));
}
