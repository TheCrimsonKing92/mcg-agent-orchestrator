using Mcg.AgentOrchestrator.Core;

// Parallel-safe: only generates and compares immutable requirement text.
public sealed class TesterPromptNoWorkerTestExecutionTests
{
    public static IEnumerable<object?[]> PromptVariants()
    {
        yield return [null, null];
        foreach (var complexity in new[] { TaskComplexity.Simple, TaskComplexity.Complex })
        {
            yield return [complexity, null];
            yield return [complexity, false];
            yield return [complexity, true];
        }
    }

    [Theory]
    [MemberData(nameof(PromptVariants))]
    public void BuildPlainText_TesterVariant_DefersTestExecution(
        TaskComplexity? complexity,
        bool? includeHighRiskContract)
    {
        var instructions = BuildVariant(AgentRole.Tester, complexity, includeHighRiskContract);
        string[] requiredLines =
        [
            "- Name the test classes to run in evidence_request; never run tests.",
            "- Build only with the worker build check; do not modify source files.",
            "- Conductor-run selections use FullyQualifiedName~Class or Name~Method-symbol syntax and reject DisplayName text and a bare class name."
        ];
        foreach (var requiredLine in requiredLines)
        {
            Assert.Single(instructions.Split(Environment.NewLine), line => line == requiredLine);
        }

        string[] forbiddenPhrases =
        [
            "You may build and run tests",
            "Run or attempt exact commands",
            "Run or attempt the exact verification commands",
            "Prefer scripts/Invoke-TestSummary.ps1",
            "Execute directly only",
            "then run tests with a narrow filter",
            "then run narrow no-rebuild test filters"
        ];
        foreach (var forbiddenPhrase in forbiddenPhrases)
        {
            Assert.DoesNotContain(forbiddenPhrase, instructions, StringComparison.Ordinal);
        }

        var primaryPath = complexity == TaskComplexity.Simple
            ? "- PRIMARY PATH: prefer Conductor-side runs. Emit `evidence_request` selections (`test_project`, `test_class`) in findings JSON; report `tests: deferred` naming requested work. The Conductor returns receipts."
            : "- PRIMARY PATH: prefer a Conductor-side run over executing tests yourself. Emit evidence_request with selections of test_project and test_class inside your findings JSON, and report tests: deferred naming what you requested. The Conductor runs that selection and returns receipts. This is faster, avoids composing runner commands for this platform and runner, and keeps large test output out of your context.";
        Assert.Equal(primaryPath, Assert.Single(
            instructions.Split(Environment.NewLine),
            line => line.StartsWith("- PRIMARY PATH:", StringComparison.Ordinal)));
        Assert.True(instructions.IndexOf("RECEIPT-FIRST", StringComparison.Ordinal) <
                    instructions.IndexOf("PRIMARY PATH", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPlainText_UnchangedRoles_MatchLiteralBaseline()
    {
        foreach (var role in new[] { AgentRole.Developer, AgentRole.Reviewer, AgentRole.Planner })
        {
            foreach (var variant in PromptVariants())
            {
                var complexity = (TaskComplexity?)variant[0];
                var includeHighRiskContract = (bool?)variant[1];
                var expected = string.Join(Environment.NewLine,
                    BaselineLines(role, compact: complexity == TaskComplexity.Simple));

                Assert.Equal(expected, BuildVariant(role, complexity, includeHighRiskContract));
            }
        }
    }

    private static string BuildVariant(
        AgentRole role,
        TaskComplexity? complexity,
        bool? includeHighRiskContract)
    {
        if (complexity is null)
            return SdlcRolePromptRequirements.BuildPlainText(role);

        return includeHighRiskContract is null
            ? SdlcRolePromptRequirements.BuildPlainText(role, complexity.Value)
            : SdlcRolePromptRequirements.BuildPlainText(role, complexity.Value,
                includeHighRiskContract.Value);
    }

    // Verbatim literals from the pre-change role lists, including expanded constant values.
    private static string[] BaselineLines(AgentRole role, bool compact) => (role, compact) switch
    {
        (AgentRole.Developer, false) =>
        [
            "## Developer Requirements",
            "- First honor eligible typed convergence for this candidate: return fresh passed focused receipts; clean worktree, no replay or invented edits.",
            "- No edits: start `NO_CHANGE:` line with reason; report `tests: deferred` naming test classes for conductor.",
            "- Format: `tests: deferred - ClassA, ClassB` or backticked names.",
            "- Before WORKER_RESULT, for each criterion naming Developer as owner, open the test written/changed for it: its assertion must check the specific named outcome, not a weaker property, and fail on pre-change code. If both hold, report proven with the test in criteria_self_check; else strengthen it or report unmet and set assigned_scope_complete: false.",
            "- Implement only the requested behavior and keep edits scoped.",
            "- Name the failing test in `tests: deferred`; never run tests.",
            "- Report changed files and the behavior each change enables.",
            "- Build changed projects with the worker build check.",
            "- Leave follow-up work explicit when the orchestrator blocks the ideal path."
        ],
        (AgentRole.Developer, true) =>
        [
            "## Developer Requirements",
            "- No edits: start `NO_CHANGE:` line with reason; report `tests: deferred` naming test classes for conductor.",
            "- Format: `tests: deferred - ClassA, ClassB` or backticked names.",
            "- Before WORKER_RESULT, for each criterion naming Developer as owner, open the test written/changed for it: its assertion must check the specific named outcome, not a weaker property, and fail on pre-change code. If both hold, report proven with the test in criteria_self_check; else strengthen it or report unmet and set assigned_scope_complete: false.",
            "- First honor an eligible typed early-convergence decision for the exact candidate by returning its passed focused receipts without replaying history or manufacturing edits.",
            "- Keep edits scoped and report changed files plus behavior enabled.",
            "- Name the failing test in `tests: deferred`; never run tests.",
            "- Build changed projects with the worker build check.",
            "- Call out blockers or follow-up work explicitly."
        ],
        (AgentRole.Reviewer, false) =>
        [
            "## Reviewer Requirements",
            "- Request a negative control only when a criterion says a test must fail against main or today's code; never for refactor, documentation or move-only criteria whose tests must pass unmodified.",
            "- RED proof: evidence_request negative_control:\"revert-src\"; if tests use members the goal adds, add revert_paths naming only the src files implementing the behavior, or a repository policy file the conductor accepts (.gitattributes). For one hunk inside a file whose other changes the tests need, use mutation:{path,old_text,new_text} instead. After negative-control-compile-red, narrow revert_paths or ask for an operator record; never repeat the same request.",
            "### 1. Spec compliance (do this first)",
            "- Walk criteria in order: report met, not-met, or not-verifiable with file+line or concrete task evidence in `criteria_verdicts`. Use zero-based `criterion_index` values (0..N-1), with exactly one entry for every criterion.",
            "- Listed in matching OPERATOR-OWNED/ACCEPTANCE-GATE-OWNED: not-verifiable; else attest met/not-met.",
            "- For each proven entry in Developer Criteria Self-Check on a criterion you attest, check the named test exists in the candidate and its assertion checks the named outcome; otherwise raise a finding against that criterion.",
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
            "- Every `needs-work` verdict must inspect the complete candidate diff supplied for the current round and enumerate every blocking finding; never stop after the first. Put each in verdict prose and one semicolon-delimited `blockers` token (no literal semicolons), with file:line, severity `blocking` from `blocking|advisory`, and a violated acceptance criterion ID/label or clear quote/paraphrase. For deletions cite an old/new diff line; for file-wide defects, the defining line. Deduplicate only the same defect identity (stable_id preferred; otherwise normalized file/region+criterion+meaning), union criterion references, retain the most precise current anchor, and never merge by shared file, criterion, or cause. Order by violated criterion index, normalized file path, line/region, then stable_id; `blockers` uses that order. End needs-work verdict prose with this exact standalone line immediately before WORKER_RESULT: `no other blocking findings exist in this diff`. Keep it outside `blockers`.",
            "- `REVIEW DEFECT` is a later-round blocker demonstrably present in an earlier reviewed complete candidate diff. Self-check for it; absent historical comparison evidence prevents this label, not current-diff review.",
            "- Remediable open blockers require `needs-work`; reserve `fail` for non-remediable stops. With none, use `verdict: pass` and `blockers: none`; advisories belong only in `findings`.",
            "- State residual risk, test gaps, and whether acceptance is justified; do not approve from another role's summary alone.",
            "- Treat `Completed` status and worker prose as claims: check each claimed file, commit, command, and test against `git diff main...HEAD` and the repository.",
            "- A claim naming a file, command, endpoint, or test that does not exist is a blocking `correctness` finding.",
            "- Exit 0 with no relevant source change, or only generated or scratch noise, is not a pass.",
            "- Pass only when a relevant source change exists, the claims match the diff, and nothing unrelated changed.",
            "- Do not modify repository files; implementation belongs to the Developer task."
        ],
        (AgentRole.Reviewer, true) =>
        [
            "## Reviewer Requirements",
            "- Request a negative control only when a criterion says a test must fail against main or today's code; never for refactor, documentation or move-only criteria whose tests must pass unmodified.",
            "- RED proof: evidence_request negative_control:\"revert-src\"; if tests use members the goal adds, add revert_paths naming only the src files implementing the behavior, or a repository policy file the conductor accepts (.gitattributes). For one hunk inside a file whose other changes the tests need, use mutation:{path,old_text,new_text} instead. After negative-control-compile-red, narrow revert_paths or ask for an operator record; never repeat the same request.",
            "### 1. Spec compliance (do this first)",
            "- Record met/not-met/not-verifiable with file+line evidence. Use zero-based `criterion_index` values (0..N-1), exactly one per criterion. Not-met uses `category: spec-compliance`; main conflicts use `category: spec-defect`.",
            "- Listed in matching OPERATOR-OWNED/ACCEPTANCE-GATE-OWNED: not-verifiable; else attest met/not-met.",
            "- For each proven entry in Developer Criteria Self-Check on a criterion you attest, check the named test exists in the candidate and its assertion checks the named outcome; otherwise raise a finding against that criterion.",
            "### 2. Code quality (only after section 1)",
            "- Review findings first by severity with evidence. Cover every in-scope file before the first verdict; state gaps. Later SHALLOW findings on unchanged code are coverage defects; deeper concurrency/durability/fault analysis is desired. Never withhold an identified finding.",
            "- Use git diff main...HEAD for scope. Branch-behind-main alone is NOT a blocker; block only on concrete conflict, semantic overlap, or a non-applying diff.",
            "- Open blocking test-evidence MUST carry `evidence_request:{selections:[{test_project,test_class}]}`; other findings may include it. Use a test project from `config/acceptance-manifest.json` by label, file name, or path; never infer a request from prose.",
            "- Categories: `spec-compliance`, `spec-defect`, `correctness`, `test-evidence`, `test-coverage`, `code-quality`, `operator-owned`, or `acceptance-owned`.",
            "- Emit exactly one `findings` entry per OPEN_ACTIVE_RECHECK stable_id: `resolved` with closure evidence if fixed, otherwise `open`. Narrative does not update the ledger; omission leaves it open.",
            "- Move a carried ID only when its prior anchor was touched; emit `touched_anchors`; new-code defects get new IDs.",
            "- Every `needs-work` verdict must inspect the complete candidate diff supplied for the current round and enumerate every blocking finding; never stop after the first. Put each in verdict prose and one semicolon-delimited `blockers` token (no literal semicolons), with file:line, severity `blocking` from `blocking|advisory`, and a violated acceptance criterion ID/label or clear quote/paraphrase. For deletions cite an old/new diff line; for file-wide defects, the defining line. Deduplicate only the same defect identity (stable_id preferred; otherwise normalized file/region+criterion+meaning), union criterion references, retain the most precise current anchor, and never merge by shared file, criterion, or cause. Order by violated criterion index, normalized file path, line/region, then stable_id; `blockers` uses that order. End needs-work verdict prose with this exact standalone line immediately before WORKER_RESULT: `no other blocking findings exist in this diff`. Keep it outside `blockers`.",
            "- `REVIEW DEFECT` is a later-round blocker demonstrably present in an earlier reviewed complete candidate diff. Self-check for it; absent historical comparison evidence prevents this label, not current-diff review.",
            "- Remediable open blockers require `needs-work`; reserve `fail` for non-remediable stops. With none, use `verdict: pass` and `blockers: none`; advisories belong only in `findings`.",
            "- Challenge generic summaries by comparing implementation and verification evidence.",
            "- Ignore generated bin/obj output unless targeted; state residual risk, test gaps, and acceptance recommendation.",
            "- Treat `Completed` status and worker prose as claims: check each claimed file, commit, command, and test against `git diff main...HEAD` and the repository.",
            "- A claim naming a file, command, endpoint, or test that does not exist is a blocking `correctness` finding.",
            "- Exit 0 with no relevant source change, or only generated or scratch noise, is not a pass.",
            "- Pass only when a relevant source change exists, the claims match the diff, and nothing unrelated changed.",
            "- Do not modify repository files; implementation belongs to the Developer task."
        ],
        (AgentRole.Planner, false) =>
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
            "- For each criterion name the evidence owner (checked against `docs/role-capability-matrix.md`), the owning seam, and the class `TEST-VERIFIABLE` or `REAL-WORLD-DEPENDENT`.",
            "- Route full-suite, test-host, and acceptance evidence to Acceptance and live or post-landing evidence to the operator; never assign evidence a worker cannot reach to Tester or Reviewer.",
            "- For evidence you cannot obtain, name what would settle it, its source, and why it is unavailable, then plan every other criterion in the same round.",
            "- Do not modify repository files; implementation belongs to the Developer task."
        ],
        (AgentRole.Planner, true) =>
        [
            "## Planner Requirements",
            "- Inspect the supplied goal evidence and current repository context before synthesizing the plan.",
            "- Produce a concrete plan with likely files or modules, smallest viable change boundary, assumptions, and stop conditions.",
            "- Map every acceptance criterion by number and include ownership/lifecycle, edge contracts, risks, seams, and focused verification.",
            "- Define falsifiable proof for downstream roles; do not return a generic checklist.",
            "- If repository evidence disproves the goal premise, report `blockers: premise-invalid - <fact and evidence>` and stop before proposing implementation.",
            "- Cite files without a line RANGE. `File.cs`, `File.cs:442`, `File.cs#L442`, and `File.cs::Symbol` are accepted; `File.cs:442-479` is REJECTED and discards your entire plan, because the validator treats the whole string including the range as the path and finds no such file. Before emitting, scan your output for `.cs:<digits>-<digits>` and replace each with its single start line.",
            "- For each criterion name the evidence owner (checked against `docs/role-capability-matrix.md`), the owning seam, and the class `TEST-VERIFIABLE` or `REAL-WORLD-DEPENDENT`.",
            "- Route full-suite, test-host, and acceptance evidence to Acceptance and live or post-landing evidence to the operator; never assign evidence a worker cannot reach to Tester or Reviewer.",
            "- For evidence you cannot obtain, name what would settle it, its source, and why it is unavailable, then plan every other criterion in the same round.",
            "- Do not modify repository files; implementation belongs to the Developer task."
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "No baseline for this role.")
    };
}
