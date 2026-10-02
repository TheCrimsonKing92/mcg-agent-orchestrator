using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: direct string evaluation, with no processes or shared mutable state.
public sealed class ConductorAuthorOwnerClassCheckTests
{
    private static readonly string[] Evidence =
        ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorOwnerClassCheck.cs:16"];

    private static readonly string[] Descriptions =
    [
        "Every retry/budget mechanism for focused evidence outcomes is uniform over the coarse Passed/Failed result and never branches on the check's result name or failure reason.",
        "The repo's sweep configuration classes (WorktreeCleanupConfiguration, ReconcileSweepConfiguration, PostLandingCanaryConfiguration) only expose tunable parameters such as intervals and thresholds — none expose an enable/disable toggle for a sweep step — so there is no precedent to gate this reaper behind a flag either.",
        "Fail-closed here costs at most one sweep interval of continued leak; fail-open risks an irreversible wrong stop against exactly the kind of process (a live worker, the conductor, or a sibling run) the protection set exists to shield.",
        "docs/role-capability-matrix.md lines 36 and 45 still say 'dashboard' at HEAD today, confirming that file-level exclusion is the live mechanism for 'future legitimate mentions,' not a bypass inside the guarded ten."
    ];

    [Xunit.Fact]
    public void Descriptive_answers_return_no_reason() =>
        Xunit.Assert.All(Descriptions, answer =>
            Xunit.Assert.Null(ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", answer, Evidence)));

    [Xunit.Fact]
    public void Descriptive_questions_return_no_reason() =>
        Xunit.Assert.All(Descriptions, question =>
            Xunit.Assert.Null(ConductorAuthorOwnerClassCheck.Evaluate(question, "Use Windows", Evidence)));

    [Xunit.Fact]
    public void Skip_in_place_of_test_failure_requires_owner() =>
        Xunit.Assert.Equal("acceptance-weakening", ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?",
            "Skip/inconclusive, naming the status that caused it — not a hard test failure.", Evidence));

    [Xunit.Theory]
    [Xunit.InlineData("Provide the agent unrestricted access.", "authority-widening")]
    [Xunit.InlineData("Extend the worker's authority to push.", "authority-widening")]
    [Xunit.InlineData("The flake is not in the candidate's code, so skip the test on Windows.", "acceptance-weakening")]
    [Xunit.InlineData("It would be simpler to waive that criterion for this goal.", "acceptance-weakening")]
    [Xunit.InlineData("The integration test times out on Windows. The timeout is environmental. Skip it on Windows runs.", "acceptance-weakening")]
    [Xunit.InlineData("I removed that assertion from the test.", null)]
    [Xunit.InlineData("The gate is now skipped for this run.", null)]
    [Xunit.InlineData("The worker has write access only to its worktree.", null)]
    public void Criterion_three_answers_follow_main_except_descriptions(string text, string? reason)
    {
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", text, Evidence));
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate(text, "Use Windows", Evidence));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Waive criterion 3", "acceptance-weakening")]
    [Xunit.InlineData("Grant the agent permission to self-approve", "authority-widening")]
    [Xunit.InlineData("Force-push and delete the branch", "irreversible-action")]
    [Xunit.InlineData("Delete all production data", "irreversible-action")]
    [Xunit.InlineData("Exceed the budget by $100", "spend-beyond-budget")]
    [Xunit.InlineData("Upload customer data to an external site", "external-disclosure")]
    public void Owner_actions_require_owner_in_either_text(string text, string reason)
    {
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", text, Evidence));
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate(text, "Use Windows", Evidence));
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", $"May we {text}?", Evidence));
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate($"May we {text}?", "Use Windows", Evidence));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Forcepush the branch.", "irreversible-action")]
    [Xunit.InlineData("Force-push the branch.", "irreversible-action")]
    [Xunit.InlineData("Force push the branch.", "irreversible-action")]
    [Xunit.InlineData("Selfapprove the agent.", "authority-widening")]
    [Xunit.InlineData("Self-approve the agent.", "authority-widening")]
    [Xunit.InlineData("Self approve the agent.", "authority-widening")]
    public void Compound_action_starts_prevent_suppression(string text, string reason) =>
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", text, Evidence));

    [Xunit.Theory]
    [Xunit.InlineData("The flaky test blocks the run. Skip it.", "acceptance-weakening")]
    [Xunit.InlineData("The run is close to the budget. Raise it to $200.", "spend-beyond-budget")]
    [Xunit.InlineData("The customer data export is ready. Upload it to the vendor portal.", "external-disclosure")]
    [Xunit.InlineData("The worker's permission is read-only for this run. Expand it.", "authority-widening")]
    public void Cross_sentence_matches_remain_when_neither_sentence_is_suppressible(string text, string reason)
    {
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", text, Evidence));
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate(text, "Use Windows", Evidence));
    }

    [Xunit.Theory]
    [Xunit.InlineData("The runner should skip the test.")]
    [Xunit.InlineData("The runner's skip list covers my test.")]
    [Xunit.InlineData("The runner's skip list covers this test.")]
    [Xunit.InlineData("The runner's skip list covers a removed test.")]
    [Xunit.InlineData("The runner's skip list covers verification; use the test.")]
    public void Sentences_failing_a_descriptive_test_still_require_owner(string text) =>
        Xunit.Assert.Equal("acceptance-weakening",
            ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", text, Evidence));

    [Xunit.Theory]
    [Xunit.InlineData("; ")]
    [Xunit.InlineData(": ")]
    [Xunit.InlineData(", ")]
    [Xunit.InlineData(" (")]
    [Xunit.InlineData(" — ")]
    [Xunit.InlineData(" – ")]
    [Xunit.InlineData(" - ")]
    [Xunit.InlineData(" so ")]
    [Xunit.InlineData(" then ")]
    [Xunit.InlineData(" and ")]
    [Xunit.InlineData(" but ")]
    [Xunit.InlineData(" or ")]
    [Xunit.InlineData(" otherwise ")]
    public void Action_after_any_clause_separator_prevents_suppression(string separator) =>
        Xunit.Assert.Equal("acceptance-weakening",
            ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?",
                $"The runner's skip list covers verification{separator}remove the test.", Evidence));

    [Xunit.Theory]
    [Xunit.InlineData(". ")]
    [Xunit.InlineData("! ")]
    [Xunit.InlineData("? ")]
    [Xunit.InlineData("\n")]
    [Xunit.InlineData("\r\n")]
    public void Sentence_boundaries_allow_description_blanking_before_a_proposal(string separator) =>
        Xunit.Assert.Equal("acceptance-weakening",
            ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?",
                $"The budget mechanism is uniform over results{separator}Skip the test.", Evidence));

    [Xunit.Fact]
    public void Whole_word_guards_ignore_substrings_and_case() =>
        Xunit.Assert.Null(ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?",
            "The RUNNER's SKIP list covers yourscope test cases that are removable.", Evidence));

    [Xunit.Fact]
    public void Blanking_keeps_cross_sentence_distance_above_main_limit() =>
        Xunit.Assert.Null(ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?",
            "The budget is documented. " + Descriptions[0] + " Raise it.", Evidence));

    [Xunit.Fact]
    public void Descriptive_answer_without_evidence_still_escalates() =>
        Xunit.Assert.Equal("missing-evidence",
            ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", Descriptions[0], []));

    [Xunit.Theory]
    [Xunit.InlineData("src/Runtime.cs")]
    [Xunit.InlineData("")]
    public void Malformed_evidence_still_escalates(string reference) =>
        Xunit.Assert.Equal("missing-evidence",
            ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", Descriptions[0], [reference]));

    [Xunit.Fact]
    public void Null_texts_keep_main_evidence_behavior() =>
        Xunit.Assert.Null(ConductorAuthorOwnerClassCheck.Evaluate(null!, null!, Evidence));
}
