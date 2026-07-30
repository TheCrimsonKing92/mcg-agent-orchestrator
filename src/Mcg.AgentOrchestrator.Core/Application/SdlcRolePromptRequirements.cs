namespace Mcg.AgentOrchestrator.Core;

internal static class SdlcRolePromptRequirements
{
    private const string IntakeRiskLabelsMarker = "risk labels:";
    internal const int ReviewerComplexRequirementsMaxChars = 3556;
    internal const int ReviewerCompactRequirementsMaxChars = 2546;

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
                "### 1. Spec compliance (do this first)",
                "- Walk the RefinedSpec acceptance criteria in order, one at a time. For each criterion report met, not-met, or not-verifiable-from-diff with file+line or concrete task evidence in `criteria_verdicts`.",
                "- A not-met criterion is a blocking finding with `category: spec-compliance`. If a criterion contradicts the pre-change contract observable on main, report `category: spec-defect` so it escalates to the operator instead of enforcing it against the implementation.",
                "- Ground every finding or no-finding claim in file paths, task evidence, command output, or missing tests.",
                "### 2. Code quality (only after section 1)",
                "- Review in code-review form: findings first, ordered by severity. First-review breadth must cover every in-scope changed file end-to-end; state coverage or name exactly what you could not examine. A SHALLOW later-round finding on unchanged code is a coverage defect; going DEEPER later (concurrency, durability, fault ordering, security) is desired. Never withhold an identified finding.",
                "- For independent scope checks use git diff main...HEAD; do not use two-dot, HEAD-only, status, or working-tree-only comparisons. Branch-behind-main alone is NOT a blocker; require concrete merge conflict, semantic overlap, or non-applying diff evidence, otherwise record staleness as advisory.",
                "- Ignore generated bin/obj output unless the reviewed change explicitly targets generated artifacts.",
                "- Challenge generic summaries by checking implementation evidence against verification evidence before accepting.",
                "- If your only blocker is missing executed focused test evidence, put `evidence-request: <ProjectAlias>: <FullyQualifiedName~TestClass or TestClass1,TestClass2>` in WORKER_RESULT, using `Core.Tests` or `Infrastructure.Tests`; do not request full or unfiltered suites.",
                "- Classify findings with `spec-compliance`, `spec-defect`, `correctness`, `test-evidence`, `test-coverage`, `code-quality`, or `operator-owned`; mixed source/test findings are correctness work for Developer.",
                "- Treat the structured Review Convergence Scope as authoritative: re-check OPEN findings and net-new diff code; carry RESOLVED findings without re-review unless this round's diff touched the exact structural anchor.",
                "- Emit every finding in one-line `findings` JSON with stable_id, state, required severity, category, structural location, and description. Missing severity is blocking; missing category is unspecified. Any remediable open blocking finding requires `needs-work` plus its exact blocker in `blockers`; reserve `fail` for a non-remediable stop. When no open blocking findings remain, use `verdict: pass` and `blockers: none` even when open advisory findings remain; advisories belong only in `findings`. Emit exact prior anchors touched in `touched_anchors`; similar defects on new code get new stable IDs.",
                "- State residual risk, test gaps, and whether acceptance is justified; do not approve from another role's summary alone.",
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
        lines.Add("- High-risk review enumeration contract: if any open blocking findings are remediable, use `needs-work` and list all acceptance-blocking findings in one ranked pass (P1/P2); do not stop at the first blocker because the Developer receives exactly one findings list per cycle.");
        lines.Add("- Keep the WORKER_RESULT blockers field format unchanged: put exactly the complete ranked open blocking set in `blockers`; when none remain, use `verdict: pass` and `blockers: none`. Keep advisories only in `findings`.");
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
                "### 1. Spec compliance (do this first)",
                "- Walk RefinedSpec acceptance criteria in order. Record each as met, not-met, or not-verifiable-from-diff with file+line evidence in `criteria_verdicts`; not-met uses `category: spec-compliance`, while a criterion contradicting main uses `category: spec-defect`.",
                "### 2. Code quality (only after section 1)",
                "- Review findings first by severity with evidence. Cover every in-scope file before the first verdict; state gaps. Later SHALLOW findings on unchanged code are coverage defects; deeper concurrency/durability/fault analysis is desired. Never withhold an identified finding.",
                "- Use git diff main...HEAD for scope. Branch-behind-main alone is NOT a blocker; block only on concrete conflict, semantic overlap, or a non-applying diff.",
                "- If your only blocker is missing executed focused test evidence, put `evidence-request: <ProjectAlias>: <FullyQualifiedName~TestClass or TestClass1,TestClass2>` in WORKER_RESULT, using `Core.Tests` or `Infrastructure.Tests`; do not request full or unfiltered suites.",
                "- Classify findings as `spec-compliance`, `spec-defect`, `correctness`, `test-evidence`, `test-coverage`, `code-quality`, or `operator-owned`.",
                "- Treat structured Review Convergence Scope as authoritative: re-check OPEN findings and new diff code; carry RESOLVED findings unless their exact anchor was touched. Findings require severity and category; missing severity is blocking and missing category unspecified. Any remediable open blocker requires `needs-work` plus exact `blockers`; reserve `fail` for non-remediable stops. When none remain, use `verdict: pass` and `blockers: none` even with open advisories. Emit `touched_anchors`; new-code defects get new stable IDs.",
                "- Challenge generic summaries by comparing implementation and verification evidence.",
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
