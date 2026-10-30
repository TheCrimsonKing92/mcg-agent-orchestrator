using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
public sealed class GoalRollbackPlanTextViewCharacterizationTests
{
    [Fact]
    public void Print_ApplicableDryRun_PreservesExactText()
    {
        var plan = new GoalRollbackPlan(new GoalId("abcdef123456"), "abcdef12",
            "Undo accepted change.", DryRun: true, CanApply: true,
            "rollback/abcdef12", "Acceptance range found.", "goal-rollback abcdef12 --apply");

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalRollbackPlanTextView.PrintGoalRollbackPlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "Goal rollback abcdef12: Undo accepted change.",
            "Dry run: True",
            "Can apply: True",
            "Rollback branch: rollback/abcdef12",
            "Detail: Acceptance range found.",
            "Command: goal-rollback abcdef12 --apply",
            ""), actual);
    }

    [Fact]
    public void Print_NoSuggestedCommand_OmitsCommandLine()
    {
        var plan = new GoalRollbackPlan(new GoalId("0123456789ab"), "01234567",
            "No acceptance range.", DryRun: false, CanApply: false,
            "", "Nothing to roll back.", SuggestedCommand: null);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalRollbackPlanTextView.PrintGoalRollbackPlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "Goal rollback 01234567: No acceptance range.",
            "Dry run: False",
            "Can apply: False",
            "Rollback branch: ",
            "Detail: Nothing to roll back.",
            ""), actual);
    }
}
