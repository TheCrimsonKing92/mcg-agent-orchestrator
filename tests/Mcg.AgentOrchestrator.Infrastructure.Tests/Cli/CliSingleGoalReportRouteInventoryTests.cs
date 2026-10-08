using Mcg.AgentOrchestrator.App.Cli;

// Argument classification is parallel-safe and uses no shared state.
public sealed class CliSingleGoalReportRouteInventoryTests
{
    [Xunit.Theory]
    [Xunit.InlineData(true, "status")]
    [Xunit.InlineData(true, "monitor")]
    [Xunit.InlineData(true, "evidence")]
    [Xunit.InlineData(true, "stages")]
    [Xunit.InlineData(true, "gates")]
    [Xunit.InlineData(true, "verify-needed")]
    [Xunit.InlineData(true, "input-needed")]
    [Xunit.InlineData(true, "goal-diagnostics")]
    [Xunit.InlineData(true, "subscription-plan")]
    [Xunit.InlineData(true, "goal-changes")]
    [Xunit.InlineData(true, "retention-plan")]
    [Xunit.InlineData(true, "supervisor")]
    [Xunit.InlineData(true, "goal-timing")]
    [Xunit.InlineData(true, "failure-triage")]
    [Xunit.InlineData(true, "readiness")]
    [Xunit.InlineData(false, "readiness-repair")]
    [Xunit.InlineData(true, "next")]
    [Xunit.InlineData(false, "goal-recovery")]
    [Xunit.InlineData(false, "dogfood-eval")]
    [Xunit.InlineData(false, "build-lease-cleanup")]
    public void ExplicitPrefix_RouteMatchesInventoryAndRemainsSingleGoal(bool readOnly, string verb)
    {
        string[] args = [verb, "abc10000"];
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args) == readOnly,
            $"Single-goal report '{verb}' must have read-only classification {readOnly}.");
        Xunit.Assert.True(
            CliPersistentStateRunner.IsSingleGoalReportCommand(args) ||
            CliPersistentStateRunner.IsSingleGoalSnapshotCommand(args),
            $"'{verb}' must remain a single-goal report or snapshot command.");
    }
}
