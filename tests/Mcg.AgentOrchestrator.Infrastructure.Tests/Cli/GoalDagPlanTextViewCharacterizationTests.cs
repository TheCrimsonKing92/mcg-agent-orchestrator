using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
// Expected text follows ConsoleViews.GoalDagPlan.cs at base 8711cbf2f.
public sealed class GoalDagPlanTextViewCharacterizationTests
{
    [Fact]
    public void Print_NoNodes_PreservesExactText()
    {
        var plan = new GoalDagPlan("Inspect CLI", [], []);

        var actual = AsyncLocalConsoleRouter.Capture(() => GoalDagPlanTextView.PrintGoalDagPlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "DAG plan: 0 node(s) for direction: Inspect CLI",
            "",
            "Confirm: plan <direction> --confirm-plan | plan --text-file <path> --confirm-plan",
            ""), actual);
    }

    [Fact]
    public void Print_DependenciesAndValidationErrors_PreservesExactText()
    {
        var plan = new GoalDagPlan("Update CLI",
            [new("first", "Add view", []), new("second", "Wire view", ["first", "missing"])],
            ["Unknown node: missing", "Fix dependencies"]);

        var actual = AsyncLocalConsoleRouter.Capture(() => GoalDagPlanTextView.PrintGoalDagPlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "DAG plan: 2 node(s) for direction: Update CLI",
            "",
            "  first: Add view",
            "  Depends on: none",
            "",
            "  second: Wire view",
            "  Depends on: first, missing",
            "",
            "Dependency edges:",
            "  second -> first",
            "  second -> missing",
            "",
            "Validation errors:",
            "  ERROR: Unknown node: missing",
            "  ERROR: Fix dependencies",
            "",
            "Fix the direction and re-run to preview before confirming.",
            ""), actual);
    }

    [Fact]
    public void Print_DormantSliceBatch_PreservesExactText()
    {
        var plan = new GoalDagPlan("Update CLI", [new("first", "Add view", [])], []);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalDagPlanTextView.PrintGoalDagPlan(plan, sliceBatch: true));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Dormant slice-batch intake preview: 1 child node(s) for direction: Update CLI",
            "",
            "  first: Add view",
            "  Depends on: none",
            "",
            "Confirm dormant intake: plan <direction> --slice-batch --confirm-plan | plan --text-file <path> --slice-batch --confirm-plan",
            ""), actual);
    }
}
