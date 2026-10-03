namespace Mcg.AgentOrchestrator.Core;

internal static class SdlcRolePromptRequirements
{
    private const string NegativeControlApplicabilityRule = "- Request a negative control only when a criterion says a test must fail against main or today's code; never for refactor, documentation or move-only criteria whose tests must pass unmodified.";
    private static readonly string NegativeControlPolicyFileClause = $", or a repository policy file the conductor accepts ({string.Join(", ", NegativeControlRevertPolicy.RevertablePolicyFiles)})";
    private static readonly string NegativeControlRequestRule = "- RED proof: evidence_request negative_control:\"revert-src\"; if tests use members the goal adds, add revert_paths naming only the src files implementing the behavior" + NegativeControlPolicyFileClause + ". For one hunk inside a file whose other changes the tests need, use mutation:{path,old_text,new_text} instead. After negative-control-compile-red, narrow revert_paths or ask for an operator record; never repeat the same request.";
    private const string IntakeRiskLabelsMarker = "risk labels:";
    private const string TesterFindingRequestRule =
        "- Put failures in structured findings JSON with stable IDs and locations; carry distinct prior findings until resolved. Open blocking test-evidence MUST carry evidence_request:{selections:[{test_project,test_class}]}.";
    private const string ResearcherStdoutOnlyContract =
        "- Stdout is the only channel the orchestrator reads: print the complete research artifact as your final message. Never write a plan file or any other file, and never reply with only a summary or a file path.";
    private const string TesterReceiptFirstContract =
        "- RECEIPT-FIRST: Before emitting `evidence_request`, inspect each supplied receipt. Accept a passing receipt only when its candidate identity (commit/SHA or proven equivalent worktree/dispatch candidate identity/content) and selection coverage (`test_project`/`test_class`) match this round; cite its identity/path, mark covered obligations closed, and do not request them again. A missing, stale, mismatched, or unreadable receipt is unproven; name that disposition. When it conflicts with stale narrative, the verified matching receipt controls.";
    private const string TesterInconclusiveReceiptContract =
        "- A matching timeout/killed/no-results receipt is inconclusive, not a pass, and closes nothing. For that same candidate, a claimed discriminating change must change the serialized `evidence_request` selection and the work it executes: only `test_project` and `test_class` define that selection. Description text, unsupported fields, and cosmetic selector differences are not a changed run. You may narrow with supported `test_class` syntax only while preserving unclosed coverage. If no justified changed selection is expressible, retain the open inconclusive finding and route the needed observation to its full-gate/operator/acceptance owner without emitting the identical request. A stale-candidate receipt may use the same selection against the current candidate. Do not lengthen deadlines by default or invent a cause. Keep full-gate/operator-owned obligations open and owned; do not widen conditional criteria.";
    private const string TesterCompactReceiptFirstContract =
        "- RECEIPT-FIRST: Inspect/cite. Pass iff candidate SHA/proven equivalent worktree/dispatch + `test_project`/`test_class` match; close covered/no repeat. Missing/stale/mismatched/unreadable=unproven; evidence > stale narrative.";
    private const string TesterCompactInconclusiveReceiptContract =
        "- Timeout/killed/no-results is inconclusive, not a pass. Rerun only on changed execution; only `test_project` and `test_class` define that selection. unsupported fields; cosmetic selector differences don't; full-gate/operator/acceptance owns rest. Never extend deadline/invent cause.";
    internal const string DeveloperSelfCheckProcedure =
        "- Before WORKER_RESULT, for each criterion naming Developer as owner, open the test written/changed for it: its assertion must check the specific named outcome, not a weaker property, and fail on pre-change code. If both hold, report proven with the test in criteria_self_check; else strengthen it or report unmet and set assigned_scope_complete: false.";
    internal const string SelfCheckAttestation =
        "- For each proven entry in Developer Criteria Self-Check on a criterion you attest, check the named test exists in the candidate and its assertion checks the named outcome; otherwise raise a finding against that criterion.";
    // Goal 79061df4 adds one attestation line; preserve existing budget headroom.
    // Goal 6552eebc adds the accepted policy-file clause; raise overflowing budgets by exactly its length.
    internal static readonly int ReviewerComplexRequirementsMaxChars = 4961 + SelfCheckAttestation.Length + 2 + NegativeControlPolicyFileClause.Length;
    internal static readonly int ReviewerCompactRequirementsMaxChars = 3870 + SelfCheckAttestation.Length + 2 + NegativeControlPolicyFileClause.Length;
    internal static readonly int TesterCompactRequirementsMaxChars = 3590 + SelfCheckAttestation.Length + 2 + NegativeControlPolicyFileClause.Length;

    private const string ReviewerExhaustiveFindingsContract =
        "- Every `needs-work` verdict must inspect the complete candidate diff supplied for the current round and enumerate every blocking finding; never stop after the first. Put each in verdict prose and one semicolon-delimited `blockers` token (no literal semicolons), with file:line, severity `blocking` from `blocking|advisory`, and a violated acceptance criterion ID/label or clear quote/paraphrase. For deletions cite an old/new diff line; for file-wide defects, the defining line. Deduplicate only the same defect identity (stable_id preferred; otherwise normalized file/region+criterion+meaning), union criterion references, retain the most precise current anchor, and never merge by shared file, criterion, or cause. Order by violated criterion index, normalized file path, line/region, then stable_id; `blockers` uses that order. End needs-work verdict prose with this exact standalone line immediately before WORKER_RESULT: `no other blocking findings exist in this diff`. Keep it outside `blockers`.";

    private const string ReviewerDefectContract =
        "- `REVIEW DEFECT` is a later-round blocker demonstrably present in an earlier reviewed complete candidate diff. Self-check for it; absent historical comparison evidence prevents this label, not current-diff review.";
    private const string DeveloperDeferredTestsExactForm =
        "- Format: `tests: deferred - ClassA, ClassB` or backticked names.";

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
                "- Inspect the supplied goal evidence and current repository context before synthesizing the plan.",
                "- Produce a concrete implementation plan with likely files or modules to inspect and the smallest viable change boundary.",
                "- Map every acceptance criterion by number and include target files, ownership/lifecycle, external and edge contracts, risks, integration seams, and focused verification.",
                "- Challenge ambiguous requirements; name assumptions, sequencing risks, and explicit stop conditions.",
                "- Define falsifiable proof Developer, Tester, and Reviewer must provide before acceptance.",
                "- If repository evidence disproves the goal premise, report `blockers: premise-invalid - <fact and evidence>` and stop before proposing implementation.",
                "- Do not return a generic SDLC checklist or restate the user's goal as a plan.",
                "- Cite files without a line RANGE. `File.cs`, `File.cs:442`, `File.cs#L442`, and `File.cs::Symbol` are accepted; `File.cs:442-479` is REJECTED and discards your entire plan, because the validator treats the whole string including the range as the path and finds no such file. Before emitting, scan your output for `.cs:<digits>-<digits>` and replace each with its single start line.",
                "- Do not modify repository files; implementation belongs to the Developer task."
            ],
            AgentRole.Researcher =>
            [
                "## Researcher Requirements",
                ResearcherStdoutOnlyContract,
                "- Do NOT build the solution or run tests. A live conductor holds the built assemblies, so your build will fail on a file lock and consume your whole session on lock recovery. Inspect source, git history, and committed receipts instead. If a question can only be settled by executing tests, say so and name the exact test classes so the Tester or a Conductor-side evidence request can settle it.",
                "- Produce the durable research artifact under the required Current source findings, Prior goal evidence, Upstream capabilities, and Likely seams and risks headings.",
                "- Lead with concrete findings tied to repository-local files, APIs, tests, or primary external sources; include file paths, commands, URLs, or symbol names for each material claim.",
                "- Prefer source-survey.md in the context directory when present, or source reads that exclude generated artifacts such as **/bin/** and **/obj/**; inspect generated output only when it is the subject of the task.",
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
                "- First honor eligible typed convergence for this candidate: return fresh passed focused receipts; clean worktree, no replay or invented edits.",
                "- No edits: start `NO_CHANGE:` line with reason; report `tests: deferred` naming test classes for conductor.",
                DeveloperDeferredTestsExactForm,
                DeveloperSelfCheckProcedure,
                "- Implement only the requested behavior and keep edits scoped.",
                "- Before editing, name the failing test and quote its assertion output.",
                "- Report changed files and the behavior each change enables.",
                "- Run focused verification when practical and include exact command names.",
                "- Leave follow-up work explicit when the orchestrator blocks the ideal path."
            ],
            AgentRole.Tester =>
            [
                "## Tester Requirements",
                NegativeControlApplicabilityRule,
                NegativeControlRequestRule,
                TesterReceiptFirstContract,
                TesterInconclusiveReceiptContract,
                "- PRIMARY PATH: prefer a Conductor-side run over executing tests yourself. Emit evidence_request with selections of test_project and test_class inside your findings JSON, and report tests: deferred naming what you requested. The Conductor runs that selection and returns receipts. This is faster, avoids composing runner commands for this platform and runner, and keeps large test output out of your context. Execute directly only when a test-class selection cannot settle the question.",
                "- A Developer-owned finding (`correctness`, `spec-compliance`, `code-quality`, `test-coverage`) that is still waiting on execution MUST carry `evidence_request`; the conductor runs that selection once per candidate before it re-dispatches Developer. Read the brief's `evidence_index` line to tell the two apart: `state=pending-execution` means no run exists for this `candidate_sha` yet and is NOT a pass, `state=executed-on-candidate` means the receipt for that exact candidate is already in hand. A finding with no `evidence_request` has no `evidence_index` line at all: it is writable source work now, never a pass. Never call the source correct, and never resolve a finding, from narrative or from a run that did not happen; keep it open and either cite the receipt or request one.",
                "- Derive a focused verification matrix from the requested behavior, changed files, and known risks.",
                "- Listed in matching OPERATOR-OWNED/ACCEPTANCE-GATE-OWNED: not-verifiable; else attest met/not-met.",
                SelfCheckAttestation,
                "- Run or attempt the exact verification commands relevant to this task.",
                "- Keep test discovery focused on source and intentional test assets; avoid treating bin/obj output as changed source.",
                "- Report command, exit code, and concise output summary for every check.",
                "- Tie each pass/fail conclusion to concrete evidence: command output, changed file behavior, manual smoke steps, or exact reproduction data.",
                "- Try to falsify the implementation with at least one negative or edge case when practical, and state what failure would have looked like.",
                "- If a command cannot run, include the exact failure text and the environment condition.",
                TesterFindingRequestRule,
                "- Reuse a carried finding's stable_id. Keep its original location unless the code moved: when the system-derived round diff touched the prior anchor, keep the stable_id and report the defect's current location. Otherwise a different location is rejected. If a new stable_id describes the same open anchor, reuse the canonical stable_id instead.",
                "- A verification command that is killed, times out, or produces no results file is an environment/plumbing outcome, NOT a test failure: report `tests: inconclusive - <current-round evidence>` with `blockers: none`, and never restate a prior round's conclusion as this round's evidence.",
                "- Keep each verification command bounded in wall time: build once as its own step, then run tests with a narrow filter and no rebuild; do not bundle a build and a broad or full-suite test run into a single command.",
                "- Do not ask for shell restoration unless an attempted command actually failed because of execution access.",
                "- You may build and run tests but must not modify source files.",
                "- Prefer scripts/Invoke-TestSummary.ps1 with -Target <csproj>, -Filter FullyQualifiedName~<Class>, and -NoBuild over hand-composed runner commands. It emits one compact MTP_TERMINAL_SUMMARY line instead of full runner output. MTP filters require FullyQualifiedName~Class or Name~Method-symbol syntax and reject DisplayName text and a bare class name."
            ],
            AgentRole.Reviewer =>
            [
                "## Reviewer Requirements",
                NegativeControlApplicabilityRule,
                NegativeControlRequestRule,
                "### 1. Spec compliance (do this first)",
                "- Walk criteria in order: report met, not-met, or not-verifiable with file+line or concrete task evidence in `criteria_verdicts`. Use zero-based `criterion_index` values (0..N-1), with exactly one entry for every criterion.",
                "- Listed in matching OPERATOR-OWNED/ACCEPTANCE-GATE-OWNED: not-verifiable; else attest met/not-met.",
                SelfCheckAttestation,
                "- A not-met criterion is a blocking finding with `category: spec-compliance`. If a criterion contradicts the pre-change contract observable on main, report `category: spec-defect` so it escalates to the operator instead of enforcing it against the implementation.",
                "- Ground every finding or no-finding claim in file paths, task evidence, command output, or missing tests.",
                "### 2. Code quality (only after section 1)",
                "- Review in code-review form: findings first, ordered by severity. First-review breadth must cover every in-scope changed file end-to-end; state coverage or name exactly what you could not examine. A SHALLOW later-round finding on unchanged code is a coverage defect; going DEEPER later (concurrency, durability, fault ordering, security) is desired. Never withhold an identified finding.",
                "- For independent scope checks use git diff main...HEAD; do not use two-dot, HEAD-only, status, or working-tree-only comparisons. Branch-behind-main alone is NOT a blocker; require concrete merge conflict, semantic overlap, or non-applying diff evidence, otherwise record staleness as advisory.",
                "- Ignore generated bin/obj output unless the reviewed change explicitly targets generated artifacts.",
                "- Challenge generic summaries by checking implementation evidence against verification evidence before accepting.",
                "- Open blocking test-evidence MUST carry `evidence_request:{selections:[{test_project,test_class}]}`; other findings may include it. Use a test project from `config/acceptance-manifest.json` by label, file name, or path; never infer a request from prose.",
                "- Classify findings with `spec-compliance`, `spec-defect`, `correctness`, `test-evidence`, `test-coverage`, `code-quality`, `operator-owned`, or `acceptance-owned`; mixed source/test findings are correctness work for Developer.",
                "- Review Convergence Scope is authoritative. Emit exactly one `findings` entry per OPEN_ACTIVE_RECHECK stable_id: `resolved` with concrete closure evidence if fixed, otherwise `open`. Narrative does not update the ledger; omission leaves it open. Re-check new diff code; carry RESOLVED findings unless the exact anchor was touched.",
                "- Each finding requires stable_id, state, severity, category, location, and description. Move a carried ID only if the diff touched its prior anchor. Emit exact `touched_anchors`; new-code defects get new IDs.",
                ReviewerExhaustiveFindingsContract,
                ReviewerDefectContract,
                "- Remediable open blockers require `needs-work`; reserve `fail` for non-remediable stops. With none, use `verdict: pass` and `blockers: none`; advisories belong only in `findings`.",
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
        return Build(role, complexity, includeHighRiskReviewerEnumerationContract, hasDurableResearch: false);
    }

    public static IReadOnlyList<string> Build(
        AgentRole role,
        TaskComplexity complexity,
        bool includeHighRiskReviewerEnumerationContract,
        bool hasDurableResearch)
    {
        var requirements = Build(role, complexity).ToList();
        if (role == AgentRole.Planner && hasDurableResearch)
        {
            requirements[1] = "- Synthesize the plan from the complete Durable Research Notes supplied by the orchestrator; do not repeat a broad repository source survey.";
        }

        return requirements;
    }

    public static IReadOnlyList<string> BuildScout(
        TaskComplexity complexity,
        bool includeHighRiskReviewerEnumerationContract)
    {
        return
        [
            "## Scout Requirements",
            "- Perform the current source survey and inspect prior goal evidence yourself; no Researcher ran before this Planner task.",
            "- Stdout is the only channel the orchestrator reads: print the complete research findings followed by the complete Planner plan in one final message. Never write a plan file or any other file, and never reply with only a summary or a file path.",
            "- Put the Current source findings, Prior goal evidence, Upstream capabilities, and Likely seams and risks sections before the Planner plan sections. Cite repository evidence for each material claim and distinguish facts from inferences.",
            "- Keep the plan's numbered criterion mapping lines only in the plan; do not start research lines with a digit and period.",
            .. Build(AgentRole.Researcher, complexity).Where(requirement => requirement != ResearcherStdoutOnlyContract),
            .. Build(AgentRole.Planner, complexity, includeHighRiskReviewerEnumerationContract, hasDurableResearch: false)
        ];
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
                "- Inspect the supplied goal evidence and current repository context before synthesizing the plan.",
                "- Produce a concrete plan with likely files or modules, smallest viable change boundary, assumptions, and stop conditions.",
                "- Map every acceptance criterion by number and include ownership/lifecycle, edge contracts, risks, seams, and focused verification.",
                "- Define falsifiable proof for downstream roles; do not return a generic checklist.",
                "- If repository evidence disproves the goal premise, report `blockers: premise-invalid - <fact and evidence>` and stop before proposing implementation.",
                "- Cite files without a line RANGE. `File.cs`, `File.cs:442`, `File.cs#L442`, and `File.cs::Symbol` are accepted; `File.cs:442-479` is REJECTED and discards your entire plan, because the validator treats the whole string including the range as the path and finds no such file. Before emitting, scan your output for `.cs:<digits>-<digits>` and replace each with its single start line.",
                "- Do not modify repository files; implementation belongs to the Developer task."
            ],
            AgentRole.Researcher =>
            [
                "## Researcher Requirements",
                ResearcherStdoutOnlyContract,
                "- Do NOT build the solution or run tests. A live conductor holds the built assemblies, so your build will fail on a file lock and consume your whole session on lock recovery. Inspect source, git history, and committed receipts instead. If a question can only be settled by executing tests, say so and name the exact test classes so the Tester or a Conductor-side evidence request can settle it.",
                "- Produce the durable research artifact under the required Current source findings, Prior goal evidence, Upstream capabilities, and Likely seams and risks headings.",
                "- Lead with repository evidence: file paths, symbols, APIs, tests, primary sources, and commands or file inspections.",
                "- Prefer source-survey.md in the context directory when present; otherwise exclude generated output such as **/bin/** and **/obj/** unless the task targets it.",
                "- Separate confirmed facts from inferences, risks, and unknowns.",
                "- If repository evidence disproves the goal premise, report `blockers: premise-invalid - <fact and evidence>` and stop before downstream implementation.",
                "- Do not modify repository files; implementation belongs to the Developer task."
            ],
            AgentRole.Developer =>
            [
                "## Developer Requirements",
                "- No edits: start `NO_CHANGE:` line with reason; report `tests: deferred` naming test classes for conductor.",
                DeveloperDeferredTestsExactForm,
                DeveloperSelfCheckProcedure,
                "- First honor an eligible typed early-convergence decision for the exact candidate by returning its passed focused receipts without replaying history or manufacturing edits.",
                "- Keep edits scoped and report changed files plus behavior enabled.",
                "- Before editing, name the failing test and quote its assertion output.",
                "- Run focused verification when practical and name exact commands.",
                "- Call out blockers or follow-up work explicitly."
            ],
            AgentRole.Tester =>
            [
                "## Tester Requirements",
                NegativeControlApplicabilityRule,
                NegativeControlRequestRule,
                TesterCompactReceiptFirstContract,
                TesterCompactInconclusiveReceiptContract,
                "- PRIMARY PATH: prefer Conductor-side runs. Emit `evidence_request` selections (`test_project`, `test_class`) in findings JSON; report `tests: deferred` naming requested work. The Conductor returns receipts. Execute directly only if selection cannot settle it.",
                "- A Developer-owned finding still waiting on execution MUST carry `evidence_request`; the conductor runs it once per candidate before re-dispatching Developer. In `evidence_index`, `state=pending-execution` means no run exists for that `candidate_sha` and is NOT a pass; `state=executed-on-candidate` means the receipt for that exact candidate exists. Never call the source correct, or resolve a finding, from narrative or a run that did not happen.",
                "- Derive focused checks from requested behavior; report concrete evidence.",
                "- Listed in matching OPERATOR-OWNED/ACCEPTANCE-GATE-OWNED: not-verifiable; else attest met/not-met.",
                SelfCheckAttestation,
                "- Run or attempt exact commands; include exit code and concise output summary.",
                "- Cover edge/negative cases when practical and avoid treating bin/obj output as changed source.",
                TesterFindingRequestRule,
                "- Reuse a carried finding's stable_id. Keep its original location unless the code moved: when the system-derived round diff touched the prior anchor, keep the stable_id and report the defect's current location. Otherwise a different location is rejected. If a new stable_id describes the same open anchor, reuse the canonical stable_id instead.",
                "- A killed/timed-out/no-results verification is an environment outcome, not a failure: report `tests: inconclusive - <current-round evidence>` with `blockers: none`, and do not reuse a prior round's conclusion as evidence.",
                "- Keep each command bounded: build once, then run narrow no-rebuild test filters; never bundle a build and a broad test run in one command.",
                "- You may build and run tests but must not modify source files.",
                "- Prefer scripts/Invoke-TestSummary.ps1 with -Target <csproj>, -Filter FullyQualifiedName~<Class>, and -NoBuild over hand-composed runner commands. It emits one compact MTP_TERMINAL_SUMMARY line instead of full runner output. MTP filters require FullyQualifiedName~Class or Name~Method-symbol syntax and reject DisplayName text and a bare class name."
            ],
            AgentRole.Reviewer =>
            [
                "## Reviewer Requirements",
                NegativeControlApplicabilityRule,
                NegativeControlRequestRule,
                "### 1. Spec compliance (do this first)",
                "- Record met/not-met/not-verifiable with file+line evidence. Use zero-based `criterion_index` values (0..N-1), exactly one per criterion. Not-met uses `category: spec-compliance`; main conflicts use `category: spec-defect`.",
                "- Listed in matching OPERATOR-OWNED/ACCEPTANCE-GATE-OWNED: not-verifiable; else attest met/not-met.",
                SelfCheckAttestation,
                "### 2. Code quality (only after section 1)",
                "- Review findings first by severity with evidence. Cover every in-scope file before the first verdict; state gaps. Later SHALLOW findings on unchanged code are coverage defects; deeper concurrency/durability/fault analysis is desired. Never withhold an identified finding.",
                "- Use git diff main...HEAD for scope. Branch-behind-main alone is NOT a blocker; block only on concrete conflict, semantic overlap, or a non-applying diff.",
                "- Open blocking test-evidence MUST carry `evidence_request:{selections:[{test_project,test_class}]}`; other findings may include it. Use a test project from `config/acceptance-manifest.json` by label, file name, or path; never infer a request from prose.",
                "- Categories: `spec-compliance`, `spec-defect`, `correctness`, `test-evidence`, `test-coverage`, `code-quality`, `operator-owned`, or `acceptance-owned`.",
                "- Emit exactly one `findings` entry per OPEN_ACTIVE_RECHECK stable_id: `resolved` with closure evidence if fixed, otherwise `open`. Narrative does not update the ledger; omission leaves it open.",
                "- Move a carried ID only when its prior anchor was touched; emit `touched_anchors`; new-code defects get new IDs.",
                ReviewerExhaustiveFindingsContract,
                ReviewerDefectContract,
                "- Remediable open blockers require `needs-work`; reserve `fail` for non-remediable stops. With none, use `verdict: pass` and `blockers: none`; advisories belong only in `findings`.",
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
