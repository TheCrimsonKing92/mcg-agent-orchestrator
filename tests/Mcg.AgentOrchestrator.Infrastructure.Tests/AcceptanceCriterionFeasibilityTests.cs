using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceCriterionFeasibilityTests
{
    // The persisted cd8ebb40 RefinedSpec artifact is not present in this worktree. This fixture is
    // intentionally labelled synthetic and is not represented as the historical criterion text.
    private const string SyntheticMakespanCriterion =
        "Measure before/after gate makespan on this host by driving two concurrent gates through the real conductor while the machine is otherwise idle.";

    [Xunit.Fact]
    public void Evaluate_SyntheticMakespanCriterion_ReturnsOneFinding()
    {
        var finding = Xunit.Assert.Single(AcceptanceCriterionFeasibility.Evaluate(
            [SyntheticMakespanCriterion],
            AgentRole.Developer));

        Xunit.Assert.Equal(SyntheticMakespanCriterion, finding.Criterion);
        Xunit.Assert.Contains("whole-host-or-wall-clock-performance", finding.TriggerCategories);
    }

    [Xunit.Fact]
    public void Evaluate_NormalCriteria_ReturnsNoFindings()
    {
        var criteria = new[]
        {
            "The goal status endpoint returns the stored state.",
            "The host configuration parser rejects an empty value.",
            "The event DTO includes the current tick number.",
            "Focused tests cover accepted and rejected input.",
            "The goal summary shows the host label and latest tick number."
        };

        Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate(criteria, AgentRole.Developer));
    }

    [Xunit.Theory]
    [Xunit.InlineData(
        "Drive two concurrent goals through dispatch and compare their results.",
        "multiple-or-concurrent-goals")]
    [Xunit.InlineData(
        "Demonstrate the recovery behavior across multiple conductor ticks.",
        "live-conductor-or-multiple-ticks")]
    [Xunit.InlineData(
        "Benchmark wall-clock performance on this host while the machine is idle.",
        "whole-host-or-wall-clock-performance")]
    [Xunit.InlineData(
        "Reserve an acceptance slot and hold it until the competing build completes.",
        "worker-sandbox-forbidden-operation")]
    public void Evaluate_CapabilityDemand_FlagsExpectedCategory(
        string criterion,
        string category)
    {
        var finding = Xunit.Assert.Single(AcceptanceCriterionFeasibility.Evaluate(
            [criterion],
            AgentRole.Developer));

        Xunit.Assert.Contains(category, finding.TriggerCategories);
    }

    [Xunit.Fact]
    public void Evaluate_RedCrossTickTestDemand_FlagsMultipleTicks()
    {
        const string criterion =
            "Show the new test RED without its fix while exercising recovery across two ticks.";

        var finding = Xunit.Assert.Single(AcceptanceCriterionFeasibility.Evaluate(
            [criterion],
            AgentRole.Developer));

        Xunit.Assert.Contains("live-conductor-or-multiple-ticks", finding.TriggerCategories);
        Xunit.Assert.Contains("worker-sandbox-forbidden-operation", finding.TriggerCategories);
    }

    [Xunit.Fact]
    public void BuildQuestion_PresentsExactlyThreeDispositions()
    {
        var finding = Xunit.Assert.Single(AcceptanceCriterionFeasibility.Evaluate(
            [SyntheticMakespanCriterion],
            AgentRole.Developer));

        var question = AcceptanceCriterionFeasibility.BuildQuestion(finding);

        Xunit.Assert.Equal(1, Count(question, "1. Re-scope"));
        Xunit.Assert.Equal(1, Count(question, "2. Mark OPERATOR-OWNED and post-landing"));
        Xunit.Assert.Equal(1, Count(question, "3. Supply the reproducing scenario"));
        Xunit.Assert.Contains(SyntheticMakespanCriterion, question, StringComparison.Ordinal);
        Xunit.Assert.Contains("re-scope: <replacement criterion>", question, StringComparison.Ordinal);
        Xunit.Assert.Contains("supply-reproducing-scenario: <worker-accessible scenario>", question, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MeasurementForkAssociation_RequiresMoreThanOneSharedSignificantToken()
    {
        var finding = Xunit.Assert.Single(AcceptanceCriterionFeasibility.Evaluate(
            ["Drive two concurrent goals through dispatch and compare their results."],
            AgentRole.Developer));
        var unrelated = new SpecRefinementFork(
            "observable-behavior",
            "low",
            "high",
            "How should the goals list assert ordering?",
            "",
            "Ordering method is unspecified.",
            "goals-list-ordering");

        Xunit.Assert.False(AcceptanceCriterionFeasibility.IsMeasurementOrAssertionForkFor(
            unrelated,
            [finding]));
    }

    [Xunit.Theory]
    [Xunit.InlineData("re-scope: Assert the pure mapper output", FeasibilityDisposition.ReScope)]
    [Xunit.InlineData("OPERATOR-OWNED", FeasibilityDisposition.OperatorOwned)]
    [Xunit.InlineData("supply-reproducing-scenario: fixture scenario A", FeasibilityDisposition.ReproducingScenario)]
    public void TryParseDisposition_ClosedAnswer_Parses(string answer, string expectedKind)
    {
        var parsed = AcceptanceCriterionFeasibility.TryParseDisposition(answer, out var disposition);

        Xunit.Assert.True(parsed);
        Xunit.Assert.Equal(expectedKind, disposition.Kind);
    }

    [Xunit.Fact]
    public void TryParseDisposition_MeasurementAnswer_IsRejected()
    {
        Xunit.Assert.False(AcceptanceCriterionFeasibility.TryParseDisposition(
            "Measure it with two live conductor gates.",
            out _));
    }

    private static int Count(string value, string needle) =>
        value.Split(needle, StringSplitOptions.None).Length - 1;
}
