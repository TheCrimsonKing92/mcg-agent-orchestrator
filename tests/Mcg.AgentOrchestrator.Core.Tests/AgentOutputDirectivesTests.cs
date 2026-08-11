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
                line.Contains("semicolon-delimited token", StringComparison.Ordinal) &&
                line.Contains("file:line", StringComparison.Ordinal) &&
                line.Contains("violated acceptance criterion", StringComparison.Ordinal) &&
                line.Contains("blank is invalid", StringComparison.Ordinal) &&
                line.Contains("advisory findings belong only in findings", StringComparison.Ordinal));
        Assert.Contains(
            "With none, use `verdict: pass` and `blockers: none`; advisories belong only in `findings`",
            SdlcRolePromptRequirements.BuildPlainText(AgentRole.Reviewer),
            StringComparison.Ordinal);
        Assert.Contains(
            "With none, use `verdict: pass` and `blockers: none`; advisories belong only in `findings`",
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
        const string attestationInstructionStart = "End needs-work verdict prose";
        const string exactAttestationInstruction =
            "End needs-work verdict prose with this exact standalone line immediately before WORKER_RESULT: `no other blocking findings exist in this diff`. Keep it outside `blockers`.";
        foreach (var requirements in new[] { complex, compact })
        {
            Assert.Contains("complete candidate diff supplied for the current round", requirements, StringComparison.Ordinal);
            Assert.Contains("enumerate every blocking finding", requirements, StringComparison.Ordinal);
            Assert.Contains("file:line", requirements, StringComparison.Ordinal);
            Assert.Contains("severity `blocking`", requirements, StringComparison.Ordinal);
            Assert.Contains("violated acceptance criterion", requirements, StringComparison.Ordinal);
            var attestationRequirement = Assert.Single(
                requirements.Split(Environment.NewLine),
                line => line.Contains(attestationInstructionStart, StringComparison.Ordinal));
            Assert.Equal(
                exactAttestationInstruction,
                attestationRequirement[attestationRequirement.IndexOf(attestationInstructionStart, StringComparison.Ordinal)..]);
            Assert.Contains("REVIEW DEFECT", requirements, StringComparison.Ordinal);
            Assert.Contains("demonstrably present in an earlier reviewed complete candidate diff", requirements, StringComparison.Ordinal);
            Assert.Contains("violated criterion index, normalized file path, line/region, then stable_id", requirements, StringComparison.Ordinal);
        }
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

    [Xunit.Fact(DisplayName = "Developer_requirements_always_require_failure_evidence_before_editing")]
    public void DeveloperRequirementsAlwaysRequireFailureEvidenceBeforeEditing()
    {
        const string requirement = "Before editing, name the failing test and quote its assertion output.";

        Assert.Contains(requirement, SdlcRolePromptRequirements.BuildPlainText(AgentRole.Developer), StringComparison.Ordinal);
        Assert.Contains(
            requirement,
            SdlcRolePromptRequirements.BuildPlainText(AgentRole.Developer, TaskComplexity.Simple),
            StringComparison.Ordinal);
        foreach (var role in new[] { AgentRole.Planner, AgentRole.Researcher, AgentRole.Tester, AgentRole.Reviewer })
        {
            Assert.DoesNotContain(requirement, SdlcRolePromptRequirements.BuildPlainText(role), StringComparison.Ordinal);
            Assert.DoesNotContain(
                requirement,
                SdlcRolePromptRequirements.BuildPlainText(role, TaskComplexity.Simple),
                StringComparison.Ordinal);
        }
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

    [Xunit.Fact]
    public void ParseHumanInputRequestClassifiesRetrievablePlannerEvidenceAndUsesStableFingerprint()
    {
        const string first =
            "PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":2,\"evidence_key\":\"third-round-receipt\",\"availability\":\"retrievable\",\"store\":\".orchestrator/goal-events/<goal>.jsonl\",\"needed\":\"timestamp and classifier receipt\",\"reason\":\"worker cannot read orchestrator state\"}";
        const string wordingVariant =
            "PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":2,\"evidence_key\":\"third-round-receipt\",\"availability\":\"retrievable\",\"store\":\"event store\",\"needed\":\"the original stdout\",\"reason\":\"not packaged\"}";

        var parsed = AgentOutputDirectives.ParseHumanInputRequest(first, AgentRole.Planner);
        var variant = AgentOutputDirectives.ParseHumanInputRequest(wordingVariant, AgentRole.Planner);

        Assert.False(parsed.IsMalformed);
        Assert.Contains("Availability: retrievable", parsed.Directive!.Question, StringComparison.Ordinal);
        Assert.Contains(".orchestrator/goal-events/<goal>.jsonl", parsed.Directive.Question, StringComparison.Ordinal);
        Assert.Equal(parsed.Directive.QuestionFingerprint, variant.Directive!.QuestionFingerprint);
    }

    [Xunit.Theory]
    [Xunit.InlineData("PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":1,\"evidence_key\":\"receipt\",\"availability\":\"retrievable\",\"needed\":\"stdout\",\"reason\":\"inaccessible\"}")]
    [Xunit.InlineData("PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":1,\"evidence_key\":\"receipt\",\"availability\":\"never-recorded\",\"store\":\"somewhere\",\"needed\":\"stdout\",\"reason\":\"absent\"}")]
    [Xunit.InlineData("PLANNER_EVIDENCE_REQUEST: not-json")]
    public void ParseHumanInputRequestRejectsMalformedPlannerEvidence(string output)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);

        Assert.True(parsed.IsMalformed);
        Assert.Null(parsed.Directive);
        Assert.StartsWith("Malformed PLANNER_EVIDENCE_REQUEST:", parsed.Diagnostic, StringComparison.Ordinal);
    }
}
