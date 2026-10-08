using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class CliAuthorDraftBriefLintTests
{
    [Theory]
    [InlineData("Create the .git-keep marker.", "git-directory-reference", BriefLintSeverity.BlocksDispatch)]
    [InlineData("Plan the authentication change.", "readiness-high-risk-word", BriefLintSeverity.BlocksCliStart)]
    public void Blocking_finding_fails_command_and_is_recorded_in_receipt(
        string text, string kind, BriefLintSeverity severity)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace("Implement the requested slice.", text);
        var finding = Assert.Single(BriefLint.Lint(markdown));
        Assert.Equal(severity, finding.Severity);
        var detail = $"{finding.Message} remedy: {finding.Remedy}";

        Assert.Equal(1, fixture.Run(Draft(markdown)));

        var draft = Assert.Single(Directory.GetFiles(fixture.Drafts, "*.md"));
        Assert.Equal(markdown, File.ReadAllText(draft));
        using var receipt = fixture.Receipt();
        var checks = receipt.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(9, checks.Length);
        Assert.All(checks.Take(8), check => Assert.True(check.GetProperty("passed").GetBoolean()));
        var failed = Assert.Single(checks.Where(check => !check.GetProperty("passed").GetBoolean()));
        Assert.Equal($"brief-lint:{kind}", failed.GetProperty("name").GetString());
        Assert.Equal(detail, failed.GetProperty("detail").GetString());
        Assert.Contains($"Failed brief-lint:{kind}: {detail}", fixture.Output.ToString());
        Assert.Equal(GoalCommand(fixture, draft), LastOutputLine(fixture));
        Assert.Empty(fixture.Error.ToString());
    }

    [Fact]
    public void Advisory_finding_prints_before_goal_command_without_changing_checks_or_exit_code()
    {
        using var clean = new CliAuthorDraftCommandTests.Fixture();
        var cleanExitCode = clean.Run(Draft(CliAuthorDraftCommandTests.ValidMarkdown));
        Assert.Equal(0, cleanExitCode);
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown + "\nSummary ends here## Next steps";
        var finding = Assert.Single(BriefLint.Lint(markdown));
        Assert.Equal(BriefLintSeverity.Advisory, finding.Severity);
        var advisory = $"BRIEF-LINT advisory inline-heading-split: {finding.Message} remedy: {finding.Remedy}";

        Assert.Equal(cleanExitCode, fixture.Run(Draft(markdown)));

        var output = fixture.Output.ToString();
        Assert.Contains(advisory, output);
        Assert.DoesNotContain("Failed brief-lint:", output);
        var draft = Assert.Single(Directory.GetFiles(fixture.Drafts, "*.md"));
        var goalCommand = GoalCommand(fixture, draft);
        Assert.True(output.IndexOf(advisory, StringComparison.Ordinal) < output.IndexOf(goalCommand, StringComparison.Ordinal));
        Assert.Equal(goalCommand, LastOutputLine(fixture));
        using var receipt = fixture.Receipt();
        using var cleanReceipt = clean.Receipt();
        Assert.Equal(cleanReceipt.RootElement.GetProperty("checks").GetRawText(),
            receipt.RootElement.GetProperty("checks").GetRawText());
        Assert.Empty(fixture.Error.ToString());
    }

    [Fact]
    public void Clean_draft_has_no_lint_output_and_keeps_original_checks_and_goal_command()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        Assert.Empty(BriefLint.Lint(CliAuthorDraftCommandTests.ValidMarkdown));

        Assert.Equal(0, fixture.Run(Draft(CliAuthorDraftCommandTests.ValidMarkdown)));

        Assert.DoesNotContain("brief-lint", fixture.Output.ToString(), StringComparison.OrdinalIgnoreCase);
        var draft = Assert.Single(Directory.GetFiles(fixture.Drafts, "*.md"));
        Assert.Equal(GoalCommand(fixture, draft), LastOutputLine(fixture));
        using var receipt = fixture.Receipt();
        var checks = receipt.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(new[] { "sections", "criteria-present", "owner-sentence", "premise-citations", "numbered-criteria", "developer-deferred-criterion", "build-item-count" },
            checks.Select(check => check.GetProperty("name").GetString()).ToArray());
        Assert.All(checks, check => Assert.True(check.GetProperty("passed").GetBoolean()));
        Assert.Empty(fixture.Error.ToString());
    }

    [Fact]
    public void Lint_still_runs_when_deterministic_checks_fail()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown
            .Replace("docs/role-capability-matrix.md:1", "missing/file.cs:1") + "\nCreate the .git-keep marker.";

        Assert.Equal(1, fixture.Run(Draft(markdown)));

        using var receipt = fixture.Receipt();
        var failedNames = receipt.RootElement.GetProperty("checks").EnumerateArray()
            .Where(check => !check.GetProperty("passed").GetBoolean())
            .Select(check => check.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "premise-citations", "brief-lint:git-directory-reference" }, failedNames);
        Assert.Contains("Failed brief-lint:git-directory-reference:", fixture.Output.ToString());
        Assert.Empty(fixture.Error.ToString());
    }

    private static string Draft(string markdown) => JsonSerializer.Serialize(new { kind = "draft", markdown });

    private static string GoalCommand(CliAuthorDraftCommandTests.Fixture fixture, string draft) =>
        $"goal --brief-file \"{draft}\" --backlog-item {fixture.Item.Id} --backlog-coverage full";

    private static string LastOutputLine(CliAuthorDraftCommandTests.Fixture fixture) =>
        fixture.Output.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Last();
}
