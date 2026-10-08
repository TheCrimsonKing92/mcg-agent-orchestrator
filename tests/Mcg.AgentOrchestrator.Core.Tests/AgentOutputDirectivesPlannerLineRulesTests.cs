using Mcg.AgentOrchestrator.Core;

public sealed class AgentOutputDirectivesPlannerLineRulesTests
{
    [Xunit.Fact]
    public void PlannerDirectiveStatesMappingLineLayoutRules()
    {
        var directive = PlannerDirective();

        Xunit.Assert.Contains("bare criterion number followed by a period", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("non-empty one-sentence summary on that same line", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("detail bullets that follow must not begin with a digit and a period", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("concrete repository-relative file, never a wildcard pattern", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("`git ls-files <path>`", directive, StringComparison.Ordinal);
        Xunit.Assert.Contains("unless it is marked as a new file", directive, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerDirectiveRetainsEveryOriginalFragment()
    {
        var directive = PlannerDirective();
        string[] originalFragments =
        [
            "Planner: print the complete decision-changing plan in stdout before WORKER_RESULT; stdout is authoritative, and a summary or private model-home file path alone is invalid. ",
            "A referenced plan artifact is only a fallback when stdout parsing fails; writing a separate artifact is not required. ",
            "Use these exact substantive headings: ## Premise validity; ## Acceptance criteria mapping; ## Target seams and symbols; ",
            "## Ownership and lifecycle; ## External and edge contracts; ## Integration seams; ## Verification commands and classes; ## Risks and stop conditions. ",
            "Map every numbered acceptance criterion, and state valid/invalid premise evidence, backticked file/symbol citations, the owner/lifecycle decision, external and unhappy-path contracts, integration sequence, ",
            "For every criterion mapping use `disposition=planned; plan=<mapping>` or `disposition=undecidable; would-settle=<evidence>; required-source=<producer/store>; unavailable-because=<reason>`. ",
            "An undecidable criterion does not block other criteria and requires `blockers: none` when no operator action is needed. ",
            "For evidence that exists only in an unreadable store, emit exactly one `PLANNER_EVIDENCE_REQUEST:` JSON directive with criterion_index, evidence_key, availability=retrievable, store, needed, and reason. ",
            "For evidence that was never recorded, prefer an undecidable mapping; if operator action is still required, use availability=never-recorded and omit store. ",
            "Evidence needed to decide or plan now is a blocking prerequisite; evidence that can only be produced after the candidate exists is prospective acceptance evidence. ",
            "For the latter, use availability=post-implementation with owner and omit store; retain the exact criterion obligation without claiming the check was executed. ",
            "backticked verification commands with TEST-VERIFIABLE or REAL-WORLD-DEPENDENT, and explicit stop conditions. ",
            "Cited repository paths must exist unless explicitly marked as a new file to create. ",
            "Mark a new file with the exact token `(new file)` or `— new file` immediately after its backticked path, ",
            "or use `— new` followed by up to three descriptive words and an artifact-kind noun (`store`, `class`, `record`, `interface`, `test`, `fixture`, `script`, `document`, or `receipt`) that ends the clause. ",
            "The descriptive form must end at a period, semicolon, or the end of the line. Alternatively, use the words 'new file', 'create', or 'add' within 24 characters before the path containing no other backtick. ",
            "A marker applies only to the single citation it is adjacent to, not to the whole line, so mark every new path individually. ",
            "To cite a symbol, use a double colon such as `Path/File.cs::SymbolName`, or `Path/File.cs:123`, or `Path/File.cs#L12`; ",
            "a single colon before a symbol name is not recognised and the whole string is then treated as a file path that does not exist.",
        ];

        foreach (var fragment in originalFragments)
        {
            Xunit.Assert.Contains(fragment, directive, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public void OtherRoleDirectiveBranchesMatchHead()
    {
        string[] developerAtHead =
        [
            "Developer: set `assigned_scope_complete` to `true` only when your assigned implementation is complete; `false` requests bounded revision and excludes later Acceptance/operator evidence.",
            "WORKER_RESULT:",
            "files: <comma-separated changed files or none>",
            "commands: <commands run or none>",
            "tests: <pass|fail|not-run|deferred|inconclusive - token first, then current-round evidence>",
            "commit: <commit sha or none>",
            "blockers: <none|premise-invalid - fact and evidence (Planner/Researcher/Developer/Tester)|exact-blocker - token first; blank is invalid; put deferred-verification notes in tests>",
            "assigned_scope_complete: <true|false>",
            "criteria_self_check: <one-line JSON array, one entry per refined criterion: {criterion_index,status:proven|not-owned|unmet,evidence}; zero-based like criteria_verdicts; evidence <=200 chars: proven=test class.method + why its assertion fails without this round's change, not-owned=named owner role, unmet=missing work; whole field <3500 chars; [] when no refined criteria>",
            "model_fit: <provider/model - adequate|overkill|underpowered - task shape - reason>",
            "skills: <selected skills used or none>",
            "confidence: <high|medium|low>",
            "END_WORKER_RESULT",
        ];
        string[] researcherAtHead =
        [
            "Researcher: print the complete evidence-backed research artifact in stdout before WORKER_RESULT. Use these exact substantive headings: ## Current source findings; ## Prior goal evidence; ## Upstream capabilities; ## Likely seams and risks. A summary or provider-private path alone is invalid.",
            "WORKER_RESULT:",
            "files: <comma-separated changed files or none>",
            "commands: <commands run or none>",
            "tests: <pass|fail|not-run|deferred|inconclusive - token first, then current-round evidence>",
            "commit: <commit sha or none>",
            "blockers: <none|premise-invalid - fact and evidence (Planner/Researcher/Developer/Tester)|exact-blocker - token first; blank is invalid; put deferred-verification notes in tests>",
            "citations: <repository files, docs, or evidence sources used>",
            "model_fit: <provider/model - adequate|overkill|underpowered - task shape - reason>",
            "skills: <selected skills used or none>",
            "confidence: <high|medium|low>",
            "END_WORKER_RESULT",
        ];
        string[] reviewerAtHead =
        [
            "WORKER_RESULT:",
            "files: <comma-separated changed files or none>",
            "commands: <commands run or none>",
            "tests: <pass|fail|not-run|deferred|inconclusive - token first, then current-round evidence>",
            "commit: <commit sha or none>",
            "blockers: <none - token first when verdict is pass; otherwise one complete semicolon-delimited token per open blocking finding, each with file:line, severity blocking, and a violated acceptance criterion; no literal semicolons inside a token; blank is invalid; advisory findings belong only in findings>",
            "findings: <one-line JSON array of {stable_id,state:open|resolved,severity:blocking|advisory,category:spec-compliance|spec-defect|correctness|test-evidence|test-coverage|code-quality|operator-owned|acceptance-owned,location:{file,region,hunk?},description,evidence_request?:{selections:[{test_project,test_class}]}}; severity is required; open blocking test-evidence requires evidence_request for Tester and Reviewer; [] when none>",
            "touched_anchors: <one-line JSON array of {file,region,hunk?} for prior finding anchors touched by this round's diff; [] when none>",
            "criteria_verdicts: <one-line JSON array of {criterion_index,verdict:met|not-met|not-verifiable,evidence}; [] when the goal has no refined acceptance criteria>",
            "verdict: <pass|needs-work|fail>",
            "model_fit: <provider/model - adequate|overkill|underpowered - task shape - reason>",
            "skills: <selected skills used or none>",
            "confidence: <high|medium|low>",
            "END_WORKER_RESULT",
        ];

        Xunit.Assert.Equal(developerAtHead, AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Developer));
        Xunit.Assert.Equal(researcherAtHead, AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Researcher));
        Xunit.Assert.Equal(reviewerAtHead, AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Reviewer));
    }

    private static string PlannerDirective() =>
        Xunit.Assert.Single(
            AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner)
                .Where(line => line.StartsWith("Planner:", StringComparison.Ordinal)));
}
