using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure routing and prompt rendering over immutable inputs.
public sealed class ConductorAuthorAnswerRoutingTests
{
    private const string Scenario = "Seed two goals in the fixture store and assert the stub flag.";
    private const string Canonical = "supply-reproducing-scenario: " + Scenario;

    [Xunit.Theory]
    [Xunit.InlineData(Canonical, null, Canonical)]
    [Xunit.InlineData("Supply the reproducing scenario - " + Scenario, null, Canonical)]
    [Xunit.InlineData("re-scope: Assert the stub flag only.", "acceptance-weakening", "re-scope: Assert the stub flag only.")]
    [Xunit.InlineData("OPERATOR-OWNED", "acceptance-weakening", "OPERATOR-OWNED")]
    [Xunit.InlineData("The criterion is feasible as written.", "acceptance-weakening", "The criterion is feasible as written.")]
    public void Feasibility_dispositions_choose_route_and_parser_ready_text(
        string answer, string? reason, string submittedText)
    {
        var route = ConductorAuthorAnswerRouting.Decide(Item(AcceptanceCriterionFeasibility.ForkKind),
            answer, ["src/Runtime.cs:12"]);

        Xunit.Assert.Equal(reason, route.EscalationReason);
        Xunit.Assert.Equal(submittedText, route.SubmittedText);
        if (reason is null)
        {
            Xunit.Assert.True(AcceptanceCriterionFeasibility.TryParseDisposition(route.SubmittedText, out var disposition));
            Xunit.Assert.Equal(FeasibilityDisposition.ReproducingScenario, disposition.Kind);
            Xunit.Assert.Equal(Scenario, disposition.Value);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("supply-reproducing-scenario: Seed two goals and assert the stub flag.", false, "missing-evidence")]
    [Xunit.InlineData("supply-reproducing-scenario: The flaky test blocks the run. Skip it.", true, "acceptance-weakening")]
    public void Scenario_answers_keep_owner_class_and_evidence_checks(
        string answer, bool hasEvidence, string reason)
    {
        var route = ConductorAuthorAnswerRouting.Decide(Item(AcceptanceCriterionFeasibility.ForkKind),
            answer, hasEvidence ? ["src/Runtime.cs:12"] : []);

        Xunit.Assert.Equal(reason, route.EscalationReason);
        Xunit.Assert.Equal(answer, route.SubmittedText);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("runtime")]
    [Xunit.InlineData("worker-spec-clarification")]
    public void Other_fork_kinds_preserve_owner_check_and_raw_text(string? forkKind)
    {
        var item = Item(forkKind);
        string[] answers = ["Use Windows", "Waive criterion 3",
            "The flaky test blocks the run. Skip it.", "supply-reproducing-scenario: x"];
        string[][] referenceSets = [["src/Runtime.cs:12"], []];
        foreach (var answer in answers)
        foreach (var references in referenceSets)
        {
            var route = ConductorAuthorAnswerRouting.Decide(item, answer, references);

            Xunit.Assert.Equal(ConductorAuthorOwnerClassCheck.Evaluate(item.Question, answer, references),
                route.EscalationReason);
            Xunit.Assert.Equal(answer, route.SubmittedText);
        }
    }

    [Xunit.Fact]
    public void Feasibility_guidance_follows_question_and_other_prompts_stay_identical()
    {
        var feasibility = Render(AcceptanceCriterionFeasibility.ForkKind);
        var runtime = Render("runtime");
        var noFork = Render(null);
        var lines = feasibility.Split('\n');
        var questionLine = Array.FindIndex(lines, line => line.StartsWith("Question: ", StringComparison.Ordinal));

        Xunit.Assert.True(questionLine >= 0);
        Xunit.Assert.Equal(ConductorAuthorPrompt.FeasibilityDispositions, lines[questionLine + 1]);
        Xunit.Assert.Equal(runtime, noFork);
        Xunit.Assert.DoesNotContain("supply-reproducing-scenario", runtime);
        Xunit.Assert.Equal(runtime, feasibility.Replace("\n" + ConductorAuthorPrompt.FeasibilityDispositions, ""));
    }

    private static string Render(string? forkKind) =>
        ConductorAuthorPrompt.Render(new ConductorAuthorRoundInput(Item(forkKind), "brief", "spec", null));

    private static ConductorAuthorItem Item(string? forkKind) => new(
        OperatorAnswerTargetKind.Clarification, "c1", "g1",
        "Criterion infeasible for Developer: test deployment", forkKind);
}
