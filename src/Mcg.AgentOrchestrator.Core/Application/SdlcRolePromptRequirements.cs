namespace Mcg.AgentOrchestrator.Core;

internal static class SdlcRolePromptRequirements
{
    private const string IntakeRiskLabelsMarker = "risk labels:";

    public static IReadOnlyList<string> Build(AgentRole role)
    {
        return role switch
        {
            AgentRole.Ideation =>
            [
                "## Ideation Requirements",
                "- Ground every proposed idea in the provided evidence context; cite the specific source (e.g. \"loop-health shows 9% rework\" or \"3 escalations about X\").",
                "- Rank ideas by expected value: highest value and lowest effort improvements first.",
                "- Include scope, value, effort, and risk fields for each idea.",
                "- Do not propose hand-wavy ideas without cited evidence; every rationale must tie to an observable metric or named source.",
                "- Do not modify repository files; ideation output is a ranked proposal list only."
            ],
            AgentRole.Planner =>
            [
                "## Planner Requirements",
                "- Produce a concrete implementation plan with likely files or modules to inspect and the smallest viable change boundary.",
                "- Challenge ambiguous requirements; name assumptions, sequencing risks, and explicit stop conditions.",
                "- Define falsifiable proof Developer, Tester, and Reviewer must provide before acceptance.",
                "- If repository evidence disproves the goal premise, report `blockers: premise-invalid - <fact and evidence>` and stop before proposing implementation.",
                "- Do not return a generic SDLC checklist or restate the user's goal as a plan.",
                "- Do not modify repository files; implementation belongs to the Developer task."
            ],
            AgentRole.Researcher =>
            [
                "## Researcher Requirements",
                "- Lead with concrete findings tied to repository-local files, APIs, tests, or primary external sources; include file paths, commands, URLs, or symbol names for each material claim.",
                "- Prefer /api/source-survey?max=8 when available, or source reads that exclude generated artifacts such as **/bin/** and **/obj/**; inspect generated output only when it is the subject of the task.",
                "- Identify integration constraints, dependency risks, contradictory evidence, and unknowns that affect implementation.",
                "- Separate confirmed facts from inferences; call out stale, missing, or low-confidence evidence and the consequence for implementation.",
                "- Include the exact repository-local commands or file inspections used as research evidence when available.",
                "- If no research is needed, say so briefly and explain why using repository evidence.",
                "- If repository evidence disproves the goal premise, report `blockers: premise-invalid - <fact and evidence>` and stop before downstream implementation.",
                "- Do not restate the goal as research output or rely on unsourced assumptions.",
                "- Do not modify repository files; implementation belongs to the Developer task."
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
                "- A verification command that is killed, times out, or produces no results file is an environment/plumbing outcome, NOT a test failure: report `tests: inconclusive - <current-round evidence>` with `blockers: none`, and never restate a prior round's conclusion as this round's evidence.",
                "- Keep each verification command bounded in wall time: build once as its own step, then run tests with a narrow filter and no rebuild; do not bundle a build and a broad or full-suite test run into a single command.",
                "- Do not ask for shell restoration unless an attempted command actually failed because of execution access.",
                "- You may build and run tests but must not modify source files."
            ],
            AgentRole.Reviewer =>
            [
                "## Reviewer Requirements",
                "- Review in code-review form: findings first, ordered by severity.",
                "- First-review breadth: cover every in-scope changed file end-to-end before your first verdict, and state your coverage explicitly - every changed file examined, or name exactly what you could not examine and why. A SHALLOW finding surfacing in a later round on unchanged in-scope code is a coverage defect. Going DEEPER on later rounds is desired, not a defect: once surface findings resolve, deeper analysis layers (concurrency, durability, fault ordering, security) are exactly what review is for. Never withhold a finding you have identified, in any round.",
                "- Ground every finding or no-finding claim in file paths, task evidence, command output, or missing tests.",
                "- For independent changed-file scope checks, use git diff main...HEAD; do not use two-dot diffs, git diff HEAD, git status, or working-tree-only comparisons as scope verdict evidence.",
                "- Staleness policy: branch-behind-main alone is NOT a blocker; the deterministic acceptance gate rebases and verifies the integrated result. Staleness may block only with concrete integration-risk evidence: merge-tree conflicts, semantic overlap with landed changes in the same files, or a diff that no longer applies. Otherwise record staleness as advisory.",
                "- Ignore generated bin/obj output unless the reviewed change explicitly targets generated artifacts.",
                "- Challenge generic summaries by checking implementation evidence against verification evidence before accepting.",
                "- If your only blocker is missing executed focused test evidence, put `evidence-request: <ProjectAlias>: <FullyQualifiedName~TestClass or TestClass1,TestClass2>` in WORKER_RESULT, using `Core.Tests` or `Infrastructure.Tests`; do not request full or unfiltered suites.",
                "- If a brief criterion contradicts the pre-change contract observable on main, report it as `suspected-defective-criterion` instead of enforcing it as a blocker.",
                "- Treat the structured Review Convergence Scope as authoritative: actively re-check OPEN findings and net-new diff code; carry RESOLVED findings forward without re-review unless this round's diff touched that finding's exact structural anchor.",
                "- Emit every finding in the one-line `findings` JSON field with stable_id, open|resolved state, structural location (file + region + optional hunk), and description. Emit exact prior anchors touched by this round in `touched_anchors`; similar defects on newly introduced code get new stable IDs.",
                "- State residual risk, test gaps, and whether acceptance is justified.",
                "- Do not approve based only on a summary from another role.",
                "- Do not modify repository files; implementation belongs to the Developer task."
            ],
            _ => []
        };
    }

    public static IReadOnlyList<string> Build(AgentRole role, TaskComplexity complexity)
    {
        return complexity == TaskComplexity.Complex ? Build(role) : BuildCompact(role);
    }

    public static IReadOnlyList<string> Build(
        AgentRole role,
        TaskComplexity complexity,
        bool includeHighRiskReviewerEnumerationContract)
    {
        var requirements = Build(role, complexity);
        if (!includeHighRiskReviewerEnumerationContract || role != AgentRole.Reviewer)
        {
            return requirements;
        }

        var lines = requirements.ToList();
        lines.Add("- High-risk review enumeration contract: if your verdict is needs-work, list all acceptance-blocking findings you discover in one ranked pass (P1/P2); do not stop at the first blocker because the Developer receives exactly one findings list per cycle.");
        lines.Add("- Keep the WORKER_RESULT blockers field format unchanged; put the complete ranked blocker list in that existing field.");
        return lines;
    }

    public static string BuildPlainText(AgentRole role)
    {
        return string.Join(Environment.NewLine, Build(role));
    }

    public static string BuildPlainText(AgentRole role, TaskComplexity complexity)
    {
        return string.Join(Environment.NewLine, Build(role, complexity));
    }

    public static string BuildPlainText(
        AgentRole role,
        TaskComplexity complexity,
        bool includeHighRiskReviewerEnumerationContract)
    {
        return string.Join(Environment.NewLine, Build(role, complexity, includeHighRiskReviewerEnumerationContract));
    }

    public static bool HasHighRiskOrComplexIntakeRiskLabel(Goal goal)
    {
        return EnumerateStoredIntakeRiskLabels(goal).Any(label =>
            label.Equals("high-risk", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("complex", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> BuildCompact(AgentRole role)
    {
        return role switch
        {
            AgentRole.Ideation =>
            [
                "## Ideation Requirements",
                "- Ground every idea in provided evidence; cite source with specifics (e.g. \"loop-health shows 9% rework\").",
                "- Rank by value, include scope/value/effort/risk; reject hand-wavy ideas with no evidence citation.",
                "- Do not modify repository files; output is a ranked proposal list only."
            ],
            AgentRole.Planner =>
            [
                "## Planner Requirements",
                "- Produce a concrete plan with likely files or modules, smallest viable change boundary, assumptions, and stop conditions.",
                "- Define falsifiable proof for downstream roles; do not return a generic checklist.",
                "- If repository evidence disproves the goal premise, report `blockers: premise-invalid - <fact and evidence>` and stop before proposing implementation.",
                "- Do not modify repository files; implementation belongs to the Developer task."
            ],
            AgentRole.Researcher =>
            [
                "## Researcher Requirements",
                "- Lead with repository evidence: file paths, symbols, APIs, tests, primary sources, and commands or file inspections.",
                "- Prefer /api/source-survey?max=8 when available; otherwise exclude generated output such as **/bin/** and **/obj/** unless the task targets it.",
                "- Separate confirmed facts from inferences, risks, and unknowns.",
                "- If repository evidence disproves the goal premise, report `blockers: premise-invalid - <fact and evidence>` and stop before downstream implementation.",
                "- Do not modify repository files; implementation belongs to the Developer task."
            ],
            AgentRole.Developer =>
            [
                "## Developer Requirements",
                "- Keep edits scoped and report changed files plus behavior enabled.",
                "- Run focused verification when practical and name exact commands.",
                "- Call out blockers or follow-up work explicitly."
            ],
            AgentRole.Tester =>
            [
                "## Tester Requirements",
                "- Derive focused checks from the requested behavior and report concrete evidence.",
                "- Run or attempt exact commands; include exit code and concise output summary.",
                "- Cover edge/negative cases when practical and avoid treating bin/obj output as changed source.",
                "- A killed/timed-out/no-results verification is an environment outcome, not a failure: report `tests: inconclusive - <current-round evidence>` with `blockers: none`, and do not reuse a prior round's conclusion as evidence.",
                "- Keep each command bounded: build once, then run narrow no-rebuild test filters; never bundle a build and a broad test run in one command.",
                "- You may build and run tests but must not modify source files."
            ],
            AgentRole.Reviewer =>
            [
                "## Reviewer Requirements",
                "- Review in code-review form: findings first, ordered by severity, with file/evidence references.",
                "- First-review breadth: cover every in-scope changed file end-to-end before your first verdict; state coverage or name exactly what you could not examine. A SHALLOW later-round finding on unchanged in-scope code is a coverage defect; going DEEPER in later rounds (concurrency, durability, fault ordering) is desired. Never withhold a finding you have identified.",
                "- Use git diff main...HEAD for independent scope checks; reject two-dot or working-tree-only scope verdict evidence.",
                "- Staleness policy: branch-behind-main alone is NOT a blocker; the deterministic acceptance gate rebases and verifies the integrated result. Staleness may block only with concrete integration-risk evidence: merge-tree conflicts, semantic overlap with landed changes in the same files, or a diff that no longer applies. Otherwise record staleness as advisory.",
                "- If your only blocker is missing executed focused test evidence, put `evidence-request: <ProjectAlias>: <FullyQualifiedName~TestClass or TestClass1,TestClass2>` in WORKER_RESULT, using `Core.Tests` or `Infrastructure.Tests`; do not request full or unfiltered suites.",
                "- If a brief criterion contradicts the pre-change contract observable on main, report it as `suspected-defective-criterion` instead of enforcing it as a blocker.",
                "- Treat structured Review Convergence Scope as authoritative: re-check OPEN findings and new diff code; carry RESOLVED findings without re-review unless their exact anchor was touched. Emit one-line `findings` and `touched_anchors` JSON fields; similar defects on new code get new stable IDs.",
                "- Challenge generic summaries by comparing implementation evidence with verification evidence.",
                "- Ignore generated bin/obj output unless targeted; state residual risk, test gaps, and acceptance recommendation.",
                "- Do not modify repository files; implementation belongs to the Developer task."
            ],
            _ => []
        };
    }

    private static IEnumerable<string> EnumerateStoredIntakeRiskLabels(Goal goal)
    {
        foreach (var evt in goal.Timeline.Where(evt => evt.Kind == ProgressKind.GoalPolicyDecision))
        {
            var markerIndex = evt.Message.IndexOf(IntakeRiskLabelsMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            var labelsText = evt.Message[(markerIndex + IntakeRiskLabelsMarker.Length)..].Trim();
            var sentenceEnd = labelsText.IndexOf('.');
            if (sentenceEnd >= 0)
            {
                labelsText = labelsText[..sentenceEnd];
            }

            foreach (var label in labelsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                yield return label;
            }
        }
    }
}
