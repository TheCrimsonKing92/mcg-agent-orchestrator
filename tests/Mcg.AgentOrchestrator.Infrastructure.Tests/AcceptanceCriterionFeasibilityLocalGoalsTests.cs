using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure strings and immutable rule metadata; no external resources.
public sealed class AcceptanceCriterionFeasibilityLocalGoalsTests
{
    private const string Category = "multiple-or-concurrent-goals";

    // Verbatim criterion 7 from goal 26510586, supplied in this goal's objective.
    internal const string HistoricalCriterion = """
        Wiring and baseline-equality fixture in `CohortAcceptanceEvidenceRetentionTests` through `RetentionReclaimFixture.Run`: two fixtures with the same goals, one also holding the criterion 1 store and artifacts, produce equal `WorkerArtifactsDeleted`, `WorkerLogsCompressed`, `SuccessfulTrxReceiptsWritten`, `AcceptanceArtifactsDeleted`, `PromptArtifactsDeleted` and `GoalJournalsArchived` values and equal non-`CohortAcceptanceEvidence` decision lists (compared by family, action, path relative to the fixture root, and reason); the second fixture alone carries the two `CohortAcceptanceEvidence` decisions of criterion 1 and its two TRX copies are gone. Developer owns; Acceptance executes. TEST-VERIFIABLE.
        """;

    [Xunit.Theory]
    [Xunit.InlineData("Acceptance")]
    [Xunit.InlineData("Developer")]
    public void HistoricalCriterion_IsFeasibleForDeclaredExecutor(string executor)
    {
        var criterion = HistoricalCriterion.Replace(
            "Developer owns; Acceptance executes", $"Developer owns; {executor} executes",
            StringComparison.Ordinal);

        // Disabling markers preserves the exact pre-change demand branch, including method Run.
        AssertCoordinationFinding(criterion, executor, honorMarkers: false);
        Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate([criterion], AgentRole.Reviewer));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Run two goals inside an isolated in-process boundary using constructed Goal values.")]
    [Xunit.InlineData("Compare several goals in an in-process fixture using synthetic GoalId values.")]
    [Xunit.InlineData("Call `GoalFixture.Run`: compare two constructed `Goal` values.")]
    [Xunit.InlineData("Call `GoalFixture.Run`: compare several goals using synthetic GoalId values.")]
    [Xunit.InlineData("Compare two local `goals` using `GoalFixture.Compare`.")]
    [Xunit.InlineData("Call `GoalFixture.Run`: two fixtures with the same goal records produce equal results.")]
    public void LocalOperands_AreFeasibleButStillMatchDemandWithoutMarkers(string criterion)
    {
        AssertCoordinationFinding(criterion, "Developer", honorMarkers: false);
        Xunit.Assert.Empty(AcceptanceCriterionFeasibility.Evaluate([criterion], AgentRole.Developer));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Run two concurrent goals.")]
    [Xunit.InlineData("Test fixture: drive two concurrent goals.")]
    [Xunit.InlineData("Call `GoalFixture.Run`: compare two goals.")]
    [Xunit.InlineData("Compare several goals using synthetic GoalId values.")]
    [Xunit.InlineData("Drive two concurrent goals through dispatch and compare their results.")]
    [Xunit.InlineData("Start multiple live goals.")]
    [Xunit.InlineData("Compare execution of several goals on a live board.")]
    public void UnspecifiedLocalBoundary_RequiresCoordination(string criterion)
    {
        AssertCoordinationFinding(criterion, "Developer", honorMarkers: true);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Drive two concurrent goals through dispatch.")]
    [Xunit.InlineData("Start multiple live goals.")]
    [Xunit.InlineData("Observe two running goals.")]
    [Xunit.InlineData("Execute several production goals.")]
    [Xunit.InlineData("Compare execution of several goals on a live board.")]
    [Xunit.InlineData("Drive two concurrent goals using the dispatcher.")]
    [Xunit.InlineData("Compare several goals on the live-board.")]
    public void LiveDemand_OverridesStrongLocalEvidenceInEitherOrder(string liveDemand)
    {
        const string local = "Call `GoalFixture.Run`: compare two constructed Goal values in an isolated in-process fixture.";
        foreach (var text in new[] { local + " " + liveDemand, liveDemand + " " + local })
        {
            foreach (var executor in new[] { "Developer", "Acceptance" })
                AssertCoordinationFinding($"{text} Developer owns; {executor} executes.", executor, honorMarkers: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("Run the real conductor loop.", "live-conductor-or-multiple-ticks", (int)WorkerCapability.LiveConductorControl, true)]
    [Xunit.InlineData("Benchmark wall-clock performance on this host.", "whole-host-or-wall-clock-performance", (int)WorkerCapability.HostPerformanceObservation, false)]
    [Xunit.InlineData("Reserve an acceptance slot.", "worker-sandbox-forbidden-operation", (int)WorkerCapability.SandboxForbiddenOperation, false)]
    public void LocalGoalData_PreservesIndependentCapabilityFindings(
        string independentDemand, string category, int capability, bool alsoCoordinates)
    {
        var text = $"Call `GoalFixture.Run`: compare two constructed goals. {independentDemand}";
        foreach (var executor in new[] { "Developer", "Acceptance" })
        {
            var finding = Xunit.Assert.Single(AcceptanceCriterionFeasibility.Evaluate(
                [$"{text} Developer owns; {executor} executes."], AgentRole.Reviewer));

            Xunit.Assert.Equal(executor, finding.Role);
            Xunit.Assert.Equal(alsoCoordinates ? [Category, category] : new[] { category }, finding.TriggerCategories);
            Xunit.Assert.Equal(alsoCoordinates
                ? [WorkerCapability.MultipleGoalCoordination, (WorkerCapability)capability]
                : new[] { (WorkerCapability)capability }, finding.MissingCapabilities);
        }
    }

    private static void AssertCoordinationFinding(string criterion, string role, bool honorMarkers)
    {
        var finding = Xunit.Assert.Single(AcceptanceCriterionFeasibility.Evaluate(
            [criterion], AgentRole.Developer, honorInProcessMarkers: honorMarkers));

        Xunit.Assert.Equal(criterion, finding.Criterion);
        Xunit.Assert.Equal(role, finding.Role);
        Xunit.Assert.Equal([Category], finding.TriggerCategories);
        Xunit.Assert.Equal([WorkerCapability.MultipleGoalCoordination], finding.MissingCapabilities);
    }
}
