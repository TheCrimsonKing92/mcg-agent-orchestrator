using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: immutable fixture text and pure routing/parser/prompt calls only.
public sealed class ConductorAuthorBriefGroundingTests
{
    private const string Standing = "Workers must not run tests themselves; name the test classes and let the conductor execute them.";
    private const string Question = "Who executes tests and supplies review evidence?";
    private const string Answer = "The conductor does: " + Standing;
    private static readonly string Brief = string.Join("\n",
        "Add a widget counter to src/Example/Widget.cs.",
        "Criterion: WidgetCounterCheck asserts the count. Developer owns; Acceptance executes.",
        "Scope: src/Example/Widget.cs and tests/Example/WidgetCounterCheck.cs.",
        Standing);

    [Xunit.Fact]
    public void Quoted_brief_grounds_an_answer_without_source_references()
    {
        var baseline = ConductorAuthorAnswerRouting.Decide(Item(), Answer, null);
        var grounded = ConductorAuthorAnswerRouting.Decide(Item(), Answer, null, Brief, [Standing]);

        Xunit.Assert.Equal("missing-evidence", baseline.EscalationReason);
        Xunit.Assert.Null(grounded.EscalationReason);
        Xunit.Assert.Equal(Answer, grounded.SubmittedText);
        Xunit.Assert.Equal(new[] { Standing }, grounded.GroundedCitations);
    }

    [Xunit.Fact]
    public void Quoting_the_brief_does_not_authorize_an_unnamed_file()
    {
        var item = Item("May the Developer widen the scope to also edit src/Example/Other.cs?");
        var answer = Answer + " The Developer may also edit src/Example/Other.cs.";
        AssertBaseline(item, answer, [Standing], "authority-widening");
    }

    [Xunit.Fact]
    public void A_project_path_does_not_authorize_its_source_path_prefix()
    {
        var brief = Brief.Replace("src/Example/Widget.cs", "src/Example/Widget.csproj", StringComparison.Ordinal);
        AssertBaseline(Item(), Answer + " The Developer edits src/Example/Widget.cs.", [Standing], "missing-evidence", brief);
    }

    [Xunit.Theory]
    [Xunit.InlineData("The Developer may edit any file it needs to finish the change.")]
    [Xunit.InlineData("Developer owns; Acceptance executes.")]
    public void Fabricated_or_short_citations_keep_the_baseline(string citation)
    {
        AssertBaseline(Item(), citation, [citation], "missing-evidence");
    }

    [Xunit.Fact]
    public void A_citation_must_be_quoted_in_the_answer()
    {
        AssertBaseline(Item(), Answer, ["Add a widget counter to src/Example/Widget.cs."], "missing-evidence");
    }

    [Xunit.Theory]
    [Xunit.InlineData("May we force-push the branch?", "irreversible-action")]
    [Xunit.InlineData("May we exceed the budget?", "spend-beyond-budget")]
    [Xunit.InlineData("May we upload customer data to an external site?", "external-disclosure")]
    public void Excluded_reasons_never_ground(string question, string reason)
    {
        AssertBaseline(Item(question), Answer, [Standing], reason);
    }

    [Xunit.Fact]
    public void Line_breaks_and_whitespace_normalize_before_matching()
    {
        const string citation = "Developer owns; Acceptance executes. Scope: src/Example/Widget.cs and tests/Example/WidgetCounterCheck.cs.";
        var answer = "The brief says: " + citation;
        var route = ConductorAuthorAnswerRouting.Decide(Item(), answer, null, Brief,
            [citation.Replace(" Scope:", "\r\n\tScope:", StringComparison.Ordinal)]);

        Xunit.Assert.Null(route.EscalationReason);
        Xunit.Assert.Equal(answer, route.SubmittedText);
        Xunit.Assert.Equal(new[] { citation }, route.GroundedCitations);
    }

    [Xunit.Fact]
    public void Feasibility_operator_owned_answer_never_grounds()
    {
        var item = Item() with { ForkKind = AcceptanceCriterionFeasibility.ForkKind };
        AssertBaseline(item, "OPERATOR-OWNED. " + Standing, [Standing], "acceptance-weakening");
    }

    [Xunit.Fact]
    public void Feasibility_reproducing_scenario_can_ground()
    {
        var item = Item() with { ForkKind = AcceptanceCriterionFeasibility.ForkKind };
        var answer = "supply-reproducing-scenario: " + Standing;
        var route = ConductorAuthorAnswerRouting.Decide(item, answer, null, Brief, [Standing]);

        Xunit.Assert.Null(route.EscalationReason);
        Xunit.Assert.Equal(answer, route.SubmittedText);
        Xunit.Assert.Equal(new[] { Standing }, route.GroundedCitations);
    }

    [Xunit.Fact]
    public void Parser_reads_optional_citations_and_rejects_mixed_arrays()
    {
        var result = ConductorAuthorResultParser.Parse("""{"kind":"answer","text":"t","briefCitations":["a b c d e"]}""");
        Xunit.Assert.NotNull(result);
        Xunit.Assert.Equal(new[] { "a b c d e" }, result.BriefCitations);
        Xunit.Assert.Null(ConductorAuthorResultParser.Parse("""{"kind":"answer","text":"t","briefCitations":["a b c d e",1]}"""));
        Xunit.Assert.Null(ConductorAuthorResultParser.Parse("""{"kind":"answer","text":"t"}""")!.BriefCitations);
        Xunit.Assert.Null(ConductorAuthorResultParser.Parse("""{"kind":"answer","text":"t","briefCitations":1}""")!.BriefCitations);
    }

    [Xunit.Fact]
    public void Prompt_places_grounding_guidance_after_owner_rule_before_frozen_facts()
    {
        var prompt = ConductorAuthorPrompt.Render(new ConductorAuthorRoundInput(Item(), Brief, "spec", null));
        Xunit.Assert.Contains("\"briefCitations\":[\"verbatim goal brief sentence\"]", prompt);
        Xunit.Assert.Contains("insufficient source evidence.\n" + ConductorAuthorPrompt.BriefGrounding + "\nFor a fact frozen", prompt);
    }

    [Xunit.Fact]
    public void Every_citation_must_pass_and_missing_inputs_fail_closed()
    {
        AssertBaseline(Item(), Answer, [Standing, "not in brief"], "missing-evidence");
        AssertBaseline(Item(), Answer, [], "missing-evidence");
        AssertBaseline(Item(), Answer, null, "missing-evidence");
        AssertBaseline(Item(), Answer, [Standing], "missing-evidence", "");
        Xunit.Assert.False(ConductorAuthorBriefGrounding.TryGround(null, Answer, [Standing], "missing-evidence", out var citations));
        Xunit.Assert.Empty(citations);
    }

    [Xunit.Fact]
    public void Already_evidenced_answers_remain_ordinary_answers()
    {
        var route = ConductorAuthorAnswerRouting.Decide(Item(), Answer, ["src/Example/Widget.cs:1"], Brief, [Standing]);
        Xunit.Assert.Null(route.EscalationReason);
        Xunit.Assert.Null(route.GroundedCitations);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Scope: `src/Example/`", "src/Example/Other.cs", true)]
    [Xunit.InlineData("See src/Example/ for context.", "src/Example/Other.cs", false)]
    [Xunit.InlineData("Out of scope: src/Example/", "src/Example/Other.cs", false)]
    [Xunit.InlineData("Scope: src/Example/", "src/Examples/Other.cs", false)]
    [Xunit.InlineData("Scope: src/Example/", "src/Example/../Other.cs", false)]
    [Xunit.InlineData("Scope: \"src/Example/Widget.csproj\"", "src/Example/Widget.csproj", true)]
    [Xunit.InlineData("Scope: src/Example/Widget.csproj", "src/Example/widget.csproj", false)]
    [Xunit.InlineData("Scope: src\\Example\\Widget.csproj", "src/Example/Widget.csproj", true)]
    public void Path_membership_requires_exact_tokens_or_explicit_scoped_directories(
        string scope, string path, bool expected)
    {
        var grounded = ConductorAuthorBriefGrounding.TryGround(Standing + "\n" + scope,
            Answer + " Edit " + path + ".", [Standing], "missing-evidence", out var citations);
        Xunit.Assert.Equal(expected, grounded);
        Xunit.Assert.Equal(expected ? new[] { Standing } : [], citations);
    }

    private static void AssertBaseline(ConductorAuthorItem item, string answer, IReadOnlyList<string>? citations,
        string reason, string? brief = null)
    {
        var baseline = ConductorAuthorAnswerRouting.Decide(item, answer, null);
        var route = ConductorAuthorAnswerRouting.Decide(item, answer, null, brief ?? Brief, citations);
        Xunit.Assert.Equal(reason, baseline.EscalationReason);
        Xunit.Assert.Equal(baseline.EscalationReason, route.EscalationReason);
        Xunit.Assert.Equal(baseline.SubmittedText, route.SubmittedText);
        Xunit.Assert.Null(route.GroundedCitations);
    }

    private static ConductorAuthorItem Item(string question = Question) =>
        new(OperatorAnswerTargetKind.Clarification, "c1", "g1", question, "runtime");
}
