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
            AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Reviewer),
            line => line.Contains("evidence_request?:{selections:[{test_project,test_class}]}", StringComparison.Ordinal));
        Assert.Contains(
            AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Tester),
            line => line.Contains("evidence_request?:{selections:[{test_project,test_class}]}", StringComparison.Ordinal));
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
            line => line.StartsWith("criteria_verdicts:", StringComparison.Ordinal));
        Assert.Contains(
            AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Reviewer),
            line =>
                line.StartsWith("findings:", StringComparison.Ordinal) &&
                line.Contains("category:spec-compliance|spec-defect|correctness", StringComparison.Ordinal));
        Assert.Contains(
            "criteria_verdicts",
            AgentOutputDirectives.RequiredWorkerResultFieldNamesForRole(AgentRole.Reviewer));
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

    [Xunit.Fact(DisplayName = "Reviewer_requirements_order_spec_before_quality_within_budget")]
    public void ReviewerRequirementsOrderSpecBeforeQualityWithinBudget()
    {
        const int preChangeComplexChars = 3156;
        const int preChangeCompactChars = 2346;
        var complex = SdlcRolePromptRequirements.BuildPlainText(AgentRole.Reviewer);
        var compact = SdlcRolePromptRequirements.BuildPlainText(AgentRole.Reviewer, TaskComplexity.Simple);

        Assert.True(
            complex.IndexOf("### 1. Spec compliance", StringComparison.Ordinal) <
            complex.IndexOf("### 2. Code quality", StringComparison.Ordinal));
        Assert.True(
            compact.IndexOf("### 1. Spec compliance", StringComparison.Ordinal) <
            compact.IndexOf("### 2. Code quality", StringComparison.Ordinal));
        Assert.Contains(
            "not-verifiable with file+line or concrete task evidence",
            complex,
            StringComparison.Ordinal);
        Assert.Contains(
            "zero-based `criterion_index` values (0..N-1), with exactly one entry for every criterion",
            complex,
            StringComparison.Ordinal);
        Assert.Contains(
            "zero-based `criterion_index` values (0..N-1), exactly one per criterion",
            compact,
            StringComparison.Ordinal);
        Assert.DoesNotContain("not-verifiable-from-diff", complex, StringComparison.Ordinal);
        Assert.DoesNotContain("not-verifiable-from-diff", compact, StringComparison.Ordinal);
        Assert.True(
            complex.Length <= SdlcRolePromptRequirements.ReviewerComplexRequirementsMaxChars,
            $"Complex Reviewer requirements grew from {preChangeComplexChars} to {complex.Length} chars.");
        Assert.True(
            compact.Length <= SdlcRolePromptRequirements.ReviewerCompactRequirementsMaxChars,
            $"Compact Reviewer requirements grew from {preChangeCompactChars} to {compact.Length} chars.");
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
