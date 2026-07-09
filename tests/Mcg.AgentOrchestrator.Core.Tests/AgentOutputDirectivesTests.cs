using Mcg.AgentOrchestrator.Core;

public sealed class AgentOutputDirectivesTests
{
    [Xunit.Fact(DisplayName = "WorkerResultTemplate_for_researcher_requires_citations")]
    public void WorkerResultTemplateForResearcherRequiresCitations()
    {
        var lines = AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Researcher);

        Assert.Contains("citations: <repo files, commands, URLs, or none when no external/source evidence was used>", lines);
        Assert.Contains("citations", AgentOutputDirectives.WorkerResultRequiredFieldsForRole(AgentRole.Researcher));
    }

    [Xunit.Fact(DisplayName = "WorkerResultTemplate_for_reviewer_requires_verdict_and_blockers")]
    public void WorkerResultTemplateForReviewerRequiresVerdictAndBlockers()
    {
        var lines = AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Reviewer);
        var required = AgentOutputDirectives.WorkerResultRequiredFieldsForRole(AgentRole.Reviewer);

        Assert.Contains("verdict: <pass|fail|needs-work>", lines);
        Assert.Contains("blockers: <none or exact blocker; put deferred-verification notes in tests>", lines);
        Assert.Contains("verdict", required);
        Assert.Contains("blockers", required);
    }

    [Xunit.Theory(DisplayName = "TryParseHumanInputRequest_ignores_explicit_no_input_directives")]
    [Xunit.InlineData("Human input: none")]
    [Xunit.InlineData("Human input: no")]
    [Xunit.InlineData("Human input: not needed")]
    [Xunit.InlineData("Human input: not needed.")]
    [Xunit.InlineData("Human input: no input needed.")]
    [Xunit.InlineData("HUMAN_INPUT: none")]
    public void TryParseHumanInputRequestIgnoresExplicitNoInputDirectives(string output)
    {
        Assert.True(AgentOutputDirectives.TryParseHumanInputRequest(output) is null);
    }

    [Xunit.Theory(DisplayName = "TryParseHumanInputRequest_reads_real_human_input_directives")]
    [Xunit.InlineData("HUMAN_INPUT: Which branch should I modify?", "Which branch should I modify?")]
    [Xunit.InlineData("Implemented setup.\nHUMAN_INPUT: Which test command should run?", "Which test command should run?")]
    [Xunit.InlineData("Human input: Which API should I inspect?", "Which API should I inspect?")]
    public void TryParseHumanInputRequestReadsRealHumanInputDirectives(string output, string expected)
    {
        Assert.Equal(expected, AgentOutputDirectives.TryParseHumanInputRequest(output));
    }
}
