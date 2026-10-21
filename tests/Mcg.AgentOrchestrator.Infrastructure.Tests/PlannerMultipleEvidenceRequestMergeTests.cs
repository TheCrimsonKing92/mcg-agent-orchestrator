using System.Text;
using Mcg.AgentOrchestrator.Core;

// Pure parser inputs; parallel-safe.
public sealed class PlannerMultipleEvidenceRequestMergeTests
{
    internal const string FirstRequest = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"gate-first","availability":"post-implementation","owner":"acceptance-gate","tests":["AlphaTests","BetaTests"],"needed":"Execute the first selection","reason":"Candidate execution is pending"}
        """;
    internal const string SecondRequest = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"gate-second","availability":"post-implementation","owner":"acceptance-gate","tests":["BetaTests","GammaTests.Row"],"needed":"Execute the second selection","reason":"Candidate execution is pending"}
        """;
    internal const string HistoricalRequest = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"history","availability":"retrievable","store":"events","store_ref":"goal:prior#receipt","needed":"Read receipt","reason":"Worker cannot reach store"}
        """;
    internal const string OperatorRequest = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":2,"evidence_key":"live-check","availability":"post-implementation","owner":"operator","needed":"Observe live behavior","reason":"Operator owns this check"}
        """;
    internal static string GateRequests => FirstRequest + "\n" + SecondRequest;
    internal static string MixedRequests => HistoricalRequest + "\n" + FirstRequest + "\n" + OperatorRequest;

    [Fact]
    public void MixedBatch_PreservesAllIdentitiesAndPayloads()
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(MixedRequests, AgentRole.Planner);
        Assert.False(parsed.IsMalformed, parsed.Diagnostic);
        Assert.Null(parsed.Directive); // Singular consumers cannot accidentally select a sibling.
        Assert.Equal(3, parsed.Directives.Count);
        Assert.Equal(new[] { "history", "gate-first", "live-check" }, parsed.Directives.Select(item => item.PlannerEvidence!.EvidenceKey));
        var history = parsed.Directives[0];
        Assert.Equal(HumanWaitKind.PlannerPrerequisiteEvidence, history.Kind);
        Assert.Equal("events", history.PlannerEvidence!.Store);
        Assert.Equal("prior", history.StoreReference!.Locator);
        Assert.Equal(new[] { "AlphaTests", "BetaTests" }, parsed.Directives[1].TestNames);
        Assert.Equal("operator", parsed.Directives[2].EvidenceOwner);
    }

    [Fact]
    public void OverlappingSelectors_DoNotMergeDistinctRequirements()
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(GateRequests, AgentRole.Planner);
        Assert.False(parsed.IsMalformed, parsed.Diagnostic);
        Assert.Equal(2, parsed.Directives.Count);
        Assert.Equal(new[] { "AlphaTests", "BetaTests" }, parsed.Directives[0].TestNames);
        Assert.Equal(new[] { "BetaTests", "GammaTests.Row" }, parsed.Directives[1].TestNames);
    }

    [Fact]
    public void ExactDuplicate_IsIdempotentAfterRecognizedNormalization()
    {
        var second = FirstRequest.Replace("acceptance-gate", " ACCEPTANCE ", StringComparison.Ordinal);
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(FirstRequest + "\n" + second, AgentRole.Planner);
        Assert.False(parsed.IsMalformed, parsed.Diagnostic);
        Assert.Single(parsed.Directives);
        Assert.Equal(2, parsed.EvidenceLineCount);
    }

    [Theory]
    [InlineData("Execute the first selection", "different need")]
    [InlineData("Candidate execution is pending", "different reason")]
    [InlineData("acceptance-gate", "operator")]
    [InlineData("AlphaTests", "OtherTests")]
    public void SameIdentityWithChangedPayload_RejectsWholeBatch(string oldValue, string newValue)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(FirstRequest + "\n" +
            FirstRequest.Replace(oldValue, newValue, StringComparison.Ordinal), AgentRole.Planner);
        Assert.True(parsed.IsMalformed);
        Assert.Contains("Conflicting", parsed.Diagnostic);
        Assert.Empty(parsed.Directives);
    }

    [Theory]
    [InlineData("events", "different-store")]
    [InlineData("goal:prior#receipt", "goal:other#receipt")]
    public void HistoricalPayloadConflict_RejectsWholeBatch(string oldValue, string newValue)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(HistoricalRequest + "\n" +
            HistoricalRequest.Replace(oldValue, newValue, StringComparison.Ordinal), AgentRole.Planner);
        Assert.True(parsed.IsMalformed);
        Assert.Empty(parsed.Directives);
    }

    [Theory]
    [InlineData("PLANNER_EVIDENCE_REQUEST: {bad}")]
    [InlineData("PLANNER_EVIDENCE_REQUEST: none")]
    [InlineData("HUMAN_INPUT: Decide the owner")]
    public void InvalidFinalItemOrAmbiguity_PublishesNoPartialBatch(string suffix)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(MixedRequests + "\n" + suffix, AgentRole.Planner);
        Assert.True(parsed.IsMalformed);
        Assert.Empty(parsed.Directives);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("no")]
    [InlineData("not needed")]
    public void HumanAbsenceSentinels_AllowBatchAndEvidenceNone(string sentinel)
    {
        Assert.Equal(3, AgentOutputDirectives.ParseHumanInputRequest(MixedRequests + "\nHUMAN_INPUT: " + sentinel, AgentRole.Planner).Directives.Count);
        var absent = AgentOutputDirectives.ParseHumanInputRequest("PLANNER_EVIDENCE_REQUEST: none\nHUMAN_INPUT: " + sentinel, AgentRole.Planner);
        Assert.False(absent.IsMalformed);
        Assert.Empty(absent.Directives);
    }

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(null)]
    public void WrongRole_RejectsPluralContract(AgentRole? role)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(MixedRequests, role);
        Assert.True(parsed.IsMalformed);
        Assert.Empty(parsed.Directives);
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(17, true)]
    public void CountBound_AppliesBeforeDeduplication(int count, bool malformed)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(string.Join("\n", Enumerable.Repeat(FirstRequest, count)), AgentRole.Planner);
        Assert.Equal(malformed, parsed.IsMalformed);
        Assert.Equal(malformed ? 0 : 1, parsed.Directives.Count);
    }

    [Theory]
    [InlineData(65536, false)]
    [InlineData(65537, true)]
    public void Utf8Bound_CountsMultibytePayload(int size, bool malformed)
    {
        var payload = FirstRequest["PLANNER_EVIDENCE_REQUEST: ".Length..];
        var extra = size - Encoding.UTF8.GetByteCount(payload);
        var padded = payload.Replace("Execute the first selection", "Execute the first selection" +
            new string('é', extra / 2) + new string('x', extra % 2), StringComparison.Ordinal);
        Assert.Equal(size, Encoding.UTF8.GetByteCount(padded));
        var parsed = AgentOutputDirectives.ParseHumanInputRequest("PLANNER_EVIDENCE_REQUEST: " + padded, AgentRole.Planner);
        Assert.Equal(malformed, parsed.IsMalformed);
        Assert.Equal(malformed ? 0 : 1, parsed.Directives.Count);
    }

    [Fact]
    public void DurableReceiptCopies_DoNotCountOrInvalidateDecisionStream()
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(MixedRequests +
            "\n<!-- MCG_DURABLE_PLANNER_PLAN:BEGIN -->\nPLANNER_EVIDENCE_REQUEST: invalid", AgentRole.Planner);
        Assert.False(parsed.IsMalformed);
        Assert.Equal(3, parsed.EvidenceLineCount);
        Assert.Equal(3, parsed.Directives.Count);
    }

    [Fact]
    public void CombinedByteBound_IsAppliedBeforeDuplicateReduction()
    {
        var payload = FirstRequest["PLANNER_EVIDENCE_REQUEST: ".Length..];
        payload = payload.Replace("Execute the first selection", "Execute the first selection" +
            new string('x', 32768 - Encoding.UTF8.GetByteCount(payload)), StringComparison.Ordinal);
        Assert.Equal(32768, Encoding.UTF8.GetByteCount(payload));
        var first = "PLANNER_EVIDENCE_REQUEST: " + payload;
        var valid = AgentOutputDirectives.ParseHumanInputRequest(first + "\n" + first, AgentRole.Planner);
        Assert.False(valid.IsMalformed, valid.Diagnostic);
        Assert.Single(valid.Directives);
        var invalid = AgentOutputDirectives.ParseHumanInputRequest(first + "\n" +
            first.Replace("Execute the first selection", "Execute the first selectionx", StringComparison.Ordinal), AgentRole.Planner);
        Assert.True(invalid.IsMalformed);
        Assert.Contains("65536", invalid.Diagnostic);
        Assert.Empty(invalid.Directives);
    }

    [Fact]
    public void SameIdentityWithDifferentAvailability_RejectsEntireBatch()
    {
        var historical = HistoricalRequest.Replace("\"history\"", "\"gate-first\"", StringComparison.Ordinal);
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(FirstRequest + "\n" + historical, AgentRole.Planner);
        Assert.True(parsed.IsMalformed);
        Assert.Contains("Conflicting", parsed.Diagnostic);
        Assert.Empty(parsed.Directives);
    }

    [Fact]
    public void ForgedBriefClaim_RejectsPayload()
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(FirstRequest.Replace("{", "{\"brief_version\":99,", StringComparison.Ordinal), AgentRole.Planner);
        Assert.True(parsed.IsMalformed);
        Assert.Contains("host-bound", parsed.Diagnostic);
        Assert.Empty(parsed.Directives);
    }
}
