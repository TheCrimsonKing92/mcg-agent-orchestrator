using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: renders immutable input and reads the verified repository document.
public sealed class ConductorAuthorPromptFrozenFactProcedureTests
{
    [Xunit.Fact]
    public void Prompt_embeds_complete_normative_procedure_without_document_dependency()
    {
        var input = new ConductorAuthorRoundInput(
            new ConductorAuthorItem(OperatorAnswerTargetKind.HumanInput, "item", "goal", "question", null),
            "brief", "spec", null);
        AssertSelfContainedProcedure(ConductorAuthorPrompt.Render(input));
    }

    internal static void AssertSelfContainedProcedure(string prompt)
    {
        // The expected authority comes from the home document, independently of prompt constants.
        var document = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), "docs", "frozen-fact-rulings.md"));
        var procedure = document.Split("## Four-step procedure", StringSplitOptions.None)[1]
            .Split("## Shared text format", StringSplitOptions.None)[0];
        var normalized = Regex.Replace(prompt, @"\s+", " ");
        Xunit.Assert.Contains(Regex.Replace(procedure, @"\s+", " ").Trim(), normalized);
        Xunit.Assert.Contains("runnable against the candidate output", normalized);
        Xunit.Assert.DoesNotContain("docs/frozen-fact-rulings.md", prompt);
        Xunit.Assert.DoesNotContain("## Operator exemplars", prompt);
    }

    [Xunit.Fact]
    public void Procedure_has_step_zero_fixture_rule_and_all_twelve_exemplars()
    {
        var document = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), "docs", "frozen-fact-rulings.md"));
        Xunit.Assert.Contains("0. " + ConductorAuthorPrompt.StepZero, document);
        Xunit.Assert.Contains("2. Allow changing only the assertions or path pieces that pin that behavior. " +
            ConductorAuthorPrompt.FixtureInputs, document);
        Xunit.Assert.Contains("Removing an assertion is never an allowed change.", document);
        Xunit.Assert.True(document.IndexOf("0. " + ConductorAuthorPrompt.StepZero, StringComparison.Ordinal) <
            document.IndexOf("1. Confirm the fact", StringComparison.Ordinal));
        var exemplars = document.Split("## Operator exemplars", StringSplitOptions.None)[1];
        var firstCells = exemplars.ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.StartsWith('|')).Select(line => line.Split('|')[1].Trim()).ToArray();
        string[] expected = ["7a / 5330f7f6", "8 / 5330f7f6", "9 / 7b911976", "10 / c64b0239",
            "10a / c64b0239", "11 / c0839d66", "11a / c0839d66", "12 / ed2bf978", "13 / 14f2e9b8",
            "14 / 64b8636e", "16 / 107b7e93", "17 / 7d406294"];
        foreach (var ruling in expected)
            Xunit.Assert.Single(firstCells, cell => cell == ruling);
    }
}
