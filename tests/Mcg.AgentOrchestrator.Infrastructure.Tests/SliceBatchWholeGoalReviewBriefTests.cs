using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fixed clocks and ids belong to independent in-memory kernels.
public sealed class SliceBatchWholeGoalReviewBriefTests
{
    private static readonly GoalId ParentId = new("11111111111111111111111111111111");
    private static readonly TaskId ReviewerId = new("22222222222222222222222222222222");

    [Xunit.Fact]
    public void ParentReviewerBrief_CarriesPlanAndLatestStreamReceiptsVerbatim()
    {
        var clock = new FixedClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var parent = CreateParent(kernel);
        var first = kernel.CreateGoal(new GoalId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), "First planned stream.",
            [new TaskSpec(TaskId.New(), "Review first stream.", AgentRole.Reviewer)], parent.Id);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var second = kernel.CreateGoal(new GoalId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), "Second planned stream.",
            [new TaskSpec(TaskId.New(), "Review second stream.", AgentRole.Reviewer)], parent.Id);
        var mapping = "First mapping " + new string('m', 1800) + " mapping end.";
        var summary = "First Reviewer findings " + new string('s', 3000) + " summary end.";
        RecordReview(kernel, first, mapping, summary, clock.UtcNow);
        RecordReview(kernel, second, "Second mapping.", "Second Reviewer findings.", clock.UtcNow);
        // Record an older round last: selection must use CompletedAt, not LastVerification.
        kernel.RecordTaskVerification(first.Id, first.Tasks[0].Id,
            new TaskVerificationRecord("old review", "memory", 0, "Obsolete review.", "", clock.UtcNow.AddHours(-1)));

        // Reversing the snapshot's goal enumeration must not reorder the recorded plan.
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = snapshot.Goals.Reverse().ToArray() }, clock);
        var brief = kernel.BuildTaskBrief(parent.Id, ReviewerId).Content;

        Xunit.Assert.Contains("Parent objective: Small feature.", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains($"- 1. {first.Id.Value}: {first.Objective}", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains($"- 2. {second.Id.Value}: {second.Objective}", brief, StringComparison.Ordinal);
        Xunit.Assert.True(brief.IndexOf(first.Objective, StringComparison.Ordinal) < brief.IndexOf(second.Objective, StringComparison.Ordinal));
        Xunit.Assert.Contains(mapping, brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("Second mapping.", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains(summary, brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("Second Reviewer findings.", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains($"candidate_sha=candidate-{first.Id.Value}", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains($"candidate_sha=candidate-{second.Id.Value}", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("artifact=focused.log", brief, StringComparison.Ordinal);
        Xunit.Assert.Contains("Test result: focused.trx", brief, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Obsolete review.", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ParentReviewerBrief_ListsChildrenWithMissingReceiptComponents()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var parent = CreateParent(kernel);
        var noReviewer = kernel.CreateGoal("Child without Reviewer.",
            [new TaskSpec(TaskId.New(), "Implement stream.", AgentRole.Developer)], parent.Id);
        var noEvidence = kernel.CreateGoal("Child without evidence.",
            [new TaskSpec(TaskId.New(), "Review stream.", AgentRole.Reviewer)], parent.Id);

        var brief = kernel.BuildTaskBrief(parent.Id, ReviewerId).Content;

        foreach (var child in new[] { noReviewer, noEvidence })
        {
            Xunit.Assert.Contains($"### Child {child.Id.Value}{Environment.NewLine}Objective: {child.Objective}" +
                $"{Environment.NewLine}Pre-review evidence receipt:{Environment.NewLine}(none recorded)" +
                $"{Environment.NewLine}Last Reviewer result:{Environment.NewLine}(none recorded)", brief, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public void ChildlessParentAndOrdinaryGoal_MatchPreChangeBriefFixture()
    {
        var childless = new AgentOrchestratorKernel(new FixedClock());
        childless.SetEngineeringPractices([]);
        var parent = CreateParent(childless);
        Xunit.Assert.Equal(PreChangeBriefFixture.ReplaceLineEndings(Environment.NewLine),
            childless.BuildTaskBrief(parent.Id, ReviewerId).Content);

        var ordinary = new AgentOrchestratorKernel(new FixedClock());
        ordinary.SetEngineeringPractices([]);
        var goal = ordinary.CreateGoal(ParentId, "Small feature.",
        [
            new TaskSpec(TaskId.New(), "Implement feature.", AgentRole.Developer),
            new TaskSpec(ReviewerId, "Review feature.", AgentRole.Reviewer)
        ]);
        Xunit.Assert.Equal(PreChangeBriefFixture.ReplaceLineEndings(Environment.NewLine),
            ordinary.BuildTaskBrief(goal.Id, ReviewerId).Content);
    }

    [Xunit.Fact]
    public void NonReviewerParentAndChildBriefs_DoNotGainParentReviewSection()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var parent = CreateParent(kernel);
        var developer = kernel.AddTask(parent.Id, AgentRole.Developer, "Implement feature.");
        var childReviewer = new TaskSpec(TaskId.New(), "Review stream.", AgentRole.Reviewer);
        var child = kernel.CreateGoal("Child stream.", [childReviewer], parent.Id);

        Xunit.Assert.DoesNotContain("## Slice-Batch Decomposition Plan", kernel.BuildTaskBrief(parent.Id, developer.Id).Content);
        Xunit.Assert.DoesNotContain("## Slice-Batch Decomposition Plan", kernel.BuildTaskBrief(child.Id, childReviewer.Id).Content);
    }

    private static Goal CreateParent(AgentOrchestratorKernel kernel) =>
        kernel.CreateGoal(ParentId, "Small feature.", [new TaskSpec(ReviewerId, "Review feature.", AgentRole.Reviewer)]);

    private static void RecordReview(AgentOrchestratorKernel kernel, Goal child, string mapping, string summary, DateTimeOffset at)
    {
        var reviewer = child.Tasks[0];
        kernel.RecordPreReviewEvidence(child.Id, reviewer.Id, new PreReviewEvidenceReceipt(
            child.Id.Value, 1, $"candidate-{child.Id.Value}", ["StreamTests"], PreReviewEvidenceDisposition.Green, 1, 0,
            [new PreReviewEvidenceCheckReceipt("focused", "run focused", true, 0, "focused.log", ["focused.trx"])],
            [], mapping, $"evidence-{child.Id.Value}", at));
        kernel.RecordTaskVerification(child.Id, reviewer.Id,
            new TaskVerificationRecord("review", "memory", 0, summary, "", at, ReviewedCommit: $"candidate-{child.Id.Value}"));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    }

    // Literal fixture transcribed from the pre-change brief path at 7a5529d6;
    // minimal Pending Reviewer inputs yield these same bytes for both goal shapes.
    private const string PreChangeBriefFixture = """
        # Agent Task Brief

        Goal: Small feature.
        Goal id: 11111111111111111111111111111111
        Goal status: Draft
        Decision context: embedded in this brief and .orchestrator-handoff.md in the working directory when present; do not attempt to reach orchestrator state.
        A Planner must map evidence that was never recorded as disposition=undecidable, naming what would settle it, its required source, and why it is unavailable, while planning all remaining criteria in the same round. Escalate only when operator action is required; classified evidence escalations must state retrievable plus the inaccessible store, or never-recorded.
        Task: Review feature.
        Task role: Reviewer
        Task status: Pending
        Task id: 22222222222222222222222222222222

        ## Instructions
        Complete this SDLC task. Report only changed files, verification evidence, blockers, or HUMAN_INPUT: <question>.
        Use repository-local verification when practical; do not claim completion without evidence.
        When surveying files, start with source-survey.md in the context directory when present, or use rg excluding **/bin/**, **/obj/**, .scratch, and prototype state.
        Do not stage or commit changes; the orchestrator commits verified Developer/Tester diffs.
        Host platform is Windows. Prefer PowerShell or cmd syntax for shell commands. POSIX-only utilities such as printf may be unavailable.
        WORKER_RESULT:
        files: <comma-separated changed files or none>
        commands: <commands run or none>
        tests: <pass|fail|not-run|deferred|inconclusive - token first, then current-round evidence>
        commit: <commit sha or none>
        blockers: <none - token first when verdict is pass; otherwise one complete semicolon-delimited token per open blocking finding, each with file:line, severity blocking, and a violated acceptance criterion; no literal semicolons inside a token; blank is invalid; advisory findings belong only in findings>
        findings: <one-line JSON array of {stable_id,state:open|resolved,severity:blocking|advisory,category:spec-compliance|spec-defect|correctness|test-evidence|test-coverage|code-quality|operator-owned|acceptance-owned,location:{file,region,hunk?},description,evidence_request?:{selections:[{test_project,test_class}]}}; severity is required; open blocking test-evidence requires evidence_request for Tester and Reviewer; [] when none>
        touched_anchors: <one-line JSON array of {file,region,hunk?} for prior finding anchors touched by this round's diff; [] when none>
        criteria_verdicts: <one-line JSON array of {criterion_index,verdict:met|not-met|not-verifiable,evidence}; [] when the goal has no refined acceptance criteria>
        verdict: <pass|needs-work|fail>
        model_fit: <provider/model - adequate|overkill|underpowered - task shape - reason>
        skills: <selected skills used or none>
        confidence: <high|medium|low>
        END_WORKER_RESULT
        Include a final model-selection note: Model fit: <provider>/<model or launcher> - adequate|overkill|underpowered - <task shape> - <short reason>.
        No goal repeats/generic progress. Keep blockers/tests/errors/sources; keep Planner/Researcher artifacts complete. Cite receipts/paths; excerpt only if missing/conflicting/malformed.

        ## Reviewer Requirements
        - Request a negative control only when a criterion says a test must fail against main or today's code; never for refactor, documentation or move-only criteria whose tests must pass unmodified.
        - RED proof: evidence_request negative_control:"revert-src"; if tests use members the goal adds, add revert_paths naming only the src files implementing the behavior, or a repository policy file the conductor accepts (.gitattributes). For one hunk inside a file whose other changes the tests need, use mutation:{path,old_text,new_text} instead. After negative-control-compile-red, narrow revert_paths or ask for an operator record; never repeat the same request.
        ### 1. Spec compliance (do this first)
        - Record met/not-met/not-verifiable with file+line evidence. Use zero-based `criterion_index` values (0..N-1), exactly one per criterion. Not-met uses `category: spec-compliance`; main conflicts use `category: spec-defect`.
        - Listed in matching OPERATOR-OWNED/ACCEPTANCE-GATE-OWNED: not-verifiable; else attest met/not-met.
        - For each proven entry in Developer Criteria Self-Check on a criterion you attest, check the named test exists in the candidate and its assertion checks the named outcome; otherwise raise a finding against that criterion.
        ### 2. Code quality (only after section 1)
        - Review findings first by severity with evidence. Cover every in-scope file before the first verdict; state gaps. Later SHALLOW findings on unchanged code are coverage defects; deeper concurrency/durability/fault analysis is desired. Never withhold an identified finding.
        - Use git diff main...HEAD for scope. Branch-behind-main alone is NOT a blocker; block only on concrete conflict, semantic overlap, or a non-applying diff.
        - Open blocking test-evidence MUST carry `evidence_request:{selections:[{test_project,test_class}]}`; other findings may include it. Use a test project from `config/acceptance-manifest.json` by label, file name, or path; never infer a request from prose.
        - Categories: `spec-compliance`, `spec-defect`, `correctness`, `test-evidence`, `test-coverage`, `code-quality`, `operator-owned`, or `acceptance-owned`.
        - Emit exactly one `findings` entry per OPEN_ACTIVE_RECHECK stable_id: `resolved` with closure evidence if fixed, otherwise `open`. Narrative does not update the ledger; omission leaves it open.
        - Move a carried ID only when its prior anchor was touched; emit `touched_anchors`; new-code defects get new IDs.
        - Every `needs-work` verdict must inspect the complete candidate diff supplied for the current round and enumerate every blocking finding; never stop after the first. Put each in verdict prose and one semicolon-delimited `blockers` token (no literal semicolons), with file:line, severity `blocking` from `blocking|advisory`, and a violated acceptance criterion ID/label or clear quote/paraphrase. For deletions cite an old/new diff line; for file-wide defects, the defining line. Deduplicate only the same defect identity (stable_id preferred; otherwise normalized file/region+criterion+meaning), union criterion references, retain the most precise current anchor, and never merge by shared file, criterion, or cause. Order by violated criterion index, normalized file path, line/region, then stable_id; `blockers` uses that order. End needs-work verdict prose with this exact standalone line immediately before WORKER_RESULT: `no other blocking findings exist in this diff`. Keep it outside `blockers`.
        - `REVIEW DEFECT` is a later-round blocker demonstrably present in an earlier reviewed complete candidate diff. Self-check for it; absent historical comparison evidence prevents this label, not current-diff review.
        - Remediable open blockers require `needs-work`; reserve `fail` for non-remediable stops. With none, use `verdict: pass` and `blockers: none`; advisories belong only in `findings`.
        - Challenge generic summaries by comparing implementation and verification evidence.
        - Ignore generated bin/obj output unless targeted; state residual risk, test gaps, and acceptance recommendation.
        - Treat `Completed` status and worker prose as claims: check each claimed file, commit, command, and test against `git diff main...HEAD` and the repository.
        - A claim naming a file, command, endpoint, or test that does not exist is a blocking `correctness` finding.
        - Exit 0 with no relevant source change, or only generated or scratch noise, is not a pass.
        - Pass only when a relevant source change exists, the claims match the diff, and nothing unrelated changed.
        - Do not modify repository files; implementation belongs to the Developer task.

        ## Executed Test Evidence
        Reviewer is read-only; use these existing verification receipts before asking for reruns. Newest first; capped at 12 receipt line(s).
        No executed test evidence exists for this goal yet.

        """;
}
