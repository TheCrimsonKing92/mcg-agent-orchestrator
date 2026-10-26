using Mcg.AgentOrchestrator.Core;

// Parallel-safe: exercises only stateless directive generation and parsing.
public sealed class PlannerEvidenceRequestCriterionNumberTests
{
    private const string NumberingDiagnostic =
        "Malformed PLANNER_EVIDENCE_REQUEST: criterion_index is the 1-based criterion number from the mapping lines; use 1 for the first criterion.";
    private const string CombinedDiagnostic =
        "Malformed PLANNER_EVIDENCE_REQUEST: criterion_index must be positive and evidence_key, availability, needed, and reason must be non-empty.";

    [Xunit.Fact]
    public void PlannerDirective_EvidenceRequest_StatesOneBasedNumberAtRequiredPosition()
    {
        const string sentence =
            "In that directive criterion_index is the 1-based criterion number used on the mapping lines, so the first criterion is 1; this differs from the 0-based criterion_index of criteria_verdicts. ";
        const string preceding = "never true or false. ";
        const string following = "For an orchestrator record";
        var line = Assert.Single(AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner),
            candidate => candidate.Contains("PLANNER_EVIDENCE_REQUEST:", StringComparison.Ordinal));

        Assert.Contains(sentence, line, StringComparison.Ordinal);
        var precedingIndex = line.IndexOf(preceding, StringComparison.Ordinal);
        var sentenceIndex = line.IndexOf(sentence, StringComparison.Ordinal);
        var followingIndex = line.IndexOf(following, StringComparison.Ordinal);
        Assert.True(precedingIndex >= 0);
        Assert.Equal(precedingIndex + preceding.Length, sentenceIndex);
        Assert.Equal(sentenceIndex + sentence.Length, followingIndex);
    }

    [Xunit.Theory]
    [Xunit.InlineData("\"criterion_index\":0,", NumberingDiagnostic)]
    [Xunit.InlineData("\"criterion_index\":-2,", NumberingDiagnostic)]
    [Xunit.InlineData("", CombinedDiagnostic)]
    [Xunit.InlineData("\"criterion_index\":\"1\",", CombinedDiagnostic)]
    public void ParseHumanInputRequest_InvalidCriterionNumber_RejectsWithExpectedDiagnostic(
        string criterionField, string expectedDiagnostic)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            EvidenceRequest(criterionField), AgentRole.Planner);

        Assert.True(parsed.IsMalformed);
        Assert.Null(parsed.Directive);
        Assert.Equal(expectedDiagnostic, parsed.Diagnostic);
    }

    [Xunit.Fact]
    public void ParseHumanInputRequest_FirstCriterion_KeepsAcceptedQuestion()
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            EvidenceRequest("\"criterion_index\":1,"), AgentRole.Planner);

        Assert.False(parsed.IsMalformed, parsed.Diagnostic);
        Assert.NotNull(parsed.Directive);
        Assert.Contains("Planner evidence request for criterion 1:", parsed.Directive.Question,
            StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void BooleanNeeded_IsRejectedWithFieldMessage(bool needed)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            EvidenceRequest("\"criterion_index\":1,", needed ? "\"needed\":true," : "\"needed\":false,"),
            AgentRole.Planner);

        Assert.True(parsed.IsMalformed);
        Assert.Null(parsed.Directive);
        Assert.Equal(
            "Malformed PLANNER_EVIDENCE_REQUEST: needed must be a non-empty JSON string that names the evidence, not true or false.",
            parsed.Diagnostic);
    }

    [Xunit.Fact]
    public void StringNeeded_IsAccepted()
    {
        const string needed = "a passing TRX row for Class.Method";
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            EvidenceRequest("\"criterion_index\":1,", "\"needed\":\"" + needed + "\","), AgentRole.Planner);

        Assert.False(parsed.IsMalformed, parsed.Diagnostic);
        Assert.NotNull(parsed.Directive);
        Assert.NotNull(parsed.Directive.PlannerEvidence);
        Assert.Equal(needed, parsed.Directive.PlannerEvidence.Needed);
    }

    [Xunit.Fact]
    public void MissingOrEmptyNeeded_KeepsGenericMessage()
    {
        foreach (var neededField in new[] { "", "\"needed\":\"\"," })
        {
            var parsed = AgentOutputDirectives.ParseHumanInputRequest(
                EvidenceRequest("\"criterion_index\":1,", neededField), AgentRole.Planner);

            Assert.True(parsed.IsMalformed);
            Assert.Null(parsed.Directive);
            Assert.Equal(CombinedDiagnostic, parsed.Diagnostic);
        }
    }

    private static string EvidenceRequest(string criterionField,
        string neededField = "\"needed\":\"focused test receipt\",") =>
        "PLANNER_EVIDENCE_REQUEST: {" + criterionField +
        "\"evidence_key\":\"candidate-receipt\",\"availability\":\"post-implementation\",\"owner\":\"Acceptance\"," +
        neededField + "\"reason\":\"the candidate must exist before execution\"}";
}
