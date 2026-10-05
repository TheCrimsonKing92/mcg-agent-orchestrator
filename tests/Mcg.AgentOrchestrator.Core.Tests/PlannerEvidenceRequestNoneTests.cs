using Mcg.AgentOrchestrator.Core;

public sealed class PlannerEvidenceRequestNoneTests
{
    [Xunit.Theory]
    [Xunit.InlineData("none.")]
    [Xunit.InlineData("none required; all evidence needed to plan was read from HEAD.")]
    [Xunit.InlineData("none needed; every criterion is planned.")]
    [Xunit.InlineData("none")]
    [Xunit.InlineData("NONE")]
    [Xunit.InlineData(" NoNe; optional explanation ")]
    [Xunit.InlineData("none, explanation")]
    [Xunit.InlineData("none-explanation")]
    [Xunit.InlineData("none_explanation")]
    [Xunit.InlineData("none: explanation")]
    [Xunit.InlineData("none\texplanation")]
    [Xunit.InlineData("none {\"extra\":true}")]
    public void ParseHumanInputRequest_NonePayload_EqualsAbsentLine(string payload)
    {
        var absent = AgentOutputDirectives.ParseHumanInputRequest("Plan prose.", AgentRole.Planner);

        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            $"PLANNER_EVIDENCE_REQUEST: {payload}", AgentRole.Planner);

        Assert.Equal(absent, parsed);
        Assert.Null(parsed.Directive);
        Assert.False(parsed.IsMalformed);
    }

    [Xunit.Theory]
    [Xunit.InlineData("nonexistent store")]
    [Xunit.InlineData("not-json")]
    [Xunit.InlineData("none1")]
    [Xunit.InlineData("noneMore")]
    [Xunit.InlineData("none+extra")]
    [Xunit.InlineData("")]
    [Xunit.InlineData("{none}")]
    [Xunit.InlineData("[none]")]
    public void ParseHumanInputRequest_OtherProse_KeepsMalformedDiagnostic(string payload)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            $"PLANNER_EVIDENCE_REQUEST: {payload}", AgentRole.Planner);

        Assert.True(parsed.IsMalformed);
        Assert.Null(parsed.Directive);
        Assert.StartsWith("Malformed PLANNER_EVIDENCE_REQUEST: payload is not valid JSON: ", parsed.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ParseHumanInputRequest_NoneWithHumanInput_EqualsAbsentLine()
    {
        const string humanInput = "HUMAN_INPUT: Which repository owns the artifact?";
        var absent = AgentOutputDirectives.ParseHumanInputRequest(humanInput, AgentRole.Planner);

        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            "PLANNER_EVIDENCE_REQUEST: none.\n" + humanInput, AgentRole.Planner);

        Assert.Equal(absent, parsed);
        Assert.NotNull(parsed.Directive);
        Assert.Equal("Which repository owns the artifact?", parsed.Directive.Question);
        Assert.False(parsed.IsMalformed);
    }

    [Xunit.Fact]
    public void ParseHumanInputRequest_DuplicateNone_KeepsMalformedDiagnostic()
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            "PLANNER_EVIDENCE_REQUEST: none.\nPLANNER_EVIDENCE_REQUEST: none.", AgentRole.Planner);

        Assert.True(parsed.IsMalformed);
        Assert.Null(parsed.Directive);
        Assert.Equal("Malformed PLANNER_EVIDENCE_REQUEST: exactly one evidence request may be emitted per round", parsed.Diagnostic);
    }

    [Xunit.Fact]
    public void ParseHumanInputRequest_NoneForDeveloper_KeepsRoleDiagnostic()
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            "PLANNER_EVIDENCE_REQUEST: none.", AgentRole.Developer);

        Assert.True(parsed.IsMalformed);
        Assert.Null(parsed.Directive);
        Assert.Equal("PLANNER_EVIDENCE_REQUEST is valid only for the Planner role", parsed.Diagnostic);
    }
}
