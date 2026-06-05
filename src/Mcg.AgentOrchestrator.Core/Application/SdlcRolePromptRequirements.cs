namespace Mcg.AgentOrchestrator.Core;

internal static class SdlcRolePromptRequirements
{
    public static IReadOnlyList<string> Build(AgentRole role)
    {
        return role switch
        {
            AgentRole.Planner =>
            [
                "## Planner Requirements",
                "- Produce a concrete implementation plan with likely files or modules to inspect and the smallest viable change boundary.",
                "- Challenge ambiguous requirements; name assumptions, sequencing risks, and explicit stop conditions.",
                "- Define falsifiable proof Developer, Tester, and Reviewer must provide before acceptance.",
                "- Do not return a generic SDLC checklist or restate the user's goal as a plan."
            ],
            AgentRole.Researcher =>
            [
                "## Researcher Requirements",
                "- Lead with concrete findings tied to repository-local files, APIs, tests, or primary external sources; include file paths, commands, URLs, or symbol names for each material claim.",
                "- Prefer source reads that exclude generated artifacts, for example rg with **/bin/** and **/obj/** exclusions; inspect generated output only when it is the subject of the task.",
                "- Identify integration constraints, dependency risks, contradictory evidence, and unknowns that affect implementation.",
                "- Separate confirmed facts from inferences; call out stale, missing, or low-confidence evidence and the consequence for implementation.",
                "- Include the exact repository-local commands or file inspections used as research evidence when available.",
                "- If no research is needed, say so briefly and explain why using repository evidence.",
                "- Do not restate the goal as research output or rely on unsourced assumptions."
            ],
            AgentRole.Developer =>
            [
                "## Developer Requirements",
                "- Implement only the requested behavior and keep edits scoped.",
                "- Report changed files and the behavior each change enables.",
                "- Run focused verification when practical and include exact command names.",
                "- Leave follow-up work explicit when the dashboard or orchestrator blocks the ideal path."
            ],
            AgentRole.Tester =>
            [
                "## Tester Requirements",
                "- Derive a focused verification matrix from the requested behavior, changed files, and known risks.",
                "- Run or attempt the exact verification commands relevant to this task.",
                "- Keep test discovery focused on source and intentional test assets; avoid treating bin/obj output as changed source.",
                "- Report command, exit code, and concise output summary for every check.",
                "- Tie each pass/fail conclusion to concrete evidence: command output, changed file behavior, manual smoke steps, or exact reproduction data.",
                "- Try to falsify the implementation with at least one negative or edge case when practical, and state what failure would have looked like.",
                "- If a command cannot run, include the exact failure text and the environment condition.",
                "- Do not ask for shell restoration unless an attempted command actually failed because of execution access."
            ],
            AgentRole.Reviewer =>
            [
                "## Reviewer Requirements",
                "- Review in code-review form: findings first, ordered by severity.",
                "- Ground every finding or no-finding claim in file paths, task evidence, command output, or missing tests.",
                "- Ignore generated bin/obj output unless the reviewed change explicitly targets generated artifacts.",
                "- Challenge generic summaries by checking implementation evidence against verification evidence before accepting.",
                "- State residual risk, test gaps, and whether acceptance is justified.",
                "- Do not approve based only on a summary from another role."
            ],
            _ => []
        };
    }

    public static string BuildPlainText(AgentRole role)
    {
        return string.Join(Environment.NewLine, Build(role));
    }
}
