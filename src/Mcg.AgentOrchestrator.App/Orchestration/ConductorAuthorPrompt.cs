using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAuthorPrompt
{
    public const string BriefGrounding = "When explicit goal brief text settles the question, answer it yourself: copy each settling sentence verbatim into text and list it in briefCitations. The conductor submits that answer without the owner only when every citation appears verbatim in the goal brief and the answer names no file the brief does not name; otherwise it goes to the owner.";
    public const string StepZero = "Every allowed change must be a literal edit the Developer can make without running tests.";
    public const string FixtureInputs = "An allowed change may also edit fixture inputs (literals, arguments or setup lines) that feed the pinned behavior, provided every assertion stays byte-identical.";
    public const string FeasibilityDispositions = "This is a feasibility clarification: answer with kind answer and start text with exactly one disposition the clarification parser accepts. When the criterion text stays unchanged, because it is feasible as written or because the question's premise is wrong, start with `supply-reproducing-scenario: ` and give the worker-accessible scenario or the corrected premise with path:line evidence. Use `re-scope: <replacement criterion>` or `OPERATOR-OWNED` only to change the criterion; those go to the owner.";

    internal static string Render(ConductorAuthorRoundInput input) => $$"""
        You are the conductor Author. Read repository source at the current candidate to answer
        a residual specification clarification. You have read-only access. Return exactly one
        JSON object, optionally fenced as json. Allowed kinds:
        {"kind":"answer","text":"answer","evidenceReferences":["repo/relative/file:line"],"precedent":"optional","briefCitations":["verbatim goal brief sentence"]}
        {"kind":"ask-owner","question":"question","recommendation":"recommendation"}
        {"kind":"frozen-fact-ruling","frozenClasses":["Class"],"amendedFacts":[{"fact":"Class.Method","file":"tests/File.cs","allowedChange":"bounded change"}],"basis":"step 1","unmodified":"step 3","diffCheck":"step 4","evidenceReferences":["tests/File.cs:1","src/File.cs:1"]}
        Ask the owner for decisions about agent authority, acceptance waivers, spend beyond budget,
        irreversible actions, external disclosure, or insufficient source evidence.
        {{BriefGrounding}}
        For a fact frozen by a criterion, use frozen-fact-ruling only when the goal brief and current
        source show all four steps: the fact builds exactly the behavior deliberately removed or moved;
        only assertions or path pieces pinning that behavior change; name every other assertion in the
        fact and every other fact in every frozen class as unmodified; give a runnable candidate diff check.
        {{StepZero}}
        {{FixtureInputs}}
        Otherwise ask-owner, or answer with a refusal that points to the owner.
        Removing an assertion is never an allowed change.
        Read docs/frozen-fact-rulings.md for the full procedure and worked exemplars.

        Item: {{input.Item.Identity}}
        Question: {{input.Item.Question}}{{(input.Item.ForkKind == AcceptanceCriterionFeasibility.ForkKind ? "\n" + FeasibilityDispositions : string.Empty)}}
        Goal brief:
        {{input.GoalBrief}}
        Refined spec:
        {{input.RefinedSpec}}
        Matching precedent:
        {{JsonSerializer.Serialize(input.MatchingPrecedent)}}
        Operator lessons:
        {{ConductorLessonSelector.Render(input.Lessons)}}
        """;
}
