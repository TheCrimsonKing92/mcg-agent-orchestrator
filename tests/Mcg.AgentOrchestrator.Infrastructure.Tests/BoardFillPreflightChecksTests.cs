using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class BoardFillPreflightChecksTests
{
    [Fact]
    public void Clean_brief_passes_all_preflight_checks()
    {
        var checks = BoardFillPreflightChecks.Run(BoardFillAssessmentTestFixture.Brief);
        Assert.Equal(4, checks.Count);
        Assert.All(checks, check => Assert.True(check.Passed, check.Detail));
    }

    [Theory]
    [InlineData("Inspect .git/config.", "preflight:brief-lint-blocking", "git-directory-reference")]
    [InlineData("Edit SKILL.md.", "preflight:brief-lint-blocking", "skill-definition-file")]
    [InlineData("Implement authentication.", "preflight:brief-lint-blocking", "readiness-high-risk-word")]
    [InlineData("A sentence## Next heading", "preflight:inline-heading-split", "##")]
    [InlineData("Remove old tests.", "preflight:missing-test-removal-bullet", "")]
    public void Each_lint_condition_fails_only_its_board_fill_check(string text, string name, string kind)
    {
        var checks = BoardFillPreflightChecks.Run(BoardFillAssessmentTestFixture.Brief + "\n" + text);
        var failing = Assert.Single(checks.Where(check => !check.Passed));
        Assert.Equal(name, failing.Name);
        Assert.Contains(kind, failing.Detail);
    }

    [Theory]
    [InlineData("src/bin/File.cs")]
    [InlineData("src/.bin/File.cs")]
    [InlineData(".scratch/input.md")]
    [InlineData("src/scratch/input.md")]
    [InlineData("src/obj/File.cs")]
    [InlineData("src/.obj/File.cs")]
    [InlineData("artifacts/receipt.json")]
    [InlineData(".artifacts/receipt.json")]
    [InlineData("src\\OBJ\\File.cs")]
    public void Output_path_in_criterion_fails_the_path_check(string path)
    {
        var text = BoardFillAssessmentTestFixture.Brief.Replace("The feature works.", $"The file `{path}` exists.");
        var failing = Assert.Single(BoardFillPreflightChecks.Run(text).Where(check => !check.Passed));
        Assert.Equal("preflight:build-output-path", failing.Name);
        Assert.Equal(path, failing.Detail);
    }

    [Theory]
    [InlineData("A sentence ## Next heading")]
    [InlineData("the scratch artifacts bin and obj folders")]
    [InlineData("src/Robin/objective.cs")]
    [InlineData("src/binary/artifact.cs")]
    [InlineData("Remove old tests.\n- test-removal: OldTests.OldFact")]
    public void Similar_words_and_declared_removals_pass(string text) =>
        Assert.All(BoardFillPreflightChecks.Run(BoardFillAssessmentTestFixture.Brief + "\n" + text), check => Assert.True(check.Passed, check.Detail));
}
