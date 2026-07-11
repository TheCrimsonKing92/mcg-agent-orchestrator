using Mcg.AgentOrchestrator.Core;

public sealed class AgentOutputDirectivesTests
{
    [Xunit.Fact(DisplayName = "WorkerResultTemplate_requires_structured_tests_and_blockers_tokens")]
    public void WorkerResultTemplateRequiresStructuredTestsAndBlockersTokens()
    {
        Assert.Contains("tests: <pass|fail|not-run|deferred - evidence>", AgentOutputDirectives.WorkerResultTemplateLines);
        Assert.Contains("blockers: <none|exact-blocker; put deferred-verification notes in tests>", AgentOutputDirectives.WorkerResultTemplateLines);
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
