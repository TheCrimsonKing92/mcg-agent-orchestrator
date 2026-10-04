using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure criterion strings and immutable detector data, with no external resources.
public sealed class AcceptanceCriterionExecutingRoleFeasibilityTests
{
    // Verbatim b0e18659 fixtures supplied by HumanInputReceived in goal/timeline.json,
    // answer 9ebf332f7cf24dc996ba60a01d2544e4, topics 0d8fe45d51913385 and 4561446b36d9c556.
    private const string CensusCriterion =
        "A test calls `ConductorBatchLoop.BuildLiveAcceptanceCensus` with one active cohort root (two member goal ids) and four running gate occupants that carry that cohort's run identity, no goal id and distinct process ids, then calls `DecideLiveAcceptanceAdmission` with width 2. It asserts the census has exactly one occupant and the decision admits. It fails against main 76026151c, where the census has five occupants and the decision holds. Developer owns; Acceptance executes. TEST-VERIFIABLE.";

    private const string DiffReadCriterion =
        "The Reviewer confirms by reading the diff that the census skips only heartbeats whose run identity matches an active attempt id or an active cohort or train root key, that identity-less unclaimed heartbeats are still counted, that the run identity written for grouped gates equals the conductor's cohort or train root key, that evidence heartbeats stay excluded, that no test decides pass or fail on elapsed real time, and that every touched guarded file stays within its SourceSizeRatchet ceiling. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.";

    [Xunit.Theory]
    [Xunit.InlineData(CensusCriterion)]
    [Xunit.InlineData(DiffReadCriterion)]
    public void Evaluate_B0e18659FalsePositivePair_ReturnsNoFindings(string criterion)
    {
        Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate(
            [criterion], AgentRole.Developer));
    }

    [Xunit.Theory]
    [Xunit.InlineData(
        "Drive two concurrent goals through dispatch and compare their results.",
        "multiple-or-concurrent-goals")]
    [Xunit.InlineData(
        "Run the real conductor loop across multiple conductor ticks and observe recovery.",
        "live-conductor-or-multiple-ticks")]
    [Xunit.InlineData("Drive the live conductor and observe recovery.", "live-conductor-or-multiple-ticks")]
    [Xunit.InlineData("Run the real conductor loop.", "live-conductor-or-multiple-ticks")]
    [Xunit.InlineData("Run the real conductor loops and assert recovery.", "live-conductor-or-multiple-ticks")]
    [Xunit.InlineData("Run the real conductor looping and assert recovery.", "live-conductor-or-multiple-ticks")]
    [Xunit.InlineData("Run two concurrent goals.", "multiple-or-concurrent-goals")]
    [Xunit.InlineData("Two concurrent goals run through dispatch.", "multiple-or-concurrent-goals")]
    [Xunit.InlineData(
        "Add tests that run two concurrent goals and assert both finish.",
        "multiple-or-concurrent-goals")]
    [Xunit.InlineData(
        "Two concurrent goals have tests that run to completion.",
        "multiple-or-concurrent-goals")]
    [Xunit.InlineData(
        "Add tests that run the real conductor loop and assert recovery.",
        "live-conductor-or-multiple-ticks")]
    public void Evaluate_PositivePair_StillFlagsExpectedCategory(string text, string category)
    {
        // A declared executor overrides even a fallback with no capability profile.
        foreach (var fallbackRole in new[] { AgentRole.Developer, AgentRole.Reviewer })
        {
            var finding = Xunit.Assert.Single(AcceptanceCriterionFeasibility.Evaluate(
                [$"{text} Developer owns; Developer executes."], fallbackRole));

            Xunit.Assert.Equal([category], finding.TriggerCategories);
            Xunit.Assert.Equal("Developer", finding.Role);
        }
    }

    [Xunit.Fact]
    public void Evaluate_ExecutingRole_SelectsProfileAndNamesAcceptance()
    {
        const string text = "Drive two concurrent goals through dispatch and compare their results.";
        var reviewerCriterion = $"{text} Reviewer owns; Reviewer executes.";
        var acceptanceCriterion = $"{text} Developer owns; Acceptance executes.";

        Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate(
            [reviewerCriterion], AgentRole.Developer));
        var finding = Xunit.Assert.Single(AcceptanceCriterionFeasibility.Evaluate(
            [acceptanceCriterion], AgentRole.Developer));

        Xunit.Assert.Equal("Acceptance", finding.Role);
        Xunit.Assert.StartsWith("Criterion infeasible for Acceptance:",
            AcceptanceCriterionFeasibility.BuildQuestion(finding), StringComparison.Ordinal);
        Xunit.Assert.Equal(acceptanceCriterion, finding.Criterion);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Tester")]
    [Xunit.InlineData("Operator")]
    [Xunit.InlineData("Unknown")]
    public void Evaluate_UnprofiledExecutor_ReturnsNoFindings(string executor)
    {
        Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate(
            [$"Drive two concurrent goals. Developer owns; {executor} executes."], AgentRole.Developer));
    }

    [Xunit.Fact]
    public void Evaluate_MixedOwnership_UsesEachCriteriaLastExecutor()
    {
        const string reviewer = "Drive two concurrent goals. Reviewer owns; Reviewer executes.";
        const string acceptance =
            "Drive two concurrent goals. Reviewer owns; Reviewer executes. developer owns; acceptance executes.";
        const string fallback = "Drive two concurrent goals.";

        var findings = AcceptanceCriterionFeasibility.Evaluate(
            [reviewer, acceptance, fallback], AgentRole.Developer);

        Xunit.Assert.Equal(2, findings.Count);
        Xunit.Assert.Equal(acceptance, findings[0].Criterion);
        Xunit.Assert.Equal("Acceptance", findings[0].Role);
        Xunit.Assert.Equal(fallback, findings[1].Criterion);
        Xunit.Assert.Equal("Developer", findings[1].Role);
    }

    // Synthetic cases isolate noun exclusions under a profiled executor in both pattern directions.
    [Xunit.Theory]
    [Xunit.InlineData("the")]
    [Xunit.InlineData("a")]
    [Xunit.InlineData("an")]
    [Xunit.InlineData("each")]
    [Xunit.InlineData("every")]
    [Xunit.InlineData("this")]
    [Xunit.InlineData("its")]
    [Xunit.InlineData("their")]
    [Xunit.InlineData("that cohort's")]
    [Xunit.InlineData("that cohort’s")]
    public void Evaluate_RunNounPrefix_ReturnsNoFindings(string prefix)
    {
        var criteria = new[]
        {
            $"{prefix} run of two concurrent goals has stored results.",
            $"Two concurrent goals carry {prefix} run.",
            $"{prefix} run of the real conductor loop has stored results."
        };

        foreach (var criterion in criteria)
            Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate(
                [$"{criterion} Developer owns; Developer executes."], AgentRole.Developer));
    }

    [Xunit.Theory]
    [Xunit.InlineData("identity")]
    [Xunit.InlineData("identities")]
    [Xunit.InlineData("id")]
    [Xunit.InlineData("ids")]
    [Xunit.InlineData("key")]
    [Xunit.InlineData("record")]
    public void Evaluate_RunNounSuffix_ReturnsNoFindings(string suffix)
    {
        var criteria = new[]
        {
            $"Run {suffix} for two concurrent goals is stored.",
            $"Two concurrent goals carry run {suffix}.",
            $"Run {suffix} for the real conductor loop is stored."
        };

        foreach (var criterion in criteria)
            Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate(
                [$"{criterion} Developer owns; Developer executes."], AgentRole.Developer));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Observe that the conductor uses the cohort run key.")]
    [Xunit.InlineData("Observe that the conductor loop uses the cohort run key.")]
    [Xunit.InlineData("Observe that the conductor writes the cohort run key.")]
    [Xunit.InlineData("Observe the conductor's key.")]
    [Xunit.InlineData("Observe the conductor’s key.")]
    [Xunit.InlineData("Observe the conductor loop's key.")]
    [Xunit.InlineData("Compare the run identity written for grouped gates with the conductor's key.")]
    public void Evaluate_ConductorSubjectOrPossessive_ReturnsNoFindings(string criterion)
    {
        Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate(
            [$"{criterion} Developer owns; Developer executes."], AgentRole.Developer));
    }
}
