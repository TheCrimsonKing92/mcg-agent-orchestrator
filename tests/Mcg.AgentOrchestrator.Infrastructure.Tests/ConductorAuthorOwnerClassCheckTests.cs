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
    [Xunit.InlineData("I removed that assertion.", "acceptance-weakening")]
    [Xunit.InlineData("The gate is now skipped for this run.", "acceptance-weakening")]
    [Xunit.InlineData("I have widened the allowlist.", "authority-widening")]
    [Xunit.InlineData("I made the worker unrestricted.", "authority-widening")]
    [Xunit.InlineData("I force-pushed the branch.", "irreversible-action")]
    [Xunit.InlineData("I exceeded the budget for this goal.", "spend-beyond-budget")]
    [Xunit.InlineData("I uploaded the customer data to an external site.", "external-disclosure")]
    public void Completed_actions_require_owner_in_either_text(string text, string reason)
    {
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", text, Evidence));
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate(text, "Use Windows", Evidence));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Add write permission for the agent.", "authority-widening")]
    [Xunit.InlineData("Let the worker have unrestricted network access.", "authority-widening")]
    [Xunit.InlineData("May the agent have unrestricted authority?", "authority-widening")]
    [Xunit.InlineData("Make the worker unrestricted.", "authority-widening")]
    [Xunit.InlineData("The author should be unrestricted.", "authority-widening")]
    [Xunit.InlineData("The flaky test blocks the run. Skip it.", "acceptance-weakening")]
    [Xunit.InlineData("The run is close to the budget. Raise it to $200.", "spend-beyond-budget")]
    [Xunit.InlineData("The customer data export is ready. Upload it to the vendor portal.", "external-disclosure")]
    [Xunit.InlineData("The worker's permission is read-only. Expand it.", "authority-widening")]
    [Xunit.InlineData("The branch is stale. Destroy it.", "irreversible-action")]
    [Xunit.InlineData("The integration test kept timing out. I skipped it.", "acceptance-weakening")]
    [Xunit.InlineData("It's fine to skip the test since it isn't required on Windows.", "acceptance-weakening")]
    [Xunit.InlineData("It's fine to exceed the budget since the overrun isn't large.", "spend-beyond-budget")]
    [Xunit.InlineData("Skipping the test is acceptable because it isn't required.", "acceptance-weakening")]
    [Xunit.InlineData("I would force-push the branch.", "irreversible-action")]
    [Xunit.InlineData("I would skip the flaky test.", "acceptance-weakening")]
    [Xunit.InlineData("I would raise the budget to $200.", "spend-beyond-budget")]
    [Xunit.InlineData("I would grant the worker unrestricted access.", "authority-widening")]
    [Xunit.InlineData("I would upload customer data to an external site.", "external-disclosure")]
    public void Review_regressions_require_owner_in_either_text(string text, string reason)
    {
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", text, Evidence));
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate(text, "Use Windows", Evidence));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Waive criterion 3", "acceptance-weakening")]
    [Xunit.InlineData("Grant the agent permission to self-approve", "authority-widening")]
    [Xunit.InlineData("Force-push and delete the branch", "irreversible-action")]
    [Xunit.InlineData("Delete all production data", "irreversible-action")]
    [Xunit.InlineData("Permanently delete the temporary file despite the risk", "irreversible-action")]
    [Xunit.InlineData("Exceed the budget by $100", "spend-beyond-budget")]
    [Xunit.InlineData("Upload customer data to an external site", "external-disclosure")]
    public void Proposals_and_requests_require_owner_in_either_text(string text, string reason)
    {
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", text, Evidence));
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate(text, "Use Windows", Evidence));
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", $"May we {text}?", Evidence));
        Xunit.Assert.Equal(reason, ConductorAuthorOwnerClassCheck.Evaluate($"May we {text}?", "Use Windows", Evidence));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Do not skip the test.")]
    [Xunit.InlineData("Do not skip the test and remove the gate.")]
    [Xunit.InlineData("You should not remove the test.")]
    [Xunit.InlineData("We rejected the suggestion that we should waive criterion 3.")]
    [Xunit.InlineData("The acceptance gate removes stale test rows before it votes.")]
    [Xunit.InlineData("The gate is skipped by the existing configuration.")]
    [Xunit.InlineData("Force-pushing the branch risks irreversible data loss.")]
    [Xunit.InlineData("The repository grants agents access through the existing allowlist.")]
    [Xunit.InlineData("The existing configuration raises the credit limit.")]
    [Xunit.InlineData("The repository purges stale databases during its existing cleanup.")]
    [Xunit.InlineData("The existing system uploads customer data to an external site.")]
    [Xunit.InlineData("The existing system has permission to read public data.")]
    [Xunit.InlineData("The existing configuration is unrestricted.")]
    [Xunit.InlineData("The flaky test blocks the run. Do not skip it.")]
    [Xunit.InlineData("The test failed. Remove the temporary log.")]
    [Xunit.InlineData("The budget is documented. Add a test.")]
    [Xunit.InlineData("Add an acceptance gate.")]
    [Xunit.InlineData("Add a test covering agent permission.")]
    [Xunit.InlineData("Let the worker retry the test.")]
    [Xunit.InlineData("A crash would destroy the production data.")]
    public void Rejections_and_existing_behavior_return_no_reason(string text)
    {
        Xunit.Assert.Null(ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", text, Evidence));
        Xunit.Assert.Null(ConductorAuthorOwnerClassCheck.Evaluate(text, "Use Windows", Evidence));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Skipping the acceptance test.")]
    [Xunit.InlineData("The gate never skips a test. Instead, waive criterion 3.")]
    [Xunit.InlineData("There is no risk, so skip the test.")]
    public void Ambiguity_or_a_separate_proposal_requires_owner(string text) =>
        Xunit.Assert.Equal("acceptance-weakening",
            ConductorAuthorOwnerClassCheck.Evaluate("Which runtime?", text, Evidence));
}
