using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local snapshots.
// Expected strings derive from ConsoleViews.Configuration.cs at base 618b883c4.
public sealed class GoalListTextViewCharacterizationTests
{
    [Fact]
    public void Print_EmptyKernel_PreservesExactText()
    {
        Assert.Equal(string.Empty, Capture(() => GoalListTextView.Print(new AgentOrchestratorKernel())));
    }

    [Fact]
    public void Print_KernelGoals_PreservesOrderingConditionsAndPriority()
    {
        var older = new GoalSnapshot("aaaaaaaa-old", "Normal objective", GoalStatus.Draft,
            [new("task-older", "Implement", AgentRole.Developer, WorkTaskStatus.Pending,
                null, null, null, null, null, null)],
            [new("aaaaaaaa-old", null, ProgressKind.GoalCreated, "Created", At(1))]);
        var newer = new GoalSnapshot("bbbbbbbb-new", "Critical objective", GoalStatus.Active,
            [new("task-failed", "Implement", AgentRole.Developer, WorkTaskStatus.Failed,
                null, null, null, null, null, null)],
            [new("bbbbbbbb-new", null, ProgressKind.GoalCreated, "Created", At(2))],
            Priority: "critical");
        var undated = new GoalSnapshot("cccccccc-none", "No timeline", GoalStatus.Draft,
            [new("task-undated", "Implement", AgentRole.Developer, WorkTaskStatus.Pending,
                null, null, null, null, null, null)], []);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new([older, undated, newer], []));

        var actual = Capture(() => GoalListTextView.Print(kernel));

        Assert.Equal(string.Join(Environment.NewLine,
            "bbbbbbbb Active [active-with-failed-task] [priority=critical]: Critical objective",
            "aaaaaaaa Draft: Normal objective",
            "cccccccc Draft: No timeline", ""), actual);
    }

    [Fact]
    public void Print_EmptySummaries_PreservesExactText()
    {
        Assert.Equal(string.Empty, Capture(() => GoalListTextView.Print(Array.Empty<GoalSummary>())));
    }

    [Fact]
    public void Print_Summaries_PreservesInputOrderShortIdsAndConditions()
    {
        GoalSummary[] goals =
        [
            new("short", "Draft", "Short objective", "2026-10-30", Condition: " "),
            new("bbbbbbbb-rest", "Active", "Failed task objective", "2026-10-29",
                Condition: "active-with-failed-task"),
            new("aaaaaaaa-rest", "Completed", "Finished objective", "2026-10-31")
        ];

        var actual = Capture(() => GoalListTextView.Print(goals));

        Assert.Equal(string.Join(Environment.NewLine,
            "short Draft: Short objective",
            "bbbbbbbb Active [active-with-failed-task]: Failed task objective",
            "aaaaaaaa Completed: Finished objective", ""), actual);
    }

    [Fact]
    public void PrintCleanupDebtWarning_Empty_PreservesSilence()
    {
        Assert.Equal(string.Empty, Capture(() => GoalListTextView.PrintCleanupDebtWarning([])));
    }

    [Fact]
    public void PrintCleanupDebtWarning_PendingOnly_PreservesSilence()
    {
        Assert.Equal(string.Empty, Capture(() => GoalListTextView.PrintCleanupDebtWarning([Debt(null)])));
    }

    [Fact]
    public void PrintCleanupDebtWarning_MixedDebt_PreservesCountsAndExactText()
    {
        var actual = Capture(() => GoalListTextView.PrintCleanupDebtWarning(
            [Debt(At(1)), Debt(null), Debt(At(2))]));

        Assert.Equal("WARNING: stale worktree cleanup escalated=2 pending=3; run cleanup-status."
            + Environment.NewLine, actual);
    }

    private static DateTimeOffset At(int day) => new(2026, 10, day, 8, 0, 0, TimeSpan.Zero);

    private static GoalWorktreeCleanupDebt Debt(DateTimeOffset? escalated) =>
        new("worktree", "locked", "remove", At(1), At(2), At(3),
            TimeSpan.FromDays(1), TimeSpan.FromDays(1), 2, escalated);

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
