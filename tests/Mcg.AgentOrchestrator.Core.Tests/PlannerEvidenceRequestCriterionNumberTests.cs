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
        const string preceding = "needed, and reason. ";
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

    private static string EvidenceRequest(string criterionField) =>
        "PLANNER_EVIDENCE_REQUEST: {" + criterionField +
        "\"evidence_key\":\"candidate-receipt\",\"availability\":\"post-implementation\",\"owner\":\"Acceptance\",\"needed\":\"focused test receipt\",\"reason\":\"the candidate must exist before execution\"}";
}
