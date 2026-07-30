using Mcg.AgentOrchestrator.Core;

public sealed class AgentOutputDirectivesTests
{
    [Xunit.Fact(DisplayName = "WorkerResultTemplate_requires_structured_tests_and_blockers_tokens")]
    public void WorkerResultTemplateRequiresStructuredTestsAndBlockersTokens()
    {
        Assert.Contains("tests: <pass|fail|not-run|deferred|inconclusive - token first, then current-round evidence>", AgentOutputDirectives.WorkerResultTemplateLines);
        Assert.Contains(
            AgentOutputDirectives.WorkerResultTemplateLines,
            line => line.Contains("premise-invalid - fact and evidence", StringComparison.Ordinal));
        Assert.Contains(
            "evidence-request: <optional; ProjectAlias: FullyQualifiedName~TestClass or ProjectAlias: TestClass1,TestClass2>",
            AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Reviewer));
        Assert.Contains(
            AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Reviewer),
            line =>
                line.StartsWith("findings:", StringComparison.Ordinal) &&
                line.Contains("severity:blocking|advisory", StringComparison.Ordinal) &&
                line.Contains("severity is required", StringComparison.Ordinal));
        Assert.Contains(
            AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Reviewer),
            line => line.StartsWith("touched_anchors:", StringComparison.Ordinal));
        Assert.Contains(
            AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Reviewer),
            line =>
                line.StartsWith("blockers:", StringComparison.Ordinal) &&
                line.Contains("none - token first when verdict is pass", StringComparison.Ordinal) &&
                line.Contains("blank is invalid", StringComparison.Ordinal) &&
                line.Contains("advisory findings belong only in findings", StringComparison.Ordinal));
        Assert.Contains(
            "When no open blocking findings remain, use `verdict: pass` and `blockers: none` even when open advisory findings remain",
            SdlcRolePromptRequirements.BuildPlainText(AgentRole.Reviewer),
            StringComparison.Ordinal);
        Assert.Contains(
            "When none remain, use `verdict: pass` and `blockers: none` even with open advisories",
            SdlcRolePromptRequirements.BuildPlainText(AgentRole.Reviewer, TaskComplexity.Simple),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "requires `needs-work` or `fail`",
            SdlcRolePromptRequirements.BuildPlainText(AgentRole.Reviewer),
            StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Role_requirements_define_canonical_inconclusive_and_premise_invalid_results")]
    public void RoleRequirementsDefineCanonicalInconclusiveAndPremiseInvalidResults()
    {
        Assert.Contains(
            "tests: inconclusive - <current-round evidence>",
            SdlcRolePromptRequirements.BuildPlainText(AgentRole.Tester),
            StringComparison.Ordinal);
        Assert.Contains(
            "blockers: none",
            SdlcRolePromptRequirements.BuildPlainText(AgentRole.Tester, TaskComplexity.Simple),
            StringComparison.Ordinal);
        Assert.Contains(
            "blockers: premise-invalid - <fact and evidence>",
            SdlcRolePromptRequirements.BuildPlainText(AgentRole.Planner),
            StringComparison.Ordinal);
        Assert.Contains(
            "blockers: premise-invalid - <fact and evidence>",
            SdlcRolePromptRequirements.BuildPlainText(AgentRole.Researcher, TaskComplexity.Simple),
            StringComparison.Ordinal);
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
