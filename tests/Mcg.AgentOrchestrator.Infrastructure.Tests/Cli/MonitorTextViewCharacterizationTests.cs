using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture and local fixtures with fixed timestamps.
// Every expected string is derived from ConsoleViews.Monitoring.cs at base fd85596fc.
// Blocker commands additionally follow VerificationCommandAdvice at that same base.
public sealed class MonitorTextViewCharacterizationTests
{
    [Fact]
    public void Print_NoTasksOrAttention_PreservesExactText()
    {
        var monitor = new GoalMonitor(new("aaaaaaaa00000000"), "Watch the goal", GoalStatus.Draft,
            0, [], 0, [], null);

        var actual = AsyncLocalConsoleRouter.Capture(() => MonitorTextView.Print(monitor));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal aaaaaaaa Draft: Watch the goal",
            "Tasks: 0",
            "Status counts:",
            "Pending human input: 0",
            "Attention:",
            "  none",
            "", ""), actual);
    }

    [Fact]
    public void Print_StatusOverrideAndAttention_PreservesExactText()
    {
        var monitor = new GoalMonitor(new("aaaaaaaa00000000"), "Watch the goal", GoalStatus.Active,
            3, [new(WorkTaskStatus.Completed, 1), new(WorkTaskStatus.Failed, 2)], 1,
            [new(TaskAttentionKind.PendingHumanInput, null, "Choose a path"),
             new(TaskAttentionKind.FailedTask, new TaskId("bbbbbbbb00000000"), "Build failed")],
            new DateTimeOffset(2026, 10, 30, 12, 34, 56, TimeSpan.Zero));

        var actual = AsyncLocalConsoleRouter.Capture(() => MonitorTextView.Print(monitor, "custom status"));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal aaaaaaaa custom status: Watch the goal",
            "Tasks: 3",
            "Status counts:",
            "  Completed: 1",
            "  Failed: 2",
            "Pending human input: 1",
            "Last event: 2026-10-30 12:34:56Z",
            "Attention:",
            "  PendingHumanInput goal: Choose a path",
            "  FailedTask bbbbbbbb: Build failed",
            "", ""), actual);
    }

    [Fact]
    public void PrintAcceptanceSummary_AcceptedWithoutBlockers_PreservesExactText()
    {
        var goal = CreateGoal();
        var summary = new GoalAcceptanceSummary(goal.Id, goal.Objective, GoalStatus.Completed,
            true, 2, 2, 0, 0, [], []);

        var actual = AsyncLocalConsoleRouter.Capture(() => MonitorTextView.PrintAcceptanceSummary(goal, summary));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal aaaaaaaa acceptance: accepted",
            "Objective: Extract monitor output",
            "Status: Completed",
            "Tasks passed: 2/2",
            "Open verification: 0",
            "Pending human input: 0",
            "Blockers: none",
            "", ""), actual);
    }

    [Fact]
    public void PrintAcceptanceSummary_BlockersAndOutcomes_PreservesExactText()
    {
        var goal = CreateGoal();
        var summary = new GoalAcceptanceSummary(goal.Id, goal.Objective, GoalStatus.Verifying,
            false, 2, 1, 1, 1,
            [new(GoalAcceptanceBlockerKind.PendingHumanInput, null, new("cccccccc00000000"),
                "Choose a path", "Answer the question"),
             new(GoalAcceptanceBlockerKind.VerificationMissing, goal.Tasks[1].Id, null,
                "Verification missing", "Record evidence")],
            [new("failed", false, DateTimeOffset.MinValue, "Prior candidate failed"),
             new("aborted", true, DateTimeOffset.MinValue, "Current candidate aborted")]);

        var actual = AsyncLocalConsoleRouter.Capture(() => MonitorTextView.PrintAcceptanceSummary(goal, summary));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal aaaaaaaa acceptance: not accepted",
            "Objective: Extract monitor output",
            "Status: Verifying",
            "Tasks passed: 1/2",
            "Open verification: 1",
            "Pending human input: 1",
            "Blockers:",
            "  PendingHumanInput (goal): Choose a path",
            "     action: Answer the question",
            "     command: answer cccccccc <answer>",
            "  VerificationMissing (task 2): Verification missing",
            "     action: Record evidence",
            "     command: verify 2 <command> | verify-manual 2 passed <note>",
            "Acceptance outcomes:",
            "  historical: Prior candidate failed",
            "  current: Current candidate aborted",
            "", ""), actual);
    }

    private static Goal CreateGoal() => new(new GoalId("aaaaaaaa00000000"), "Extract monitor output",
        [new TaskSpec(new TaskId("bbbbbbbb00000000"), "Move printers", AgentRole.Developer),
         new TaskSpec(new TaskId("dddddddd00000000"), "Characterize printers", AgentRole.Tester)]);
}
