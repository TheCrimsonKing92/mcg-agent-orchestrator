using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: checks use an in-memory repository and immutable markdown.
public sealed class CliAuthorDraftCommandTestsHouseConventions
{
    private static readonly string[] HouseChecks =
        ["numbered-criteria", "developer-deferred-criterion", "build-item-count"];
    private const string DeferredCriterion = "2. The Developer reports `tests: deferred - ` followed, directly after the hyphen and comma-separated, by every test class it touched or added. The Tester's evidence_request runs them. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
    private const string BuildItems = "1. First item.\n2. Second item.\n3. Third item.\n4. Fourth item.";
    private static string HouseDraft => CliAuthorDraftCommandTests.ValidMarkdown.ReplaceLineEndings("\n")
        .Replace("Implement the requested slice.", BuildItems);

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void House_draft_without_planner_section_passes_all_eight_checks(string newline)
    {
        var checks = Run(HouseDraft.ReplaceLineEndings(newline));
        Assert.Equal(new[] { "sections", "criteria-present", "owner-sentence", "premise-citations", "numbered-criteria", "developer-deferred-criterion", "build-item-count", "post-landing-criterion", "new-partial-file", "pre-change-failure-criterion" },
            checks.Select(check => check.Name).ToArray());
        Assert.All(checks, check => Assert.True(check.Passed, check.Detail));
    }

    [Theory]
    [InlineData("numbered-criteria", "- The receipt records the result.")]
    [InlineData("developer-deferred-criterion", "Observed 2 declared criteria")]
    [InlineData("build-item-count", "Observed 5 numbered build items")]
    public void Missing_house_convention_fails_its_check_with_observed_value(
        string checkName, string observedValue)
    {
        var markdown = checkName switch
        {
            "numbered-criteria" => HouseDraft.Replace("1. The receipt", "- The receipt"),
            "developer-deferred-criterion" => HouseDraft.Replace(DeferredCriterion + "\n", ""),
            "build-item-count" => HouseDraft.Replace(BuildItems, BuildItems + "\n5. Fifth item."),
            _ => throw new ArgumentOutOfRangeException(nameof(checkName))
        };

        var failed = Assert.Single(Run(markdown).Where(check => !check.Passed));
        Assert.Equal(checkName, failed.Name);
        Assert.Contains(observedValue, failed.Detail);
    }

    [Theory]
    [InlineData("* The receipt")]
    [InlineData("  - The receipt")]
    public void Asterisk_and_nested_bullets_fail_numbered_criteria(string bullet)
    {
        var check = Assert.Single(Run(HouseDraft.Replace("1. The receipt", bullet)),
            check => check.Name == "numbered-criteria");
        Assert.False(check.Passed);
        Assert.Contains(bullet.TrimStart(), check.Detail);
    }

    [Theory]
    [InlineData("```markdown", "```")]
    [InlineData("~~~markdown", "~~~")]
    [InlineData("````markdown", "````")]
    public void Fenced_bullets_are_ignored_but_bullets_after_closing_fence_fail(
        string opening, string closing)
    {
        var innerMarker = opening.StartsWith("````", StringComparison.Ordinal) ? "```" :
            opening.StartsWith("~~~", StringComparison.Ordinal) ? "```" : "~~~";
        var code = $"{opening}\n- Example bullet\n* Another example\n## Example heading\n{innerMarker}\n{closing}\n";
        var markdown = HouseDraft.Replace("1. The receipt", code + "1. The receipt");
        Assert.True(Assert.Single(Run(markdown), check => check.Name == "numbered-criteria").Passed);

        var outside = markdown.Replace("1. The receipt", "- The receipt");
        var failed = Assert.Single(Run(outside), check => check.Name == "numbered-criteria");
        Assert.False(failed.Passed);
        Assert.Contains("- The receipt", failed.Detail);
    }

    [Fact]
    public void Empty_criteria_report_absence_only_through_existing_check()
    {
        var markdown = HouseDraft[..HouseDraft.IndexOf("1. The receipt", StringComparison.Ordinal)] +
            "\n## Scope\nDrafting only.";
        var checks = Run(markdown);
        Assert.Equal("criteria-present", Assert.Single(checks.Where(check => !check.Passed)).Name);
        foreach (var name in new[] { "numbered-criteria", "developer-deferred-criterion" })
        {
            var check = Assert.Single(checks, check => check.Name == name);
            Assert.True(check.Passed);
            Assert.Equal("No declared criteria; criteria-present reports the absence.", check.Detail);
        }
    }

    [Fact]
    public void Deferred_criterion_requires_the_exact_developer_ending()
    {
        var markdown = HouseDraft.Replace(DeferredCriterion,
            DeferredCriterion.Replace("Developer owns; Acceptance executes.", "Reviewer owns; Reviewer executes."));
        var check = Assert.Single(Run(markdown), check => check.Name == "developer-deferred-criterion");
        Assert.False(check.Passed);
        Assert.Contains("Observed 3 declared criteria", check.Detail);
    }

    [Fact]
    public void Original_board_fill_sections_fail_all_three_house_checks()
    {
        var markdown = "# Original board-fill draft\n## Measured premise\nObserved in `docs/role-capability-matrix.md:1`.\n" +
            OriginalBoardFillSections + "\n## Scope\nFirst board-fill draft.";
        var checks = Run(markdown);
        foreach (var name in HouseChecks)
            Assert.False(Assert.Single(checks, check => check.Name == name).Passed, name);
        Assert.All(checks.Where(check => !HouseChecks.Contains(check.Name)),
            check => Assert.True(check.Passed, check.Detail));
        Assert.Contains("Observed 6 numbered build items", Assert.Single(checks, check => check.Name == "build-item-count").Detail);
        Assert.Contains("Observed 7 declared criteria", Assert.Single(checks, check => check.Name == "developer-deferred-criterion").Detail);
    }

    private static IReadOnlyList<AuthorBriefDraftCheck> Run(string markdown) =>
        AuthorBriefDraftChecks.Run(markdown, CliAuthorDraftCommandTests.Fixture.MainSha,
            new CliAuthorDraftCommandTests.FakeRepository());

    // Verbatim sections supplied in operator answer 099f93b7dcc8457da31f8cdb4a2bbe97.
    private const string OriginalBoardFillSections = """
        ## What to build

        1. Make add-task a conductor-applied typed intent. Add an add-task verb and payload in `src/Mcg.AgentOrchestrator.Core/Application/OperatorIntentVerbs.cs`. Route the CLI through the inbox-backed submission path in `src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs`, following the way progress, retry and adjudicate are submitted. Apply the intent in the conductor tick, which is the only state writer. The CLI reports the recorded outcome rather than mutating the goal. A successful add-task must survive every concurrent tick and reload.
        2. Add an atomic graph-repair form. One intent carries an ordered list of role and description pairs plus an optional before-role anchor. The conductor applies all of them in one transaction and validates the final role order. If the order is invalid, nothing is applied and the outcome carries typed evidence. A goal with a pending repair intent must not dispatch until that intent is applied or rejected.
        3. Give dispatch a committed graph version. Add a graph version to the goal state that any task-list change advances. Record it when a dispatch is prepared. Compare it again immediately before a paid process starts, in the worker admission code that `src/Mcg.AgentOrchestrator.App/Application/GoalDispatchOperationsStart.cs:81-100` implements. If the version changed, abort the stale preparation with a typed reason before any process starts.
        4. Keep add-task and the repair idempotent. Give each intent an idempotency key derived from the goal, the ordered role and description list and the anchor. A replay of the same key creates no duplicate task. Reuse of the same key with different content fails closed with typed evidence.
        5. Fix the lost-update path in `src/Mcg.AgentOrchestrator.Execution/Persistence/SqliteOrchestratorStateRepository.cs`. Reproduce the loss first from the real sequence: the insertion lands in the stored goal after the tick loaded its baseline. Then make the merge keep a task that exists only in the stored goal. Do not change the merge rules for tasks both sides know about.
        6. Update the add-task usage and help text in `src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs` and the operator runbook text that mentions add-task in `docs/operator-runbook.md`, so operators know the repair form exists.

        ## Acceptance criteria

        - Add-task and the repair form submit a typed intent that only the conductor tick applies. The CLI never changes goal state directly, and it reports the recorded applied or rejected outcome. A test reads the intent store and the persisted goal after a tick that overlaps the submission, and the inserted task is present. Developer owns; Acceptance executes. TEST-VERIFIABLE.
        - The repair form inserts every requested task in one transaction and validates the final role order. An invalid final order applies nothing and records typed evidence naming the violation. A test covers a valid repair to Researcher, Planner, Developer, Tester, Reviewer from a two-role goal, and an invalid order. Developer owns; Acceptance executes. TEST-VERIFIABLE.
        - Dispatch preparation records the committed graph version. A graph change between preparation and process start aborts the dispatch with a typed reason, and no worker process is started. A test uses the existing checkpoint-before-worker-start seam in `src/Mcg.AgentOrchestrator.App/Application/GoalDispatchOperationsStart.cs`. Developer owns; Acceptance executes. TEST-VERIFIABLE.
        - Replaying the same add-task or repair intent creates no duplicate task. Reusing the same idempotency key with different content fails closed with typed evidence. Developer owns; Acceptance executes. TEST-VERIFIABLE.
        - A controlled integration test pauses one writer after it loads the old goal, applies an insertion through the other writer, then lets the stale writer commit. The inserted task remains and no downstream dispatch occurs. The test repeats with a conductor restart between enqueue and apply. The test name appears in the verification receipt, and the test fails on the pre-change code. Tester owns; Acceptance executes. TEST-VERIFIABLE.
        - The change adds no write path for the goal task list outside the conductor tick for the new verb, and the pre-existing merge behavior for tasks known to both writers is unchanged. The Reviewer inspects the diff against `src/Mcg.AgentOrchestrator.Execution/Persistence/SqliteOrchestratorStateRepository.cs` and `src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs` and records the findings. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.
        - Live proof: after the change lands and the conductor is running on the new build, the operator repairs a two-role goal to Researcher, Planner, Developer, Tester, Reviewer using the repair form while the loop runs. Exactly those five tasks persist, and only Researcher dispatches first. The operator records the goal events and the intent outcome as evidence. Operator owns; Operator executes. REAL-WORLD-DEPENDENT.


        """;
}
