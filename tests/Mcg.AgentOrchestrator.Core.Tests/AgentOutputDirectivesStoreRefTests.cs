using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class AgentOutputDirectivesStoreRefTests
{
    private const string Locator = "0d8fe45d519133850d8fe45d51913385";
    private const string Reference = "goal-events:" + Locator + "#contains=feasibility-0d8fe45d51913385";

    [Fact]
    public void RetrievableCarriesTypedReference()
    {
        var parsed = Parse("retrievable", Reference);
        Assert.Null(parsed.Diagnostic);
        var reference = Assert.IsType<PlannerEvidenceStoreReference>(parsed.Directive!.StoreReference);
        Assert.Equal("goal-events", reference.Kind);
        Assert.Equal(Locator, reference.Locator);
        Assert.Equal("contains=feasibility-0d8fe45d51913385", reference.Selector);
        Assert.Equal("feasibility-receipt", reference.Name);
        Assert.Equal("store-ref: feasibility-receipt = " + Reference, reference.ToStoreRefLine());
        Assert.Equal(Parse("retrievable", includeReference: false).Directive!.Question, parsed.Directive.Question);
    }

    [Theory]
    [InlineData("never-recorded")]
    [InlineData("post-implementation")]
    public void OtherAvailabilityRejectsStoreRef(string availability)
    {
        var parsed = Parse(availability, Reference);
        Assert.Null(parsed.Directive);
        Assert.Equal($"Malformed PLANNER_EVIDENCE_REQUEST: store_ref requires availability=retrievable, not '{availability}'.",
            parsed.Diagnostic);
    }

    [Theory]
    [InlineData("not a reference")]
    [InlineData("")]
    [InlineData("goal-events:id#contains=one\nstore-ref: other = operator-evidence:operator-evidence/x.md")]
    public void InvalidReferenceHasSpecificDiagnostic(string value)
    {
        var parsed = Parse("retrievable", value);
        Assert.Null(parsed.Directive);
        Assert.Equal($"Malformed PLANNER_EVIDENCE_REQUEST: store_ref does not parse as <kind>:<locator>[#<selector>]: '{value}'.",
            parsed.Diagnostic);
    }

    [Fact]
    public void NonStringReferenceIsMalformed()
    {
        var parsed = Parse("retrievable", 42);
        Assert.Null(parsed.Directive);
        Assert.Equal("Malformed PLANNER_EVIDENCE_REQUEST: store_ref does not parse as <kind>:<locator>[#<selector>]: '42'.",
            parsed.Diagnostic);
    }

    [Fact]
    public void ReferenceDoesNotReplaceRequiredStore()
    {
        var parsed = Parse("retrievable", Reference, includeStore: false);
        Assert.Null(parsed.Directive);
        Assert.Equal("Malformed PLANNER_EVIDENCE_REQUEST: retrievable evidence must name a non-empty store.", parsed.Diagnostic);
    }

    [Theory]
    [InlineData(" A___B / C ", "a-b-c")]
    [InlineData("!", "evidence")]
    [InlineData("0123456789012345678901234567890123456789-more", "0123456789012345678901234567890123456789")]
    public void ReferenceNameIsSafeAndDeterministic(string key, string expected)
    {
        Assert.Equal(expected, PlannerEvidenceStoreReference.DeriveName(key));
    }

    [Fact]
    public void PlannerInstructionsNameTheOptionalField()
    {
        Assert.Contains("For an orchestrator record, add store_ref as <kind>:<locator>[#<selector>].",
            string.Join("\n", AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner)));
    }

    private static HumanInputDirectiveParseResult Parse(string availability, object? reference = null,
        bool includeReference = true, bool includeStore = true)
    {
        var payload = new Dictionary<string, object?>
        {
            ["criterion_index"] = 1, ["evidence_key"] = "Feasibility Receipt", ["availability"] = availability,
            ["needed"] = "historical event", ["reason"] = "worker cannot reach the store"
        };
        if (availability == "retrievable" && includeStore) payload["store"] = ".orchestrator/goal-events/" + Locator + ".jsonl";
        if (availability == "post-implementation") payload["owner"] = "operator";
        if (includeReference) payload["store_ref"] = reference;
        return AgentOutputDirectives.ParseHumanInputRequest("PLANNER_EVIDENCE_REQUEST: " + JsonSerializer.Serialize(payload), AgentRole.Planner);
    }
}
