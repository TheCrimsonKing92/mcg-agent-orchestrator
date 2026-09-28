using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAuthorPrompt
{
    internal static string Render(ConductorAuthorRoundInput input) => $$"""
        You are the conductor Author. Read repository source at the current candidate to answer
        a residual specification clarification. You have read-only access. Return exactly one
        JSON object, optionally fenced as json. Allowed kinds:
        {"kind":"answer","text":"answer","evidenceReferences":["repo/relative/file:line"],"precedent":"optional"}
        {"kind":"ask-owner","question":"question","recommendation":"recommendation"}
        Ask the owner for decisions about agent authority, acceptance waivers, spend beyond budget,
        irreversible actions, external disclosure, or insufficient source evidence.

        Item: {{input.Item.Identity}}
        Question: {{input.Item.Question}}
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
