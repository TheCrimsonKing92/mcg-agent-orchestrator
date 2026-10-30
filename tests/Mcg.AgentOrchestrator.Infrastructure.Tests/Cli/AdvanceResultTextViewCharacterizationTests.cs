using System.Globalization;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local fixtures.
// Expected strings derive from ConsoleViews.Advance.cs at base
// 7bf9f91fe45d5b90445139295dda786e4392d443 (before extraction).
public sealed class AdvanceResultTextViewCharacterizationTests
{
    [Fact]
    public void Print_NotExecutedWithoutAction_PreservesExactText()
    {
        var goal = CreateGoal();
        var result = new GoalAdvanceOutcome(goal.Id, false, null,
            NextActionAutomationKind.None, "No available step", new GoalAdvanceNoPayload());

        var actual = Capture(() => AdvanceResultTextView.Print(goal, result));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Cannot advance goal abcdef01: No available step", "", ""), actual);
    }

    [Fact]
    public void Print_ExecutedWithAction_PreservesExactText()
    {
        var goal = CreateGoal();
        var action = new NextActionItem(NextActionKind.MonitorGoal, null, null, "Inspect progress");
        var result = new GoalAdvanceOutcome(goal.Id, true, action,
            NextActionAutomationKind.None, "Step finished", new GoalAdvanceNoPayload());

        var actual = Capture(() => AdvanceResultTextView.Print(goal, result));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Advanced goal abcdef01: Step finished",
            "Next action: MonitorGoal - Inspect progress", "Command: monitor", "", ""), actual);
    }

    [Fact]
    public void PrintRunGoalResult_NoStepsOrOptionalEvidence_PreservesExactText()
    {
        var result = new RunGoalService.RunGoalResult(false, "Nothing runnable", null, [], null);

        var actual = Capture(() => AdvanceResultTextView.PrintRunGoalResult(CreateGoal(), result));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Goal abcdef01: no steps executed. Nothing runnable", "", ""), actual);
    }

    [Fact]
    public void PrintRunGoalResult_TasksBlockerAndRetry_PreservesExactText()
    {
        var result = new RunGoalService.RunGoalResult(true, "Stopped for review",
            new NextActionItem(NextActionKind.MonitorGoal, null, null, "Review evidence"),
            [new(1, "12345678-rest", "Implement", true, "Build passed"),
             new(2, "87654321-rest", "Verify", false, " ")],
            new(2, "87654321-rest", "Needs attention", "failure\nlast line"),
            new DateTimeOffset(2026, 10, 30, 8, 5, 0, TimeSpan.Zero));

        var actual = Capture(() => AdvanceResultTextView.PrintRunGoalResult(CreateGoal(), result));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Goal abcdef01: 2 task(s) transitioned. Stopped for review",
            "  Task 1 12345678: succeeded - Implement", "    tail: Build passed",
            "  Task 2 87654321: failed - Verify",
            "Blocked: MonitorGoal - Review evidence", "Command: monitor",
            "Stop evidence: task 2 87654321 - Needs attention",
            "Output tail: failure\nlast line",
            "Retry after: 2026-10-30 08:05:00Z", "", ""), actual);
    }

    [Fact]
    public void PrintRunGoalResult_GoalStopWithoutTail_PreservesExactText()
    {
        var result = new RunGoalService.RunGoalResult(false, "Goal held", null, [],
            new(null, null, "Approval required", " "));

        var actual = Capture(() => AdvanceResultTextView.PrintRunGoalResult(CreateGoal(), result));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Goal abcdef01: no steps executed. Goal held",
            "Stop evidence: goal - Approval required", "", ""), actual);
    }

    private static Goal CreateGoal() => new(new GoalId("abcdef01-rest"), "Advance sample",
        [new(new TaskId("12345678-rest"), "Implement", AgentRole.Developer)]);

    private static string Capture(Action print)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return AsyncLocalConsoleRouter.Capture(print);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
