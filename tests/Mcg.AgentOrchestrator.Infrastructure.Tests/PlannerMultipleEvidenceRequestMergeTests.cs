using Mcg.AgentOrchestrator.Core;

// Pure parser inputs; safe to run in parallel.
public sealed class PlannerMultipleEvidenceRequestMergeTests
{
    internal const string FirstRequest = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"gate-first","availability":"post-implementation","owner":"acceptance-gate","tests":["AlphaTests","BetaTests"],"needed":"Execute the first selection","reason":"Candidate execution is pending"}
        """;
    internal const string SecondRequest = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"gate-second","availability":"post-implementation","owner":"acceptance-gate","tests":["BetaTests","GammaTests.Row"],"needed":"Execute the second selection","reason":"Candidate execution is pending"}
        """;
    private const string OperatorRequest = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":2,"evidence_key":"family-review","availability":"post-implementation","owner":"operator","needed":"Review the candidate with another model family","reason":"Operator owns the pending review"}
        """;
    private const string Rejection =
        "Malformed PLANNER_EVIDENCE_REQUEST: exactly one evidence request may be emitted per round.";
    internal static string GateRequests => FirstRequest + "\n" + SecondRequest;

    [Fact]
    public void Parse_AllGateRequests_RetainsIdentitySelectionsAndOrderedEvidence()
    {
        var first = AgentOutputDirectives.ParseHumanInputRequest(FirstRequest, AgentRole.Planner).Directive!;
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            "## Plan\nImplement the candidate.\n" + GateRequests + "\nEnd plan.", AgentRole.Planner);

        Assert.False(parsed.IsMalformed);
        Assert.Null(parsed.Diagnostic);
        var directive = Assert.IsType<HumanInputDirective>(parsed.Directive);
        Assert.Equal(HumanWaitKind.ProspectiveAcceptanceEvidence, directive.Kind);
        Assert.Equal("acceptance-gate", directive.EvidenceOwner);
        Assert.Equal(new[] { "AlphaTests", "BetaTests", "GammaTests.Row" }, directive.TestNames);
        Assert.Equal(HumanInputRequest.BuildPlannerEvidenceFingerprint(1, "gate-first"), directive.QuestionFingerprint);
        Assert.Equal(first.BlockerFingerprint, directive.BlockerFingerprint);
        Assert.Equal(first.StoreReference, directive.StoreReference);
        Assert.StartsWith(first.Question, directive.Question, StringComparison.Ordinal);
        Assert.Contains("Merged evidence requests: 1) gate-first: Execute the first selection; " +
            "2) gate-second: Execute the second selection.", directive.Question, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(IncompatibleRequests))]
    public void Parse_IncompatibleRequests_RetainsExactRejection(string output)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);

        Assert.True(parsed.IsMalformed);
        Assert.Null(parsed.Directive);
        Assert.Equal(Rejection, parsed.Diagnostic);
    }

    public static IEnumerable<object[]> IncompatibleRequests()
    {
        const string retrievable = """
            PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"history","availability":"retrievable","store":"events","needed":"Read receipt","reason":"Worker cannot read store"}
            """;
        const string neverRecorded = """
            PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"missing","availability":"never-recorded","needed":"Produce receipt","reason":"No receipt exists"}
            """;
        yield return [FirstRequest + "\n" + SecondRequest.Replace("\"criterion_index\":1", "\"criterion_index\":0", StringComparison.Ordinal)];
        yield return [retrievable + "\n" + retrievable];
        yield return [FirstRequest + "\n" + retrievable];
        yield return [OperatorRequest + "\n" + OperatorRequest];
        yield return [FirstRequest + "\nPLANNER_EVIDENCE_REQUEST: none."];
        yield return ["PLANNER_EVIDENCE_REQUEST: none.\nPLANNER_EVIDENCE_REQUEST: none."];
        yield return [FirstRequest + "\n" + OperatorRequest];
        yield return [GateRequests + "\n" + OperatorRequest];
        yield return [FirstRequest + "\nPLANNER_EVIDENCE_REQUEST: not-json"];
        yield return [FirstRequest + "\n" + neverRecorded];
        yield return [FirstRequest + "\n" + SecondRequest.Replace("acceptance-gate", "Developer", StringComparison.Ordinal)];
        yield return [FirstRequest + "\n" + SecondRequest.Replace("acceptance-gate", "Reviewer", StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Reviewer)]
    public void Parse_NonPlannerRole_RetainsExactMultiRequestRejection(AgentRole? role)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(GateRequests, role);

        Assert.Null(parsed.Directive);
        Assert.Equal(Rejection, parsed.Diagnostic);
    }

    [Fact]
    public void Parse_GateOwnerCaseAndSelectorCase_PreservesOrdinalUnion()
    {
        var second = SecondRequest.Replace("acceptance-gate", "ACCEPTANCE-GATE", StringComparison.Ordinal)
            .Replace("GammaTests.Row", "alphaTests", StringComparison.Ordinal);
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(FirstRequest + "\n" + second, AgentRole.Planner);

        Assert.False(parsed.IsMalformed);
        Assert.Equal(new[] { "AlphaTests", "BetaTests", "alphaTests" }, parsed.Directive!.TestNames);
        Assert.Equal("acceptance-gate", parsed.Directive.EvidenceOwner);
    }

    [Fact]
    public void Parse_NoNamedTests_RetainsNullSelections()
    {
        var output = GateRequests.Replace("\"tests\":[\"AlphaTests\",\"BetaTests\"],", "", StringComparison.Ordinal)
            .Replace("\"tests\":[\"BetaTests\",\"GammaTests.Row\"],", "", StringComparison.Ordinal);
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);

        Assert.False(parsed.IsMalformed);
        Assert.NotNull(parsed.Directive);
        Assert.Null(parsed.Directive.TestNames);
    }

    [Fact]
    public void Parse_SingleMalformedRequest_RetainsJsonDiagnostic()
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest("PLANNER_EVIDENCE_REQUEST: not-json", AgentRole.Planner);

        Assert.Null(parsed.Directive);
        Assert.True(parsed.IsMalformed);
        Assert.StartsWith("Malformed PLANNER_EVIDENCE_REQUEST: payload is not valid JSON:", parsed.Diagnostic);
    }

    [Fact]
    public void Parse_SingleRequestWithProse_RetainsEveryField()
    {
        var expected = AgentOutputDirectives.ParseHumanInputRequest(FirstRequest, AgentRole.Planner);
        var actual = AgentOutputDirectives.ParseHumanInputRequest("Plan prose.\n" + FirstRequest + "\nEnd.", AgentRole.Planner);

        Assert.False(actual.IsMalformed);
        Assert.Equal(expected.Diagnostic, actual.Diagnostic);
        Assert.Equal(expected.Directive!.TestNames, actual.Directive!.TestNames);
        Assert.Equal(expected.Directive with { TestNames = null }, actual.Directive with { TestNames = null });
    }

    [Fact]
    public void Parse_NoRequest_ReturnsNoDirectiveOrDiagnostic()
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest("## Plan\nImplement the candidate.", AgentRole.Planner);

        Assert.Null(parsed.Directive);
        Assert.Null(parsed.Diagnostic);
        Assert.False(parsed.IsMalformed);
    }
}
