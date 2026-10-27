using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class PlannerBriefAmendmentRequestTests
{
    private const string Prefix = "PLANNER_BRIEF_AMENDMENT: ";
    private const string Evidence = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"parser-tests","availability":"post-implementation","owner":"acceptance-gate","needed":"passing parser tests","reason":"implementation must exist first","tests":["PlannerBriefAmendmentRequestTests"]}
        """;

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Parse_valid_amendment_returns_only_one_spec_clarification(bool withEvidence)
    {
        var output = Amendment();
        if (withEvidence)
            output += "\n" + Evidence + "\n" + Evidence.Replace("parser-tests", "replay-tests");

        var result = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);

        Xunit.Assert.False(result.IsMalformed, result.Diagnostic);
        var directive = Xunit.Assert.Single(result.Directives);
        Xunit.Assert.Same(directive, result.Directive);
        Xunit.Assert.Equal(HumanWaitKind.SpecClarification, directive.Kind);
        Xunit.Assert.Null(directive.PlannerEvidence);
        Xunit.Assert.Equal(0, result.EvidenceLineCount);
        Xunit.Assert.Contains("criterion 1", directive.Question);
        Xunit.Assert.Contains("Defect: Existing regression contradicts the criterion.", directive.Question);
        Xunit.Assert.Contains("Evidence: tests/ExistingTests.cs:12", directive.Question);
        Xunit.Assert.Contains("Proposed text: Preserve the existing behavior.", directive.Question);
        Xunit.Assert.Contains("Fallback: Narrow the criterion.", directive.Question);
        Xunit.Assert.False(string.IsNullOrWhiteSpace(directive.BlockerFingerprint));
    }

    public static IEnumerable<object?[]> InvalidFields()
    {
        foreach (var field in new[] { "criterion_index", "defect", "evidence", "proposed_text" })
            foreach (var value in new[] { null, "null", "\"\"", "\"   \"", "true", "{}", "[]" })
                yield return [field, value];
        foreach (var value in new[] { "0", "-1", "\"1\"", "1.5", "2147483648" })
            yield return ["criterion_index", value];
        foreach (var field in new[] { "defect", "evidence", "proposed_text", "fallback_text" })
            yield return [field, "42"];
        foreach (var value in new[] { "\"File.cs\"", "\"File.cs:0\"", "\"File.cs:-1\"", "\":12\"", "\" :12\"", "\"File.cs:line\"" })
            yield return ["evidence", value];
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(InvalidFields))]
    public void Parse_invalid_field_returns_a_named_malformed_diagnostic(string field, string? json)
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(Amendment(field, json), AgentRole.Planner);

        Xunit.Assert.True(result.IsMalformed);
        Xunit.Assert.StartsWith("Malformed PLANNER_BRIEF_AMENDMENT:", result.Diagnostic);
        Xunit.Assert.Contains(field, result.Diagnostic);
        Xunit.Assert.Null(result.Directive);
        Xunit.Assert.Empty(result.Directives);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("\"\"")]
    [Xunit.InlineData("\"   \"")]
    public void Parse_optional_fallback_accepts_absence_and_reports_it(string? json)
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(Amendment("fallback_text", json), AgentRole.Planner);

        Xunit.Assert.False(result.IsMalformed, result.Diagnostic);
        Xunit.Assert.Contains("Fallback: No fallback supplied.", Xunit.Assert.Single(result.Directives).Question);
    }

    [Xunit.Fact]
    public void Parse_windows_path_and_unknown_properties_preserves_only_named_fields()
    {
        var payload = ValidPayload();
        payload["evidence"] = @"C:\repo\File.cs:12";
        payload["goal_id"] = "untrusted-goal";
        payload["task_id"] = "untrusted-task";
        payload["brief_version"] = 99;
        var result = AgentOutputDirectives.ParseHumanInputRequest(Prefix + JsonSerializer.Serialize(payload), AgentRole.Planner);

        Xunit.Assert.False(result.IsMalformed, result.Diagnostic);
        var directive = Xunit.Assert.Single(result.Directives);
        Xunit.Assert.Contains(@"Evidence: C:\repo\File.cs:12", directive.Question);
        Xunit.Assert.DoesNotContain("untrusted", directive.Question);
        Xunit.Assert.Equal(ParseDirective(Amendment()).BlockerFingerprint, directive.BlockerFingerprint);
    }

    [Xunit.Theory]
    [Xunit.InlineData("[]")]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("true")]
    [Xunit.InlineData("not-json")]
    [Xunit.InlineData("{} trailing text")]
    public void Parse_invalid_payload_is_malformed(string payload)
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(Prefix + payload, AgentRole.Planner);

        Xunit.Assert.True(result.IsMalformed);
        Xunit.Assert.StartsWith("Malformed PLANNER_BRIEF_AMENDMENT:", result.Diagnostic);
    }

    [Xunit.Fact]
    public void Parse_multiple_amendments_and_conflicting_questions_are_malformed()
    {
        var multiple = AgentOutputDirectives.ParseHumanInputRequest(Amendment() + "\n" + Amendment(), AgentRole.Planner);
        var conflict = AgentOutputDirectives.ParseHumanInputRequest(Amendment() + "\nHUMAN_INPUT: Another question?", AgentRole.Planner);
        var ambiguity = AgentOutputDirectives.ParseHumanInputRequest(
            Amendment() + "\nHUMAN_INPUT: Another question?\n" + Evidence, AgentRole.Planner);

        Xunit.Assert.True(multiple.IsMalformed);
        Xunit.Assert.Contains("one amendment", multiple.Diagnostic);
        Xunit.Assert.True(conflict.IsMalformed);
        Xunit.Assert.Contains("conflicts", conflict.Diagnostic);
        Xunit.Assert.Equal("Malformed PLANNER_EVIDENCE_REQUEST: substantive HUMAN_INPUT and Planner evidence are ambiguous; use evidence lines for every requirement.", ambiguity.Diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("PLANNER_EVIDENCE_REQUEST: not-json")]
    [Xunit.InlineData("PLANNER_EVIDENCE_REQUEST: none\n" + Evidence)]
    public void Parse_amendment_does_not_bypass_invalid_evidence(string evidence)
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(Amendment() + "\n" + evidence, AgentRole.Planner);

        Xunit.Assert.True(result.IsMalformed);
        Xunit.Assert.StartsWith("Malformed PLANNER_EVIDENCE_REQUEST:", result.Diagnostic);
    }

    [Xunit.Fact]
    public void Parse_none_sentinels_wrapper_and_receipt_boundary_keep_existing_behavior()
    {
        var expected = ParseDirective(Amendment());
        var none = AgentOutputDirectives.ParseHumanInputRequest(
            "HUMAN_INPUT: none\n" + Amendment() + "\nPLANNER_EVIDENCE_REQUEST: none", AgentRole.Planner);
        var receipt = AgentOutputDirectives.ParseHumanInputRequest(
            "<!-- MCG_DURABLE_PLANNER_PLAN:BEGIN -->\n" + Amendment(), AgentRole.Planner);

        Xunit.Assert.Equal(expected, none.Directive);
        Xunit.Assert.Equal(expected.Question, AgentOutputDirectives.TryParseHumanInputRequest(Amendment()));
        Xunit.Assert.Equal(expected.Question, AgentOutputDirectives.TryParseHumanInputRequest(
            Amendment() + "\n" + Evidence + "\n" + Evidence.Replace("parser-tests", "replay-tests")));
        Xunit.Assert.Null(AgentOutputDirectives.TryParseHumanInputRequest(
            Evidence + "\n" + Evidence.Replace("parser-tests", "replay-tests")));
        Xunit.Assert.Null(receipt.Directive);
        Xunit.Assert.False(receipt.IsMalformed);
    }

    [Xunit.Fact]
    public void Fingerprint_uses_only_normalized_criterion_and_defect()
    {
        var original = ParseDirective(Amendment());
        var expected = original.BlockerFingerprint;
        Xunit.Assert.Equal(expected, original.QuestionFingerprint);
        foreach (var output in new[]
        {
            Amendment("evidence", "\"other.cs:99\""),
            Amendment("proposed_text", "\"Different proposal.\""),
            Amendment("fallback_text", null),
            Amendment("defect", "\" EXISTING   regression contradicts the CRITERION. \"")
        })
            Xunit.Assert.Equal(expected, ParseDirective(output).QuestionFingerprint);
        Xunit.Assert.Equal(expected, ParseDirective(Amendment("evidence", "\"other.cs:99\"")).BlockerFingerprint);
        Xunit.Assert.Equal(expected, ParseDirective(Amendment("proposed_text", "\"Different proposal.\"")).BlockerFingerprint);
        Xunit.Assert.Equal(expected, ParseDirective(Amendment("fallback_text", null)).BlockerFingerprint);
        Xunit.Assert.Equal(expected, ParseDirective(Amendment("defect", "\" EXISTING   regression contradicts the CRITERION. \"")).BlockerFingerprint);
        Xunit.Assert.NotEqual(expected, ParseDirective(Amendment("defect", "\"Different defect.\"")).BlockerFingerprint);
        Xunit.Assert.NotEqual(expected, ParseDirective(Amendment("criterion_index", "2")).BlockerFingerprint);
    }

    [Xunit.Fact]
    public void Planner_template_names_amendment_contract_and_ruling_lifecycle()
    {
        var template = AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner)[0];
        foreach (var fragment in new[] { "PLANNER_BRIEF_AMENDMENT:", "criterion_index (1-based)", "defect", "evidence (path:line)",
                     "proposed_text", "optional fallback_text", "report the amendment in blockers", "re-emit evidence lines after the ruling", "only the owner question" })
            Xunit.Assert.Contains(fragment, template);
    }

    private static HumanInputDirective ParseDirective(string output)
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);
        Xunit.Assert.False(result.IsMalformed, result.Diagnostic);
        return Xunit.Assert.Single(result.Directives);
    }

    private static Dictionary<string, object?> ValidPayload() => new()
    {
        ["criterion_index"] = 1,
        ["defect"] = "Existing regression contradicts the criterion.",
        ["evidence"] = "tests/ExistingTests.cs:12",
        ["proposed_text"] = "Preserve the existing behavior.",
        ["fallback_text"] = "Narrow the criterion."
    };

    private static string Amendment(string? field = null, string? json = null)
    {
        var payload = ValidPayload();
        if (field is not null)
        {
            if (json is null) payload.Remove(field);
            else payload[field] = JsonSerializer.Deserialize<JsonElement>(json);
        }
        return Prefix + JsonSerializer.Serialize(payload);
    }
}
